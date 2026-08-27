using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// B-class runtime extensions: todo checklist tool (dsh tool-todo lineage), oversized-result
/// spill with read_spill paging, additionalContexts/concludesTurn pipeline extras, and
/// durable approval audits.
/// </summary>
public sealed class AgentRuntimeExtensionsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hackermes-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
    private AiSettings Settings() => new()
    {
        MaxToolRounds = 8,
        MaxContextCharacters = 120_000,
        AcpEnabled = false,
        MemoryEnabled = false,
    };

    #region todo_write

    [Fact]
    public void Todo_write_accepts_whole_list_and_reports_counts()
    {
        var registry = new AgentTodoRegistry();
        var result = registry.Write(JsonDocument.Parse(
            """{"todos":[{"content":"扫描目标","status":"completed"},{"content":"分析响应","status":"in_progress"},{"content":"撰写报告"}]}""")
            .RootElement.Clone());

        Assert.True(result.Success);
        Assert.Contains("1 待办", result.Content, StringComparison.Ordinal);
        Assert.Contains("1 进行中", result.Content, StringComparison.Ordinal);
        Assert.Contains("1 已完成", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, registry.Current.Count);
        Assert.Equal(AgentTodoStatus.InProgress, registry.Current[1].Status);
    }

    [Fact]
    public void Todo_write_rejects_duplicates_and_parallel_in_progress()
    {
        var registry = new AgentTodoRegistry();
        var duplicate = registry.Write(JsonDocument.Parse(
            """{"todos":[{"content":"a"},{"content":"a"}]}""").RootElement.Clone());
        Assert.False(duplicate.Success);
        Assert.Contains("重复", duplicate.Content, StringComparison.Ordinal);

        var parallel = registry.Write(JsonDocument.Parse(
            """{"todos":[{"content":"a","status":"in_progress"},{"content":"b","status":"in_progress"}]}""").RootElement.Clone());
        Assert.False(parallel.Success);
        Assert.Contains("in_progress", parallel.Content, StringComparison.Ordinal);

        // Rejected writes leave the previous list untouched.
        Assert.Empty(registry.Current);
    }

    [Fact]
    public void Todo_begin_turn_drops_completed_and_keeps_open_items()
    {
        var registry = new AgentTodoRegistry();
        registry.Write(JsonDocument.Parse(
            """{"todos":[{"content":"step one","status":"pending"},{"content":"step two","status":"completed"}]}""")
            .RootElement.Clone());
        Assert.Equal(2, registry.Current.Count);

        registry.BeginTurn();
        Assert.Single(registry.Current);
        Assert.Equal("step one", registry.Current[0].Content);
        Assert.Equal(AgentTodoStatus.Pending, registry.Current[0].Status);
    }

    [Fact]
    public async Task Todo_tool_registers_and_routes_through_registry()
    {
        var registry = new AgentTodoRegistry();
        var tools = new AiToolRegistry();
        new AgentTodoToolAdapter(registry).RegisterAll(tools);

        Assert.True(tools.TryGet("todo_write", out var definition));
        using var args = JsonDocument.Parse("""{"todos":[{"content":"x","status":"pending"}]}""");
        var result = await definition!.Handler(
            new ToolInvocation("todo_write", args.RootElement.Clone()), CancellationToken.None);
        Assert.True(result.Success);
    }

    #endregion

    #region spill

    [Fact]
    public async Task Oversized_result_spills_to_store_with_readable_locator()
    {
        var spillRoot = Path.Combine(_dataDir, "spill-root");
        var store = new AgentSpillStore(() => spillRoot);
        var tools = new AiToolRegistry();
        tools.Register(new AiToolDefinition(
            "chatty_tool", "tool", JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(new string('e', 40_000)))));
        var dispatcher = new AiToolDispatcher(
            tools, new DefaultToolPolicyGate(), new RejectingToolConfirmationService(),
            TimeProvider.System, AiToolDispatcher.DefaultSessionGrantLifetime,
            maxToolResultCharacters: 12_000,
            toolCallTimeout: AiToolDispatcher.DefaultToolCallTimeout,
            spillStore: store);

        var result = await dispatcher.InvokeAsync(new ToolInvocation("chatty_tool", JsonDocument.Parse("{}").RootElement.Clone(), null, "sess-1", "call-9"));

        Assert.True(result.Success);
        Assert.Contains("已外存", result.Content, StringComparison.Ordinal);
        Assert.Contains("read_spill", result.Content, StringComparison.Ordinal);
        var locatorStart = result.Content.IndexOf("spill:", StringComparison.Ordinal);
        Assert.True(locatorStart >= 0);
        var locator = result.Content.Substring(locatorStart, "spill:".Length + 32);
        // Full payload survives off-context and pages back intact.
        var firstSlice = store.Read(locator, 0, 12_000)!;
        Assert.StartsWith(new string('e', 100), firstSlice, StringComparison.Ordinal);
        Assert.Contains("剩余约", firstSlice, StringComparison.Ordinal);
        // A single bounded read of the whole payload accounts for every character.
        var whole = store.Read(locator, 0, 48_000)!;
        var remainderMarker = whole.IndexOf("…[剩余约", StringComparison.Ordinal);
        Assert.Equal(40_000, remainderMarker >= 0 ? remainderMarker : whole.Length);
    }

    [Fact]
    public async Task Read_spill_rejects_forged_locators_and_paging_works()
    {
        var store = new AgentSpillStore(() => _dataDir);
        var tools = new AiToolRegistry();
        new AgentSpillToolAdapter(store).RegisterAll(tools);
        var locator = store.Save("session-x", "packet_dump", new string('z', 500));

        var good = await InvokeReadSpill(tools, $$"""{"locator":"{{locator}}","offset":100,"limit":50}""");
        Assert.StartsWith(new string('z', 50), good.Content, StringComparison.Ordinal);
        Assert.Contains("剩余约", good.Content, StringComparison.Ordinal);

        var forged = await InvokeReadSpill(tools, """{"locator":"../../secrets"}""");
        Assert.False(forged.Success);
        var unknownToken = await InvokeReadSpill(tools, $$"""{"locator":"{{"spill:" + new string('a', 32)}}"}""");
        Assert.False(unknownToken.Success);
    }

    private static async Task<ToolResult> InvokeReadSpill(IAiToolRegistry tools, string argumentsJson)
    {
        tools.TryGet("read_spill", out var definition);
        using var args = JsonDocument.Parse(argumentsJson);
        return await definition!.Handler(
            new ToolInvocation("read_spill", args.RootElement.Clone()), CancellationToken.None);
    }

    #endregion

    #region additionalContexts / concludesTurn

    [Fact]
    public async Task Tool_additional_context_is_injected_into_the_next_request()
    {
        var tools = new AiToolRegistry();
        tools.Register(new AiToolDefinition(
            "evidence_tool", "tool", JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(new ToolResult(true, "evidence",
                AdditionalContexts: ["补充上下文：目标端口为 8443"]))));
        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "c1", "evidence_tool", "{}"), "tool_calls")])
            .Respond(_ => [new ChatStreamDelta("done", null, "stop")]);
        var runner = CreateRunner(client, tools);

        await runner.RunTurnAsync("collect", "m", null, "s-ctx", CancellationToken.None);

        var secondRequest = client[1].Snapshots.Single();
        Assert.Contains(secondRequest, message => message.Role == "user" && message.Content!.Contains("8443", StringComparison.Ordinal));
        Assert.Contains(runner.Log.Snapshot(), @event =>
            @event.Kind == AgentEventKind.UserMessage &&
            ((UserMessageReceived)@event.Data).Injected);
        // Injected context never masquerades as operator steering.
        Assert.DoesNotContain(runner.Log.Snapshot(), @event =>
            @event.Kind == AgentEventKind.UserMessage &&
            ((UserMessageReceived)@event.Data).Injected &&
            ((UserMessageReceived)@event.Data).Priority);
    }

    [Fact]
    public async Task Tool_concludes_turn_closes_it_after_the_step_settles()
    {
        var tools = new AiToolRegistry();
        tools.Register(new AiToolDefinition(
            "finisher", "tool", JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(new ToolResult(true, "finished!", ConcludesTurn: true))));
        var calls = 0;
        var client = new ScriptedClient().RespondAlways(_ => [
            new ChatStreamDelta(null, new ToolCallDelta(0, $"c{Interlocked.Increment(ref calls)}", "finisher", "{}"), "tool_calls"),
        ]);
        var runner = CreateRunner(client, tools, maxRounds: 8);

        var reason = await runner.RunTurnAsync("wrap up", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        Assert.Equal(1, calls); // the concluding step was the only one
        Assert.Equal(AgentEventKind.TurnEnd, runner.Log.Snapshot()[^1].Kind);
    }

    #endregion

    #region approval audit

    [Fact]
    public async Task Confirmation_outcomes_land_as_durable_audit_events()
    {
        var tools = new AiToolRegistry();
        tools.Register(new AiToolDefinition(
            "mutating_tool", "tool", JsonSerializer.SerializeToElement(new { }), AiToolRisk.Mutating,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("ran"))));
        var approvals = 0;
        var confirmation = new ScriptedConfirmation
        {
            NextAnswer = () => ++approvals % 2 == 1, // approve once, then reject
        };
        var dispatcher = new AiToolDispatcher(tools, new DefaultToolPolicyGate(), confirmation);
        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "c-m1", "mutating_tool", "{}"), null),
                new ChatStreamDelta(null, new ToolCallDelta(1, "c-m2", "mutating_tool", "{}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("acknowledged", null, "stop")]);
        var runner = CreateRunnerWithDispatcher(client, dispatcher);

        await runner.RunTurnAsync("ask twice", "m", "page-7", "sess-audit", CancellationToken.None);

        var audits = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.ApprovalAudited)
            .Select(@event => ((ApprovalAudited)@event.Data).Record).ToArray();
        Assert.Equal(2, audits.Length);
        Assert.Equal("ApprovedOnce", audits[0].Decision);
        Assert.Equal("RejectedByOperator", audits[1].Decision);
        Assert.All(audits, record => Assert.Equal("page-7", record.PageId));
        Assert.All(audits, record => Assert.Equal("mutating_tool", record.Tool));
    }

    private sealed class ScriptedConfirmation : IToolConfirmationService
    {
        public Func<bool> NextAnswer { get; init; } = () => true;

        public ValueTask<ToolConfirmation> ConfirmAsync(ToolInvocation invocation, string reason, CancellationToken ct) =>
            ValueTask.FromResult(new ToolConfirmation(NextAnswer()));
    }

    #endregion

    #region shrink guard & legacy overflow trim

    [Fact]
    public void Manual_compression_rejects_summaries_not_smaller_than_the_range()
    {
        var store = new AcpContextStore(() => "system", 10_000);
        for (var index = 0; index < 6; index++)
        {
            var filler = new string((char)('c' + index), 1_400);
            if (index % 2 == 0) store.AppendUser(filler);
            else store.AppendAssistant(filler);
        }

        var bloated = new string('b', 5_000);
        var (ok, message) = store.Compress("m00001", "m00002", bloated, "bad");
        Assert.False(ok);
        Assert.Contains("收缩守卫", message, StringComparison.Ordinal);
        Assert.Empty(store.Blocks);

        // A concise summary passes the guard and lands normally.
        var (okShort, _) = store.Compress("m00001", "m00002", "精炼摘要：保留关键路径与结论。", "good");
        Assert.True(okShort);
        Assert.Single(store.Blocks);
    }

    [Fact]
    public async Task Legacy_strategy_recovers_from_context_overflow_by_trimming_oldest_turn()
    {
        var overflowSeen = false;
        var client = new ScriptedClient().Respond(_ =>
        {
            if (!overflowSeen)
            {
                overflowSeen = true;
                throw new HttpRequestException(
                    "HTTP 400 Bad Request：prompt is too long: 200000 tokens > 131072 maximum.");
            }
            return [new ChatStreamDelta("recovered", null, "stop")];
        });
        var runner = CreateRunner(client);

        // Seed a large completed turn so there is something to trim.
        runner.SeedHistory([
            new ChatMessage("user", new string('o', 30_000)),
            new ChatMessage("assistant", "old answer"),
            new ChatMessage("user", "current question"),
        ]);

        var reason = await runner.RunTurnAsync("retry after trim", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        Assert.Contains(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.ContextCompacted);
        Assert.DoesNotContain(runner.History, message => message.Role == "assistant" && message.Content == "old answer");
        Assert.Contains(runner.History, message => message.Content == "current question");
    }

    #endregion

    private static AgentTurnRunner CreateRunner(
        IOpenAiChatClient client,
        IAiToolRegistry? tools = null,
        Action<AgentTurnRunnerOptions>? configureOptions = null,
        int maxRounds = 8)
    {
        var registry = tools ?? new AiToolRegistry();
        var options = new AgentTurnRunnerOptions { ToolSelector = () => registry.All };
        configureOptions?.Invoke(options);
        return new AgentTurnRunner(
            client,
            new AiToolDispatcher(registry, new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
            () => new AiSettings { MaxToolRounds = maxRounds, MaxParallelReadOnlyTools = 1 },
            () => new AgentMemoryDocument(),
            () => [],
            options);
    }

    private static AgentTurnRunner CreateRunnerWithDispatcher(
        IOpenAiChatClient client, AiToolDispatcher dispatcher)
    {
        var runner = new AgentTurnRunner(
            client, dispatcher,
            () => new AiSettings(),
            () => new AgentMemoryDocument(),
            () => [],
            new AgentTurnRunnerOptions());
        // Mirror the VM wiring: dispatcher audits flow into the session log.
        dispatcher.Audited += runner.AppendAudit;
        return runner;
    }

    /// <summary>Duplicated minimal streaming stub from AgentTurnRunnerTests (kept local by design).</summary>
    private sealed class ScriptedClient : IOpenAiChatClient
    {
        public sealed class Responder
        {
            public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> Handler { get; init; } =
                _ => Array.Empty<ChatStreamDelta>();
            public List<IReadOnlyList<ChatMessage>> Snapshots { get; } = [];
        }

        public List<Task> BeforeFirstStream { get; } = [];
        public Responder this[int index] => Responders[index];
        public List<Responder> Responders { get; } = [];
        private bool _always;

        public ScriptedClient Respond(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> handler)
        {
            Responders.Add(new Responder { Handler = handler });
            return this;
        }

        public ScriptedClient RespondAlways(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> handler)
        {
            Respond(handler);
            _always = true;
            return this;
        }

        public int ResponsesGiven { get; private set; }

        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = _always ? Responders.Count - 1 : Math.Min(ResponsesGiven, Responders.Count - 1);
            var responder = Responders[index];
            ResponsesGiven++;
            await Task.Yield();

            if (ResponsesGiven == 1)
                foreach (var gate in BeforeFirstStream)
                    await gate.WaitAsync(ct).ConfigureAwait(false);

            responder.Snapshots.Add(request.Messages);
            IReadOnlyList<ChatStreamDelta> deltas;
            try
            {
                deltas = responder.Handler(request.Messages);
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new StreamFault(ex.Message, ex);
            }

            foreach (var delta in deltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
            }
        }

        public sealed class StreamFault(string message, Exception inner) : Exception(message, inner);
    }
}
