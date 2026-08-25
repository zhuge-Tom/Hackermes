using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// ACP（Active Context Pruning）存储的核心行为：模型驱动的区间压缩、工具调用对完整性、
/// 保护窗口、质量门提醒与 GC 兜底墓碑。机制参考 opencode-acp / pai-acp。
/// 小预算（4_000 / 1_200 字符）让保护窗口与建议逻辑在小夹具上即可触发。
/// </summary>
public sealed class AcpContextStoreTests
{
    private const int Budget = 4_000;

    private static AiSettings Settings(int budget) => new()
    {
        MaxContextCharacters = budget,
        MaxRecentMessages = 16,
        AcpEnabled = true
    };

    private static AcpContextStore NewStore(int budget = Budget) => new(() => "SYS", budget);

    private static string Long(int length, string marker)
    {
        var body = new string('x', Math.Max(0, length - marker.Length - 1));
        return body + " " + marker;
    }

    /// <summary>Seed: m1 user, m2 tool_call(c1), m3 tool result, then fillers.</summary>
    private static void SeedQueryTurn(AcpContextStore store, int fillerCount)
    {
        store.AppendUser("旧任务开始");
        store.AppendAssistantToolCalls(null, [new AssistantToolCall("c1", "packet_query", "{}")]);
        store.AppendToolResult("c1", Long(600, "EVIDENCE-TAIL"), "packet_query");
        for (var i = 0; i < fillerCount; i++) store.AppendAssistant(Long(90, $"filler-{i}"));
    }

    [Fact]
    public void Compress_then_decompress_roundtrip_restores_originals()
    {
        var store = NewStore();
        SeedQueryTurn(store, 5);

        var (ok, message) = store.Compress("m00002", "m00002", "查询了 packet_query，得到长输出；结论记录于后续 filler。", "查询阶段");
        Assert.True(ok, message);
        Assert.Equal(1, store.BlockCount);

        // Marker stays visible in the request; original tool output is gone.
        var request = store.BuildRequest(new AgentMemoryDocument(), [], Settings(Budget));
        Assert.Contains("系统存档 b1·T1",
            string.Join("\n", request.Where(m => m.Role == "user").Select(m => m.Content ?? "")),
            StringComparison.Ordinal);
        Assert.DoesNotContain("EVIDENCE-TAIL", string.Join("\n", request.Select(m => m.Content ?? "")), StringComparison.Ordinal);

        var (restored, restoreMessage) = store.Decompress("b1");
        Assert.True(restored, restoreMessage);
        Assert.Contains(store.ActiveEntries, entry =>
            entry.Message.Role == "tool" &&
            entry.Message.Content!.Contains("EVIDENCE-TAIL", StringComparison.Ordinal));
    }

    [Fact]
    public void Compress_range_ending_mid_tool_pair_snaps_to_whole_segment()
    {
        var store = NewStore();
        SeedQueryTurn(store, 6);

        // start 指向工具结果本身 —— 分段必须把发起调用的 assistant 一起带上，不能留下孤儿结果。
        var (ok, message) = store.Compress("m00003", "m00003", "调用意图与结果摘要。", null);
        Assert.True(ok, message);
        Assert.DoesNotContain(store.ActiveEntries, entry =>
            entry.Message.Role == "tool" &&
            string.Equals(entry.Message.ToolCallId, "c1", StringComparison.Ordinal));
    }

    [Fact]
    public void Protected_recent_window_rejects_range()
    {
        var store = NewStore();
        foreach (var i in Enumerable.Range(0, 8)) store.AppendUser($"消息-{i}");

        var (ok, message) = store.Compress("m00006", "m00008", "摘要", null);
        Assert.False(ok);
        Assert.Contains("受保护", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_bearing_compress_result_blocks_range_that_covers_it()
    {
        var store = NewStore();
        store.AppendUser("第一轮");
        store.AppendAssistantToolCalls(null, [new AssistantToolCall("k1", "context_compress", "{\"ranges\":[]}")]);
        store.AppendToolResult("k1", "已压缩 … 摘要记录", "context_compress");
        for (var i = 0; i < 6; i++) store.AppendAssistant(Long(90, $"filler-{i}"));

        var (ok, message) = store.Compress("m00003", "m00005", "试图吞掉压缩记录的区间", null);
        Assert.False(ok);
        Assert.Matches(@"\[m00003\].*context_compress 结果", message);
    }

    [Fact]
    public void T3_only_rewrite_is_rejected()
    {
        var store = NewStore();
        store.AppendUser("旧任务");
        store.AppendAssistant("阶段一");
        store.AppendAssistant("阶段二");
        store.AppendUser("新目标");
        store.AppendAssistant("f1");
        store.AppendAssistant("f2");
        store.AppendAssistant("f3");

        Assert.True(store.Compress("m00001", "m00003", "T1 摘要", "一阶").Ok);
        Assert.True(store.Compress("m00008", "m00008", "T2 提炼", "二阶").Ok);
        Assert.True(store.Compress("m00009", "m00009", "T3 浓缩", "三阶").Ok);

        var (ok, message) = store.Compress("m00010", "m00010", "再次浓缩 T3", null);
        Assert.False(ok);
        Assert.Contains("T3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_gate_warns_on_catastrophically_short_summary()
    {
        var store = NewStore();
        store.AppendUser(Long(4_000, "BIG-RANGE"));
        for (var i = 0; i < 6; i++) store.AppendAssistant(Long(90, $"filler-{i}"));
        store.AppendUser("新问题");
        store.AppendAssistant("回答");

        var (ok, message) = store.Compress("m00001", "m00001", "太短", null);
        Assert.True(ok, message);
        Assert.Contains("质量门提醒", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nudge_lists_suggestions_only_when_over_soft_budget()
    {
        var store = NewStore(2_500);
        store.AppendToolResult("t0", Long(700, "TOOL-BULK"), "scan_tool");
        for (var i = 0; i < 6; i++) store.AppendAssistant(Long(60, $"filler-{i}"));

        var hot = store.BuildRequest(new AgentMemoryDocument(), [], Settings(2_500));
        var system = hot[0].Content!;
        Assert.Contains("[ACP 上下文预算]", system, StringComparison.Ordinal);
        Assert.Contains("构成:", system, StringComparison.Ordinal);
        Assert.Contains("可压缩区间", system, StringComparison.Ordinal);
        Assert.Contains("m00001", system, StringComparison.Ordinal);

        var cold = new AcpContextStore(() => "SYS", 500_000);
        cold.AppendUser("小对话");
        cold.AppendAssistant("回复");
        var coldSystem = cold.BuildRequest(new AgentMemoryDocument(), [], Settings(500_000))[0].Content!;
        Assert.Contains(AcpContextStore.PhilosophyLine, coldSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("[ACP 上下文预算]", coldSystem, StringComparison.Ordinal);
    }

    [Fact]
    public void Gc_truncation_parks_victims_into_searchable_tombstone()
    {
        var store = NewStore(900);
        for (var i = 0; i < 12; i++)
        {
            store.AppendUser($"问题-{i:D2} {Long(40, $"u{i}")}");
            store.AppendAssistant($"回答-{i:D2} {Long(40, $"a{i}")}");
        }
        store.BuildRequest(new AgentMemoryDocument(), [], Settings(900));

        var status = JsonDocument.Parse(store.Status()).RootElement;
        Assert.True(status.GetProperty("gcRan").GetBoolean());
        Assert.True(status.GetProperty("activeChars").GetInt64() <= 900 + 400,
            $"active={status.GetProperty("activeChars").GetInt64()}");

        var search = JsonDocument.Parse(store.Search("自动截断", 8)).RootElement;
        Assert.Equal(JsonValueKind.Array, search.ValueKind);
        Assert.True(search.GetArrayLength() >= 1);
        Assert.StartsWith("b", search[0].GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gc_never_orphans_tool_results_when_budget_is_crossed_between_call_and_results()
    {
        const int budget = 4_200;
        var store = NewStore(budget);
        store.AppendUser(Long(1_000, "OLD-USER"));
        store.AppendAssistantToolCalls(null,
        [
            new AssistantToolCall("call-a", "page_context", "{}"),
            new AssistantToolCall("call-b", "page_security_snapshot", "{}")
        ]);
        store.AppendToolResult("call-a", Long(900, "PAGE-CONTEXT"), "page_context");
        store.AppendToolResult("call-b", Long(900, "SECURITY-SNAPSHOT"), "page_security_snapshot");
        for (var i = 0; i < 6; i++) store.AppendAssistant(Long(100, $"recent-{i}"));

        var request = store.BuildRequest(new AgentMemoryDocument(), [], Settings(budget));

        AssertValidToolProtocol(request);
    }

    [Fact]
    public void Annotated_refs_are_visible_for_every_entry_in_request()
    {
        var store = NewStore();
        store.AppendUser("你好");
        store.AppendAssistantToolCalls(null, [new AssistantToolCall("c9", "tool_x", "{}")]);
        store.AppendToolResult("c9", "结果", "tool_x");

        var request = store.BuildRequest(new AgentMemoryDocument(), [], Settings(Budget));
        var bodies = request.Skip(1).Select(m => m.Content!).ToList();
        Assert.StartsWith("[m00001·", bodies[0], StringComparison.Ordinal);
        Assert.StartsWith("[m00002·", bodies[1], StringComparison.Ordinal);
        Assert.StartsWith("[m00003·", bodies[2], StringComparison.Ordinal);
        Assert.Contains("(调用工具 tool_x)", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_line_reports_ratio_and_active_blocks()
    {
        var store = NewStore(10_000);
        store.AppendUser(Long(2_000, "bulk"));

        var line = store.UsageLine(10_000);
        Assert.Contains("上下文", line, StringComparison.Ordinal);
        Assert.Contains("%", line, StringComparison.Ordinal);
        Assert.Contains("块", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_accepts_json_encoded_ranges_string()
    {
        var registry = new AcpContextRegistry();
        var store = NewStore();
        store.AppendUser("旧任务");
        store.AppendAssistant("中间结论");
        for (var i = 0; i < 6; i++) store.AppendAssistant(Long(90, $"filler-{i}"));
        store.AppendUser("收尾问题");
        store.AppendAssistant("好的");
        registry.Current = store;

        var tools = new AiToolRegistry();
        new AcpToolAdapter(registry).RegisterAll(tools);
        tools.TryGet("context_compress", out var definition);
        Assert.NotNull(definition);

        var arguments = JsonSerializer.SerializeToElement(new
        {
            ranges = """[{"start":"m00001","end":"m00002","summary":"两条早期消息的摘要","title":"开局"}]"""
        });
        var result = await definition!.Handler(
            new ToolInvocation("context_compress", arguments), default);

        Assert.True(result.Success, result.Content);
        Assert.Equal(1, store.BlockCount);
    }

    [Fact]
    public void Ref_parsing_tolerates_bracket_and_size_annotated_forms()
    {
        var store = NewStore();
        store.AppendUser("旧任务");
        // 单条目标区间也要明显大于存档标记开销，否则会被收缩守卫（正确地）拒绝。
        for (var i = 0; i < 3; i++) store.AppendUser(Long(160, $"target-{i}"));
        for (var i = 0; i < 6; i++) store.AppendAssistant(Long(90, $"filler-{i}"));
        store.AppendUser("收尾问题");
        store.AppendAssistant("好的");

        var cases = new[]
        {
            (Form: "m00002", Start: "m00002", End: "m00002"),
            (Form: "[m00003]", Start: "m00003", End: "m00003"),
            (Form: "[m00004·0.3K]", Start: "m00004", End: "m00004")
        };
        foreach (var testCase in cases)
        {
            var (ok, message) = store.Compress(testCase.Start, testCase.End, $"{testCase.Form} 的单条摘要", null);
            if (!ok) Assert.Fail($"ref 形式 {testCase.Form} 解析失败: {message}");
        }
        // 三种写法各命中一个真实条目并成功建块；带尺寸/括号的形式不会误删其它条目。
        Assert.Equal(3, store.BlockCount);
    }

    private static void AssertValidToolProtocol(IReadOnlyList<ChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.Where(message => message.Role != "system"))
        {
            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 } calls)
            {
                Assert.Empty(pending);
                pending.UnionWith(calls.Select(call => call.Id));
                continue;
            }
            if (message.Role == "tool")
            {
                Assert.NotNull(message.ToolCallId);
                Assert.True(pending.Remove(message.ToolCallId!),
                    $"orphan tool result: {message.ToolCallId}");
                continue;
            }
            Assert.Empty(pending);
        }
        Assert.Empty(pending);
    }
}
