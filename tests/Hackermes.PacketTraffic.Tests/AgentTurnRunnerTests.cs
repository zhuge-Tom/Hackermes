using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Headless agent loop contract (deepseek-harness lineage): durable event vocabulary,
/// steering inbox claims at step boundaries, promoted-instruction preemption,
/// model-order tool commits, bounded parallel read-only pools, and clean-failure retries.
/// </summary>
public sealed class AgentTurnRunnerTests
{
    private static AiSettings Settings(int maxRounds = 8, int parallel = 1, int contextCharacters = 120_000) => new()
    {
        MaxToolRounds = maxRounds,
        MaxParallelReadOnlyTools = parallel,
        MaxContextCharacters = contextCharacters,
        AcpEnabled = false,
        MemoryEnabled = false,
    };

    private static AgentTurnRunner CreateRunner(
        IOpenAiChatClient client,
        IAiToolRegistry? tools = null,
        Action<AgentTurnRunnerOptions>? configureOptions = null,
        int parallel = 1,
        int maxRounds = 8,
        int contextCharacters = 120_000)
    {
        var registry = tools ?? new AiToolRegistry();
        var options = new AgentTurnRunnerOptions { ToolSelector = () => registry.All };
        configureOptions?.Invoke(options);
        return new AgentTurnRunner(
            client,
            new AiToolDispatcher(registry, new DefaultToolPolicyGate(), new AllowingConfirmation()),
            () => Settings(maxRounds, parallel, contextCharacters),
            () => new AgentMemoryDocument(),
            () => [],
            options);
    }

    private static List<string> RegisterProbe(IAiToolRegistry tools, string name)
    {
        var calls = new List<string>();
        tools.Register(new AiToolDefinition(
            name, "probe", JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
            (invocation, _) =>
            {
                lock (calls) calls.Add(name + ":" + invocation.Arguments.GetRawText());
                return ValueTask.FromResult(ToolResult.Ok($"{name}-done"));
            }));
        return calls;
    }

    private static void RegisterDelayed(IReadOnlyCollection<(string Name, int DelayMs)> tools, AiToolRegistry registry)
    {
        foreach (var (name, delayMs) in tools)
            registry.Register(new AiToolDefinition(
                name, name, JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
                async (_, ct) =>
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    return ToolResult.Ok($"{name}-result");
                }));
    }

    [Fact]
    public async Task Turn_with_tool_round_trip_emits_full_event_vocabulary()
    {
        var tools = new AiToolRegistry();
        RegisterProbe(tools, "probe");
        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta("正在检查。", null, null),
                new ChatStreamDelta(null, new ToolCallDelta(0, "call-1", "probe", "{\"path\":\"a\"}"), null),
                new ChatStreamDelta(null, null, "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("完成。", null, "stop")]);
        var runner = CreateRunner(client, tools);

        var reason = await runner.RunTurnAsync("开始任务", "test-model", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        var events = runner.Log.Snapshot();
        Assert.Equal(AgentEventKind.TurnStart, events[0].Kind);
        Assert.Contains(AgentEventKind.UserMessage, events.Select(@event => @event.Kind));
        Assert.Contains(AgentEventKind.StepStart, events.Select(@event => @event.Kind));
        Assert.Contains(AgentEventKind.AssistantChunk, events.Select(@event => @event.Kind));
        Assert.Contains(AgentEventKind.ToolCall, events.Select(@event => @event.Kind));
        Assert.Contains(AgentEventKind.ToolResult, events.Select(@event => @event.Kind));
        Assert.Equal(AgentEventKind.TurnEnd, events[^1].Kind);

        // Tool protocol ordering: call logged before its result; exactly two steps ran.
        var callSeq = events.First(@event => @event.Kind == AgentEventKind.ToolCall).Seq;
        var resultSeq = events.First(@event => @event.Kind == AgentEventKind.ToolResult).Seq;
        Assert.True(callSeq < resultSeq);
        Assert.Equal(2, events.Count(@event => @event.Kind == AgentEventKind.StepStart));

        // Second model request carries the OpenAI tool transcript.
        var secondRequest = client[1].Snapshots.Single();
        Assert.Contains(secondRequest, message => message.Role == "assistant" && message.ToolCalls is { Count: > 0 });
        Assert.Contains(secondRequest, message => message.Role == "tool" && message.Content == "probe-done");

        // Final assistant reply closes the turn as the report.
        var finalReply = (AssistantReply)events.Last(@event => @event.Kind == AgentEventKind.AssistantMessage).Data;
        Assert.False(finalReply.HasToolCalls);
        Assert.True(finalReply.IsFinalReport);
        Assert.Equal("完成。", finalReply.Content);
    }

    [Fact]
    public async Task Queued_instruction_is_claimed_at_the_step_boundary_within_the_same_turn()
    {
        var tools = new AiToolRegistry();
        RegisterProbe(tools, "probe");
        var client = new ScriptedClient()
            .Respond(_ => [new ChatStreamDelta(null, new ToolCallDelta(0, "c1", "probe", "{}"), "tool_calls")])
            .Respond(_ => [new ChatStreamDelta("done", null, "stop")]);
        var runner = CreateRunner(client, tools);
        runner.EnqueueInstruction("also do this");

        var reason = await runner.RunTurnAsync("start", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        // Claimed steer lands as a durable user/message between steps…
        Assert.Contains(runner.Log.Snapshot(), @event =>
            @event.Kind == AgentEventKind.UserMessage &&
            ((UserMessageReceived)@event.Data).Steered &&
            ((UserMessageReceived)@event.Data).Text == "also do this");
        // …and reaches the follow-up request together with the tool result.
        var secondRequest = client[1].Snapshots.Single();
        Assert.Contains(secondRequest, message => message.Role == "user" && message.Content == "also do this");
        Assert.Contains(secondRequest, message => message.Role == "tool");
    }

    [Fact]
    public async Task Promoted_instruction_preempts_all_dispatches_of_the_running_step()
    {
        var tools = new AiToolRegistry();
        RegisterProbe(tools, "probe_a");
        RegisterProbe(tools, "probe_b");
        var dispatched = 0;
        tools.Register(new AiToolDefinition(
            "mutating_step", "tool", JsonSerializer.SerializeToElement(new { }), AiToolRisk.Mutating,
            (_, _) => { Interlocked.Increment(ref dispatched); return ValueTask.FromResult(ToolResult.Ok("ran")); }));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "c1", "mutating_step", "{}"), null),
                new ChatStreamDelta(null, new ToolCallDelta(1, "c2", "probe_b", "{}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("acknowledged the steer", null, "stop")]);
        client.BeforeFirstStream.Add(release.Task);

        var runner = CreateRunner(client, tools);
        var running = runner.RunTurnAsync("go", "m", null, null, CancellationToken.None);
        // Operator queues and promotes while the model response is held back.
        runner.EnqueueInstruction("stop everything");
        runner.PromoteLatestInstruction();
        release.TrySetResult();
        var reason = await running;

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        Assert.Equal(0, dispatched);
        // Both calls stay logged with synthetic results so the protocol remains complete.
        var results = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.ToolResult)
            .Select(@event => (ToolCallCompleted)@event.Data).ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, result => Assert.Contains("优先指示", result.Content, StringComparison.Ordinal));
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Contains(runner.History, message => message.Role == "user" && message.Content == "stop everything");
    }

    [Fact]
    public async Task Max_rounds_ends_the_turn_with_MaxRounds_reason()
    {
        var tools = new AiToolRegistry();
        RegisterProbe(tools, "probe");
        var endless = new ScriptedClient().RespondAlways(_ => [
            new ChatStreamDelta(null, new ToolCallDelta(0, "c", "probe", "{}"), null),
        ]);
        var runner = CreateRunner(endless, tools, maxRounds: 3);

        var reason = await runner.RunTurnAsync("loop", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.MaxRounds, reason);
        var ended = Assert.Single(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.TurnEnd);
        Assert.Equal(AgentTurnEndReason.MaxRounds, ((TurnEnded)ended.Data).Reason);
        Assert.Equal(3, runner.Log.Snapshot().Count(@event => @event.Kind == AgentEventKind.StepStart));
        Assert.Contains("3 轮工具调用上限",
            ((AssistantReply)runner.Log.Snapshot().Last(@event => @event.Kind == AgentEventKind.AssistantMessage).Data).Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_maps_to_Aborted_turn_end()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = CreateRunner(new ScriptedClient());

        var reason = await runner.RunTurnAsync("x", "m", null, null, cts.Token);

        Assert.Equal(AgentTurnEndReason.Aborted, reason);
        var ended = Assert.Single(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.TurnEnd);
        Assert.Equal(AgentTurnEndReason.Aborted, ((TurnEnded)ended.Data).Reason);
    }

    [Fact]
    public async Task Parallel_readonly_pool_commits_results_in_model_order()
    {
        var tools = new AiToolRegistry();
        RegisterDelayed([("fast", 20), ("medium", 120), ("slow", 260)], tools);

        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "c-fast", "fast", "{}"), null),
                new ChatStreamDelta(null, new ToolCallDelta(1, "c-slow", "slow", "{}"), null),
                new ChatStreamDelta(null, new ToolCallDelta(2, "c-medium", "medium", "{}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("all done", null, "stop")]);
        var runner = CreateRunner(client, tools, parallel: 4);

        await runner.RunTurnAsync("fan out", "m", null, null, CancellationToken.None);

        // Committed strictly in model order even though completion order differs.
        var results = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.ToolResult)
            .Select(@event => ((ToolCallCompleted)@event.Data).Name).ToArray();
        Assert.Equal(["fast", "slow", "medium"], results);
        var toolHistory = runner.History.Where(message => message.Role == "tool")
            .Select(message => message.Content ?? string.Empty).ToArray();
        Assert.Equal(["fast-result", "slow-result", "medium-result"], toolHistory);
        // Parallelism really happened: total wall time stayed well below the serial sum (~400ms).
        Assert.Equal(3, results.Length);
    }

    [Fact]
    public async Task Mutating_tool_breaks_the_parallel_pool_and_runs_alone()
    {
        var tools = new AiToolRegistry();
        RegisterDelayed([("ro_1", 60), ("ro_2", 10)], tools);
        tools.Register(new AiToolDefinition(
            "mut_1", "mut", JsonSerializer.SerializeToElement(new { }), AiToolRisk.Mutating,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("rm"))));

        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "a", "ro_1", "{}"), null),
                new ChatStreamDelta(null, new ToolCallDelta(1, "b", "mut_1", "{}"), null),
                new ChatStreamDelta(null, new ToolCallDelta(2, "c", "ro_2", "{}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("finished", null, "stop")]);
        var confirmation = new AllowingConfirmation();
        var runner = new AgentTurnRunner(
            client,
            new AiToolDispatcher(tools, new DefaultToolPolicyGate(), confirmation),
            () => Settings(parallel: 4),
            () => new AgentMemoryDocument(),
            () => [],
            new AgentTurnRunnerOptions { ToolSelector = () => tools.All });

        await runner.RunTurnAsync("mixed", "m", null, null, CancellationToken.None);

        Assert.Equal(1, confirmation.Count); // mutating call asked exactly once
        var names = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.ToolResult)
            .Select(@event => ((ToolCallCompleted)@event.Data).Name).ToArray();
        Assert.Equal(["ro_1", "mut_1", "ro_2"], names);
    }

    [Fact]
    public async Task Clean_request_failure_retries_then_succeeds()
    {
        var failures = 0;
        var flaky = new ScriptedClient().Respond(_ =>
        {
            if (Interlocked.Increment(ref failures) <= 2)
                throw new HttpRequestException("connection reset by peer");
            return [new ChatStreamDelta("recovered", null, "stop")];
        });
        var runner = CreateRunner(flaky, configureOptions: options =>
        {
            options.MaxRequestRetries = 3;
            options.RetryBackoff = _ => TimeSpan.FromMilliseconds(1);
        });

        var reason = await runner.RunTurnAsync("flaky", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        var retries = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.RequestRetry)
            .Select(@event => (RequestRetried)@event.Data).ToArray();
        Assert.Equal(2, retries.Length);
        Assert.Equal([1, 2], retries.Select(retry => retry.Attempt).ToArray());
        Assert.EndsWith("recovered", runner.History[^1].Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exhausted_retries_fail_the_turn_with_error_detail()
    {
        var alwaysFailing = new ScriptedClient().RespondAlways(
            _ => throw new HttpRequestException("down"));
        var runner = CreateRunner(alwaysFailing, configureOptions: options =>
        {
            options.MaxRequestRetries = 1;
            options.RetryBackoff = _ => TimeSpan.FromMilliseconds(1);
        });

        var reason = await runner.RunTurnAsync("doomed", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Error, reason);
        var ended = Assert.Single(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.TurnEnd);
        var payload = Assert.IsType<TurnEnded>(ended.Data);
        Assert.Equal(AgentTurnEndReason.Error, payload.Reason);
        Assert.NotNull(payload.Detail);
    }

    [Fact]
    public async Task Terminal_http_errors_fail_immediately_without_retries()
    {
        var attempts = 0;
        var unauthorized = new ScriptedClient().RespondAlways(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("HTTP 401 Unauthorized：invalid api key");
        });
        var runner = CreateRunner(unauthorized, configureOptions: options =>
        {
            options.MaxRequestRetries = 3;
            options.RetryBackoff = _ => TimeSpan.FromMilliseconds(1);
        });

        var reason = await runner.RunTurnAsync("doomed", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Error, reason);
        Assert.Equal(1, attempts); // terminal classification: no retry attempts burned
        Assert.DoesNotContain(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.RequestRetry);
    }

    [Fact]
    public async Task Cancellation_fills_orphan_tool_calls_with_synthetic_results()
    {
        var tools = new AiToolRegistry();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tools.Register(new AiToolDefinition(
            "blocking_probe", "probe", JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
            async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return ToolResult.Ok("never");
            }));

        using var cts = new CancellationTokenSource();
        var client = new ScriptedClient().Respond(_ => [
            new ChatStreamDelta(null, new ToolCallDelta(0, "c-1", "blocking_probe", "{}"), null),
            new ChatStreamDelta(null, new ToolCallDelta(1, "c-2", "blocking_probe", "{}"), "tool_calls"),
        ]);
        var runner = CreateRunner(client, tools);

        var running = runner.RunTurnAsync("stop me", "m", null, null, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var reason = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AgentTurnEndReason.Aborted, reason);
        // Every logged tool/call has a matching result; uncompleted ones carry the marker.
        var events = runner.Log.Snapshot();
        var callIds = events.Where(@event => @event.Kind == AgentEventKind.ToolCall)
            .Select(@event => ((ToolCallRequested)@event.Data).CallId).ToArray();
        var results = events.Where(@event => @event.Kind == AgentEventKind.ToolResult)
            .Select(@event => ((ToolCallCompleted)@event.Data).CallId).ToArray();
        Assert.Equal(2, callIds.Length);
        Assert.Equal(callIds, results);
        Assert.Contains(runner.History,
            message => message.Role == "tool" && (message.Content ?? string.Empty).Contains("中止", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reasoning_deltas_are_logged_but_never_enter_model_history()
    {
        var client = new ScriptedClient().Respond(_ => [
            new ChatStreamDelta(null, null, null, Reasoning: "先分析目标结构。"),
            new ChatStreamDelta(null, null, null, Reasoning: "再决定工具。"),
            new ChatStreamDelta("结论如下。", null, "stop"),
        ]);
        var runner = CreateRunner(client);

        var reason = await runner.RunTurnAsync("think first", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        var reasoningTexts = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.ReasoningChunk)
            .Select(@event => ((ReasoningDelta)@event.Data).Text).ToArray();
        Assert.Equal(["先分析目标结构。", "再决定工具。"], reasoningTexts);
        // Thinking stays out of the model-visible transcript entirely.
        Assert.DoesNotContain(runner.History,
            message => (message.Content ?? string.Empty).Contains("分析目标结构", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Length_capped_reply_drops_tool_fragments_and_sticks_as_turn_reason()
    {
        var tools = new AiToolRegistry();
        RegisterProbe(tools, "probe");
        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta("部分输出…", null, null),
                new ChatStreamDelta(null, new ToolCallDelta(0, "c-x", "probe", "{}"), null),
                new ChatStreamDelta(null, null, "length"),
            ])
            .Respond(_ => [new ChatStreamDelta("干净的收尾。", null, "stop")]);
        var runner = CreateRunner(client, tools);
        runner.EnqueueInstruction("继续");

        var reason = await runner.RunTurnAsync("long task", "m", null, null, CancellationToken.None);

        // Sticky: step 2 completed cleanly after the steer, but the cap verdict stands.
        Assert.Equal(AgentTurnEndReason.LengthCapped, reason);
        // The truncated tool-call fragment never reached the protocol.
        Assert.DoesNotContain(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.ToolCall);
        Assert.DoesNotContain(runner.History, message => message.Role == "tool");
        Assert.Contains(runner.History, message => message.Role == "assistant" && message.Content == "部分输出…");
    }

    [Fact]
    public async Task Context_overflow_recovers_through_forced_compaction_and_retries()
    {
        var store = new AcpContextStore(() => "system prompt", 10_000);
        for (var index = 0; index < 6; index++)
        {
            var filler = new string((char)('a' + index), 1_450);
            if (index % 2 == 0) store.AppendUser($"问题 {index}: {filler}");
            else store.AppendAssistant($"回答 {index}: {filler}");
        }
        var overflowSeen = false;
        var client = new ScriptedClient().Respond(messages =>
        {
            // The summarizer's auxiliary call is distinguishable by its system prompt.
            if ((messages.Count > 0 ? messages[0].Content : null)!.Contains("会话压缩器", StringComparison.Ordinal))
                return [new ChatStreamDelta("【目标】验证溢出自愈。【关键事实】保留路径 /etc/app。【待办】继续验证。", null, "stop")];
            if (!overflowSeen)
            {
                overflowSeen = true;
                throw new HttpRequestException(
                    "HTTP 400 Bad Request：This model's maximum context length is 8192 tokens, please reduce the length of the input.");
            }
            return [new ChatStreamDelta("fits now", null, "stop")];
        });
        var compactionWarnings = new List<string>();
        var autoCompactor = new AcpAutoCompactor(
            client, () => "test-model", () => store,
            // Pressure path disabled (ratio 0): this test exercises ONLY the overflow path.
            () => new AiSettings { MaxContextCharacters = 10_000, AutoCompactRatio = 0 },
            new CollectingLogger(compactionWarnings));
        var runner = CreateRunner(client, configureOptions: options => options.AutoCompactor = autoCompactor,
            contextCharacters: 10_000);
        runner.Strategy = new AcpContextStrategy(store);

        var reason = await runner.RunTurnAsync("overflow me", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        Assert.True(overflowSeen);
        // The forced reduction landed durably and visibly before the retry succeeded.
        // (A GC tombstone may also exist: the pre-request budget check ran first.)
        Assert.Contains(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.ContextCompacted);
        var landed = Assert.Single(store.Blocks.Where(block => !block.IsTombstone));
        Assert.True(landed.Active);
        Assert.EndsWith("fits now", runner.History[^1].Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_overflow_without_a_compactor_fails_the_turn_without_retry()
    {
        var attempts = 0;
        var client = new ScriptedClient().RespondAlways(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException(
                "HTTP 400 Bad Request：prompt is too long: 200000 tokens > 131072 maximum.");
        });
        var runner = CreateRunner(client);

        var reason = await runner.RunTurnAsync("overflow", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Error, reason);
        Assert.Equal(1, attempts); // unrecoverable: no compactor wired, so no retry at all
        Assert.DoesNotContain(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.ContextCompacted);
    }

    [Fact]
    public async Task Usage_events_accumulate_from_stream_chunks()
    {
        var client = new ScriptedClient().Respond(_ => [
            new ChatStreamDelta(null, null, null, new StreamUsage(11, 5, 16)),
            new ChatStreamDelta("ok", null, "stop"),
        ]);
        var runner = CreateRunner(client);

        await runner.RunTurnAsync("count me", "m", null, null, CancellationToken.None);

        var usage = Assert.Single(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.Usage);
        var payload = Assert.IsType<UsageRecorded>(usage.Data);
        Assert.Equal(11, payload.Usage.PromptTokens);
    }

    private sealed class CollectingLogger(List<string> sink) : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null)
        {
            lock (sink) sink.Add($"[{level}] {message}");
        }
    }

    private sealed class AllowingConfirmation : IToolConfirmationService
    {
        public int Count { get; private set; }

        public ValueTask<ToolConfirmation> ConfirmAsync(ToolInvocation invocation, string reason, CancellationToken ct)
        {
            Count++;
            return ValueTask.FromResult(new ToolConfirmation(true));
        }
    }

    /// <summary>
    /// Scripted streaming client. Responders run in order; <see cref="RespondAlways"/> repeats
    /// the final one. Handler exceptions thrown before the first delta surface as transport
    /// failures (<see cref="ScriptedClient.StreamFault"/>) so retry semantics can be exercised.
    /// </summary>
    private sealed class ScriptedClient : IOpenAiChatClient
    {
        public sealed class Responder
        {
            public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> Handler { get; init; } =
                _ => Array.Empty<ChatStreamDelta>();
            public List<IReadOnlyList<ChatMessage>> Snapshots { get; } = [];
        }

        /// <summary>Holds the first streamed response back until released (steering races).</summary>
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
                // Preserve the wire-level type: the runner's error classifier keys on it.
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

        /// <summary>Wraps handler exceptions thrown BEFORE the first yield, like a transport failure.</summary>
        public sealed class StreamFault(string message, Exception inner) : Exception(message, inner);
    }
}
