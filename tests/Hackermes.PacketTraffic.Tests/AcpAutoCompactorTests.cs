using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Pressure-triggered auto-compaction (deepseek-harness compaction-basic lineage):
/// threshold behavior, strict shrink guard, protection-aware landing through the
/// normal context_compress path, and disable/rate-limit switches.
/// </summary>
public sealed class AcpAutoCompactorTests
{
    private const int Budget = 10_000;

    private static AiSettings Settings(double ratio = 0.8) => new()
    {
        MaxContextCharacters = Budget,
        AutoCompactRatio = ratio,
        AcpEnabled = true,
        MemoryEnabled = false,
    };

    /// <summary>Six sizeable turns: total active chars cross the 80% pressure line.</summary>
    private static AcpContextStore CreatePressuredStore()
    {
        var store = new AcpContextStore(() => "system prompt", Budget);
        for (var index = 0; index < 6; index++)
        {
            var filler = new string((char)('a' + index), 1_450);
            if (index % 2 == 0) store.AppendUser($"问题 {index}: {filler}");
            else store.AppendAssistant($"回答 {index}: {filler}");
        }
        return store;
    }

    private sealed class StubClient(Func<IReadOnlyList<ChatMessage>, string>? responder = null) : IOpenAiChatClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, string> _responder =
            responder ?? (_ => "【目标】测试目标。【关键事实】保留路径 /etc/app。【错误与修复】无。【待办】继续验证。");

        public List<OpenAiChatRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            var reply = _responder(request.Messages);
            yield return new ChatStreamDelta(reply[..(reply.Length / 2)], null, null);
            yield return new ChatStreamDelta(reply[(reply.Length / 2)..], null, "stop");
        }
    }

    private static AcpAutoCompactor CreateCompactor(
        StubClient client,
        Func<AcpContextStore?> store,
        double ratio = 0.8,
        Func<AiSettings>? settings = null) =>
        new(client,
            () => "test-model",
            store,
            settings ?? (() => Settings(ratio)));

    [Fact]
    public async Task Pressure_crossing_summarizes_the_largest_safe_range_into_a_block()
    {
        var store = CreatePressuredStore();
        Assert.True(store.ActiveChars >= Budget * 0.8);
        var client = new StubClient();
        var compactor = CreateCompactor(client, () => store);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Automatic);
        Assert.True(result.ReclaimedChars > 0);
        Assert.Contains("[m00001]", result.Range, StringComparison.Ordinal);
        // Landed through the store's normal compression path: one active block, fewer active chars.
        Assert.Equal(1, store.Blocks.Count(block => block.Active));
        Assert.True(store.ActiveChars < Budget * 0.8);
        // Summarizer replayed the annotated range verbatim as its input.
        Assert.Single(client.Requests);
        var summarizerInput = string.Join("\n", client.Requests[0].Messages.Select(message => message.Content));
        Assert.Contains("[m00001]", summarizerInput, StringComparison.Ordinal);
        Assert.Contains("[m00002]", summarizerInput, StringComparison.Ordinal);
        Assert.DoesNotContain("回答 5", summarizerInput, StringComparison.Ordinal); // protected tail excluded
    }

    [Fact]
    public async Task Shrink_guard_rejects_a_summary_not_smaller_than_its_range()
    {
        var store = CreatePressuredStore();
        var bloated = new string('z', 20_000);
        var client = new StubClient(_ => bloated);
        var compactor = CreateCompactor(client, () => store);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(store.Blocks);
        Assert.Contains(store.ActiveEntries, entry => entry.Ref == "m00001"); // nothing was replaced
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Below_pressure_no_model_call_is_made()
    {
        var store = new AcpContextStore(() => "system", Budget);
        store.AppendUser("短消息。");
        store.AppendAssistant("短回复。");
        var client = new StubClient();
        var compactor = CreateCompactor(client, () => store);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(client.Requests);
        Assert.Empty(store.Blocks);
    }

    [Fact]
    public async Task Ratio_zero_disables_auto_compaction_entirely()
    {
        var store = CreatePressuredStore();
        var client = new StubClient();
        var compactor = CreateCompactor(client, () => store, ratio: 0);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(client.Requests);
        Assert.Empty(store.Blocks);
    }

    [Fact]
    public void ResolvePressureRatio_clamps_and_zero_disables()
    {
        var compactor = CreateCompactor(new StubClient(), () => null);
        Assert.Equal(0, compactor.ResolvePressureRatio(Settings(ratio: 0)));
        Assert.Equal(0.5, compactor.ResolvePressureRatio(Settings(ratio: 0.1)));
        Assert.Equal(0.95, compactor.ResolvePressureRatio(Settings(ratio: 0.99)));
        Assert.Equal(0.8, compactor.ResolvePressureRatio(Settings(ratio: 0.8)));
    }

    [Fact]
    public async Task Failed_compact_rate_limits_the_next_attempt()
    {
        var store = CreatePressuredStore();
        var bloated = new string('z', 20_000);
        var client = new StubClient(_ => bloated);
        var compactor = CreateCompactor(client, () => store);

        Assert.Null(await compactor.CompactIfNeededAsync(CancellationToken.None));
        Assert.Null(await compactor.CompactIfNeededAsync(CancellationToken.None));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Tool_schema_overhead_does_not_trigger_compaction_on_a_small_conversation()
    {
        var store = new AcpContextStore(() => "system prompt", Budget);
        store.AppendUser("短消息。");
        store.AppendAssistant("短回复。");
        var tools = Enumerable.Range(0, 30).Select(i => new AiToolDefinition(
            $"tool_{i}",
            new string('d', 800),
            JsonSerializer.SerializeToElement(new { type = "object", description = new string('x', 400) }),
            AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok()))).ToList();
        var prefix = new CompactionPrefix("主请求系统提示", tools);
        var client = new StubClient();
        var compactor = new AcpAutoCompactor(client, () => "test-model", () => store,
            () => Settings(), prefixProvider: () => prefix);

        Assert.True(store.PressureChars(prefix.SystemPrompt, prefix.Tools) >= Budget * 0.8);
        Assert.True(store.ActiveChars < Budget * 0.8);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Summarizer_keeps_head_tail_spill_locators_and_omits_tool_schemas()
    {
        const string spill = "spill:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var store = new AcpContextStore(() => "system prompt", Budget);
        var huge = "HEAD-UNIQUE-TOKEN" + new string('x', 7_000) + "MID-UNIQUE-TOKEN" + spill +
                   new string('y', 7_000) + "TAIL-UNIQUE-TOKEN";
        store.AppendUser("问题 0: " + huge);
        for (var index = 1; index < 6; index++)
        {
            var filler = new string((char)('a' + index), 1_450);
            if (index % 2 == 0) store.AppendUser($"问题 {index}: {filler}");
            else store.AppendAssistant($"回答 {index}: {filler}");
        }

        var prefix = new CompactionPrefix("主请求系统提示",
        [
            new AiToolDefinition("packet_query", "q",
                JsonSerializer.SerializeToElement(new { type = "object" }),
                AiToolRisk.ReadOnly,
                (_, _) => ValueTask.FromResult(ToolResult.Ok()))
        ]);
        var client = new StubClient();
        var compactor = new AcpAutoCompactor(client, () => "test-model", () => store,
            () => Settings(), prefixProvider: () => prefix);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.NotNull(result);
        var request = Assert.Single(client.Requests);
        Assert.Null(request.Tools);
        Assert.Equal("主请求系统提示", request.Messages[0].Content);
        var summarizerInput = string.Join("\n", request.Messages.Select(message => message.Content));
        Assert.Contains("HEAD-UNIQUE-TOKEN", summarizerInput, StringComparison.Ordinal);
        Assert.Contains("TAIL-UNIQUE-TOKEN", summarizerInput, StringComparison.Ordinal);
        Assert.Contains(spill, summarizerInput, StringComparison.Ordinal);
        Assert.DoesNotContain("MID-UNIQUE-TOKEN", summarizerInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summarizer_prefers_newest_range_entries_when_budget_is_tight()
    {
        var store = new AcpContextStore(() => "system prompt", Budget);
        for (var index = 0; index < 24; index++)
            store.AppendUser($"N{index:D2}-" + new string('z', 4_000));

        var suggestions = store.SuggestRanges(store.ActiveEntries);
        Assert.NotEmpty(suggestions);
        var target = suggestions[0];
        var client = new StubClient();
        var compactor = CreateCompactor(client, () => store);

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.NotNull(result);
        var summarizerInput = string.Join("\n", client.Requests[0].Messages.Select(message => message.Content));
        Assert.Contains($"[{target.EndRef}]", summarizerInput, StringComparison.Ordinal);
    }
}
