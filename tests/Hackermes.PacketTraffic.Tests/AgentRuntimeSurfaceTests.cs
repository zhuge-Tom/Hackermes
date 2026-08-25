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
/// Surface-era capabilities: pre-step interception waterfall, shared history projector,
/// Markdown transcript export, event-log fork and LLM session titles.
/// </summary>
public sealed class AgentRuntimeSurfaceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hackermes-surface-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private const string SessionId = "abcdef0123456789abcdef0123456789";

    private static AgentTurnRunner CreateRunner(
        IOpenAiChatClient client,
        IAiToolRegistry? tools = null,
        Action<AgentTurnRunnerOptions>? configure = null)
    {
        var registry = tools ?? new AiToolRegistry();
        var options = new AgentTurnRunnerOptions { ToolSelector = () => registry.All };
        configure?.Invoke(options);
        return new AgentTurnRunner(
            client,
            new AiToolDispatcher(registry, new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
            () => new AiSettings { MaxContextCharacters = 120_000 },
            () => new AgentMemoryDocument(),
            () => [],
            options);
    }

    #region pre-step waterfall

    [Fact]
    public async Task Pre_step_reject_closes_the_turn_as_blocked_without_a_model_call()
    {
        var attempts = 0;
        var client = new ScriptedClient().RespondAlways(_ =>
        {
            Interlocked.Increment(ref attempts);
            return [new ChatStreamDelta("never", null, "stop")];
        });
        var runner = CreateRunner(client, configure: options => options.PreStepHooks =
        [
            new LambdaHook(_ => PreStepDecision.Reject("目标页面已关闭，禁止继续评估。")),
        ]);

        var reason = await runner.RunTurnAsync("开始", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Blocked, reason);
        Assert.Equal(0, attempts);
        var ended = Assert.Single(runner.Log.Snapshot(), @event => @event.Kind == AgentEventKind.TurnEnd);
        var payload = Assert.IsType<TurnEnded>(ended.Data);
        Assert.Contains("目标页面已关闭", payload.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_step_rewrite_scrubs_entering_messages_before_they_land()
    {
        string? seenInRequest = null;
        var tools = new AiToolRegistry();
        var client = new ScriptedClient().Respond(request =>
        {
            seenInRequest = request.Last(message => message.Role == "user").Content;
            return [new ChatStreamDelta("ok", null, "stop")];
        });
        var runner = CreateRunner(client, configure: options => options.PreStepHooks =
        [
            new LambdaHook(input => PreStepDecision.RewriteEntering(
                input.EnteringMessages.Select(message => message with
                {
                    Content = message.Content!.Replace("password=123", "password=***"),
                }).ToArray())),
        ]);
        runner.EnqueueInstruction("login with password=123 now");

        await runner.RunTurnAsync("start", "m", null, null, CancellationToken.None);

        Assert.NotNull(seenInRequest);
        Assert.Contains("password=***", seenInRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("password=123", seenInRequest, StringComparison.Ordinal);
        // The redaction is durable: history and log carry the scrubbed text only.
        Assert.DoesNotContain(runner.History,
            message => (message.Content ?? string.Empty).Contains("password=123", StringComparison.Ordinal));
        Assert.All(runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.UserMessage),
            @event => Assert.DoesNotContain("password=123", ((UserMessageReceived)@event.Data).Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pre_step_ephemeral_appendix_rides_the_request_but_not_history()
    {
        var tools = new AiToolRegistry();
        var client = new ScriptedClient().Respond(request =>
        {
            return [new ChatStreamDelta("done", null, "stop")];
        });
        var runner = CreateRunner(client, configure: options => options.PreStepHooks =
        [
            new LambdaHook(_ => PreStepDecision.AppendEphemeral([
                new ChatMessage("user", "[临时] 当前页面快照摘要（仅本次请求可见）"),
            ])),
        ]);

        await runner.RunTurnAsync("看一眼", "m", null, null, CancellationToken.None);

        var outgoing = client[0].Snapshots.Single();
        Assert.Contains(outgoing, message => message.Content!.Contains("页面快照摘要", StringComparison.Ordinal));
        // Ephemeral context never enters the persistent transcript or the model history.
        Assert.DoesNotContain(runner.History, message => (message.Content ?? string.Empty).Contains("页面快照摘要", StringComparison.Ordinal));
        Assert.Empty(runner.Log.Snapshot().Where(@event =>
            @event.Kind == AgentEventKind.UserMessage &&
            ((UserMessageReceived)@event.Data).Text.Contains("页面快照摘要", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Throwing_pre_step_hook_is_skipped_without_failing_the_turn()
    {
        var client = new ScriptedClient().Respond(_ => [new ChatStreamDelta("survived", null, "stop")]);
        var runner = CreateRunner(client, configure: options => options.PreStepHooks =
        [
            new ThrowingHook(),
            new LambdaHook(_ => PreStepDecision.Proceed),
        ]);

        var reason = await runner.RunTurnAsync("keep going", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        Assert.EndsWith("survived", runner.History[^1].Content ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class LambdaHook(Func<PreStepInput, PreStepDecision> decide) : IAgentPreStepHook
    {
        public ValueTask<PreStepDecision> BeforeStepAsync(PreStepInput input, CancellationToken ct) =>
            ValueTask.FromResult(decide(input));
    }

    private sealed class ThrowingHook : IAgentPreStepHook
    {
        public ValueTask<PreStepDecision> BeforeStepAsync(PreStepInput input, CancellationToken ct) =>
            throw new InvalidOperationException("hook exploded");
    }

    #endregion

    #region projector / export / fork

    private static List<AgentSessionEvent> SampleStream() => new()
    {
        new(0, DateTimeOffset.UtcNow, AgentEventKind.TurnStart, 1, 0, new TurnStarted(1)),
        new(1, DateTimeOffset.UtcNow, AgentEventKind.UserMessage, 1, 0, new UserMessageReceived("问题一")),
        new(2, DateTimeOffset.UtcNow, AgentEventKind.AssistantMessage, 1, 1,
            new AssistantReply("", HasToolCalls: true, IsFinalReport: false)),
        new(3, DateTimeOffset.UtcNow, AgentEventKind.ToolCall, 1, 1,
            new ToolCallRequested("c-1", "page_click", "{}")),
        new(4, DateTimeOffset.UtcNow, AgentEventKind.ToolResult, 1, 1,
            new ToolCallCompleted("c-1", "page_click", Success: true, "clicked")),
        new(5, DateTimeOffset.UtcNow, AgentEventKind.AssistantMessage, 1, 2,
            new AssistantReply("最终回答", HasToolCalls: false, IsFinalReport: true)),
        new(6, DateTimeOffset.UtcNow, AgentEventKind.TurnEnd, 1, 0, new TurnEnded(AgentTurnEndReason.Completed)),
        // Incomplete protocol tail (crash orphan): call without a result must vanish.
        new(7, DateTimeOffset.UtcNow, AgentEventKind.AssistantMessage, 2, 1,
            new AssistantReply("", HasToolCalls: true, IsFinalReport: false)),
        new(8, DateTimeOffset.UtcNow, AgentEventKind.ToolCall, 2, 1,
            new ToolCallRequested("c-2", "packet_query", "{}")),
    };

    [Fact]
    public void Projector_folds_tool_pairs_and_drops_incomplete_protocol_tails()
    {
        var projected = AgentHistoryProjector.Project(SampleStream());

        Assert.Equal(4, projected.Count); // user, assistant+calls, tool, assistant final
        Assert.Equal("user", projected[0].Role);
        Assert.Equal("assistant", projected[1].Role);
        Assert.Single(projected[1].ToolCalls!);
        Assert.Equal("tool", projected[2].Role);
        Assert.Equal("c-1", projected[2].ToolCallId);
        Assert.Equal("assistant", projected[3].Role);
        // The crash-orphan call never became an OpenAI-invalid dangling tool_calls.
        Assert.Equal("assistant", projected[^1].Role);
    }

    [Fact]
    public void Export_markdown_contains_transcript_sections_and_metadata()
    {
        var markdown = AgentTranscriptExporter.BuildMarkdown("渗透测试会话", DateTimeOffset.Parse("2026-01-02T03:04:05Z"), SampleStream());

        Assert.Contains("# Hackermes 会话导出 — 渗透测试会话", markdown, StringComparison.Ordinal);
        Assert.Contains("### 🧑 操作者", markdown, StringComparison.Ordinal);
        Assert.Contains("#### 🔧 page_click — 成功", markdown, StringComparison.Ordinal);
        Assert.Contains("### ✅ 执行报告", markdown, StringComparison.Ordinal);
        Assert.Contains("回合结束：Completed", markdown, StringComparison.Ordinal);
        Assert.Contains("2026-01-02", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Fork_copies_the_stream_and_leaves_the_source_untouched()
    {
        var store = new AgentEventLogStore(() => _dataDir);
        foreach (var @event in SampleStream()) store.Append(SessionId, @event);
        var forkId = Guid.NewGuid().ToString("N");

        Assert.True(store.Fork(SessionId, forkId));
        var source = store.Load(SessionId);
        var forked = store.Load(forkId);
        Assert.Equal(source.Count, forked.Count);
        Assert.Equal(source[^1].Kind, forked[^1].Kind);
        for (var index = 0; index < source.Count; index++)
            Assert.Equal(source[index].Kind, forked[index].Kind);

        // Forking a missing/invalid id fails cleanly.
        Assert.False(store.Fork(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")));
        Assert.False(store.Fork("bad-id", Guid.NewGuid().ToString("N")));
    }

    #endregion

    #region LLM session titles

    [Fact]
    public async Task Title_maker_normalizes_model_output()
    {
        var title = await AgentSessionTitleMaker.SuggestAsync(
            new StaticTitleClient("\"端口扫描报告\"\n附加说明不该出现"), "m", "帮我扫一下内网网关");
        Assert.NotNull(title);
        Assert.StartsWith("端口扫描报告", title, StringComparison.Ordinal);
        Assert.DoesNotContain("附加说明", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Title_maker_fails_silently_on_provider_errors()
    {
        var failing = new ScriptedClient().RespondAlways(
            _ => throw new HttpRequestException("provider down"));
        var title = await AgentSessionTitleMaker.SuggestAsync(failing, "m", "任何输入");
        Assert.Null(title);
    }

    [Fact]
    public async Task Title_maker_times_out_rather_than_hanging()
    {
        var slow = new SlowTitleClient();
        var title = await AgentSessionTitleMaker.SuggestAsync(slow, "m", "慢供应商");
        Assert.Null(title);
    }

    private sealed class StaticTitleClient(string reply) : IOpenAiChatClient
    {
        public StaticTitleClient() : this("标题") { }
        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatStreamDelta(reply, null, "stop");
        }
    }

    private sealed class SlowTitleClient : IOpenAiChatClient
    {
        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            yield return new ChatStreamDelta("太迟了", null, "stop");
        }
    }

    #endregion

    /// <summary>Minimal streaming stub shared by this file's tests.</summary>
    private sealed class ScriptedClient : IOpenAiChatClient
    {
        public sealed class Responder
        {
            public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> Handler { get; init; } =
                _ => Array.Empty<ChatStreamDelta>();
            public List<IReadOnlyList<ChatMessage>> Snapshots { get; } = [];
        }

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

            responder.Snapshots.Add(request.Messages);
            IReadOnlyList<ChatStreamDelta> deltas;
            try
            {
                deltas = responder.Handler(request.Messages);
            }
            catch (HttpRequestException) { throw; }
            catch (Exception ex) { throw new StreamFault(ex.Message, ex); }
            foreach (var delta in deltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
            }
        }

        public sealed class StreamFault(string message, Exception inner) : Exception(message, inner);
    }
}
