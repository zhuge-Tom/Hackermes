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
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Session event-log persistence (dsh log-as-truth lineage): JSONL round-trips for every
/// durable kind, and end-to-end resume — a second runner rebuilds history, ACP compaction
/// blocks and approval audits purely from the persisted stream.
/// </summary>
public sealed class AgentEventLogStoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hackermes-evlog-" + Guid.NewGuid().ToString("N"));
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private AgentEventLogStore CreateStore() => new(() => _dataDir);

    [Fact]
    public void Every_durable_kind_round_trips_through_jsonl()
    {
        var store = CreateStore();
        var original = new List<AgentSessionEvent>
        {
            new(0, DateTimeOffset.UnixEpoch, AgentEventKind.TurnStart, 1, 0, new TurnStarted(1)),
            new(1, DateTimeOffset.UtcNow, AgentEventKind.UserMessage, 1, 0,
                new UserMessageReceived("你好 \"引号\"\n换行", Steered: false, Priority: false, Injected: true)),
            new(2, DateTimeOffset.UtcNow, AgentEventKind.AssistantMessage, 1, 1,
                new AssistantReply("回答正文", HasToolCalls: true, IsFinalReport: false)),
            new(3, DateTimeOffset.UtcNow, AgentEventKind.ToolCall, 1, 1,
                new ToolCallRequested("c-1", "page_click", "{\"sel\":\"#a\"}")),
            new(4, DateTimeOffset.UtcNow, AgentEventKind.ToolResult, 1, 1,
                new ToolCallCompleted("c-1", "page_click", Success: false, "失败详情")),
            new(5, DateTimeOffset.UtcNow, AgentEventKind.Usage, 1, 1,
                new UsageRecorded(new StreamUsage(120, 30, 150))),
            new(6, DateTimeOffset.UtcNow, AgentEventKind.RequestRetry, 1, 1,
                new RequestRetried(1, 2, "connection reset", TimeSpan.FromMilliseconds(700))),
            new(7, DateTimeOffset.UtcNow, AgentEventKind.ContextCompacted, 1, 2,
                new ContextCompacted(4_200, "[m00003]–[m00009]", Automatic: true, Warning: "质量门提醒", Summary: "压缩摘要内容")),
            new(8, DateTimeOffset.UtcNow, AgentEventKind.ApprovalAudited, 1, 2,
                new ApprovalAudited(new AiToolAuditRecord(DateTimeOffset.UtcNow, "packet_replay", SessionId, "page-1",
                    "ApprovedWithSessionGrant", "reason text"))),
            new(9, DateTimeOffset.UtcNow, AgentEventKind.TurnEnd, 1, 0,
                new TurnEnded(AgentTurnEndReason.LengthCapped, null)),
        };
        foreach (var @event in original) store.Append(SessionId, @event);

        Assert.True(store.Exists(SessionId));
        var loaded = store.Load(SessionId);
        Assert.Equal(original.Count, loaded.Count);
        for (var index = 0; index < loaded.Count; index++)
        {
            Assert.Equal(index, loaded[index].Seq);
            Assert.Equal(original[index].Kind, loaded[index].Kind);
            Assert.Equal(original[index].Turn, loaded[index].Turn);
            Assert.Equal(original[index].Step, loaded[index].Step);
        }
        Assert.Equal(((UserMessageReceived)original[1].Data).Text, ((UserMessageReceived)loaded[1].Data).Text);
        Assert.True(((UserMessageReceived)loaded[1].Data).Injected);
        Assert.Equal("{\"sel\":\"#a\"}", ((ToolCallRequested)loaded[3].Data).ArgumentsJson);
        Assert.False(((ToolCallCompleted)loaded[4].Data).Success);
        var usage = Assert.IsType<UsageRecorded>(loaded[5].Data);
        Assert.Equal((120, 30, 150), (usage.Usage.PromptTokens, usage.Usage.CompletionTokens, usage.Usage.TotalTokens));
        Assert.Equal(TimeSpan.FromMilliseconds(700), ((RequestRetried)loaded[6].Data).Delay);
        var compacted = Assert.IsType<ContextCompacted>(loaded[7].Data);
        Assert.Equal("压缩摘要内容", compacted.Summary);
        Assert.Equal("ApprovedWithSessionGrant", ((ApprovalAudited)loaded[8].Data).Record.Decision);
        Assert.Equal(AgentTurnEndReason.LengthCapped, ((TurnEnded)loaded[9].Data).Reason);
    }

    [Fact]
    public void Invalid_session_ids_are_rejected_and_corrupt_lines_skipped()
    {
        var store = CreateStore();
        Assert.Throws<ArgumentException>(() => store.Append("../../evil", default!));
        Assert.Throws<ArgumentException>(() => store.Append("short", default!));

        store.Append(SessionId, new AgentSessionEvent(0, DateTimeOffset.UtcNow, AgentEventKind.TurnStart, 1, 0, new TurnStarted(1)));
        File.AppendAllText(Path.Combine(_dataDir, "agent-events", SessionId + ".jsonl"), "{not json}\n");
        store.Append(SessionId, new AgentSessionEvent(1, DateTimeOffset.UtcNow, AgentEventKind.TurnEnd, 1, 0,
            new TurnEnded(AgentTurnEndReason.Completed, null)));

        var loaded = store.Load(SessionId);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(AgentEventKind.TurnEnd, loaded[^1].Kind);
    }

    [Fact]
    public async Task Resume_rebuilds_history_compaction_blocks_and_audits_from_the_log()
    {
        // --- First runner: a full turn with a manual context_compress call. ---
        var store = CreateStore();
        var acpFirst = new AcpContextStore(() => "system prompt", 20_000);
        SeedAcpConversation(acpFirst);

        var toolsFirst = new AiToolRegistry();
        toolsFirst.Register(new AiToolDefinition(
            "mutating_step", "tool", JsonSerializer.SerializeToElement(new { }), AiToolRisk.Mutating,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("ran once"))));
        var acpRegistry = new AcpContextRegistry { Current = acpFirst };
        new AcpToolAdapter(acpRegistry).RegisterAll(toolsFirst);
        var confirmation = new ApprovingConfirmation();

        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "cc-1", "context_compress",
                    """{"ranges":[{"start":"m00001","end":"m00002","summary":"早期区间摘要","title":"背景"}]}"""), null),
                new ChatStreamDelta(null, new ToolCallDelta(1, "mt-1", "mutating_step", "{}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("全部完成。", null, "stop")]);

        var firstRunner = NewRunner(client, toolsFirst, confirmation, store, acpFirst);
        firstRunner.Strategy = new AcpContextStrategy(acpFirst);

        await firstRunner.RunTurnAsync("开始任务", "m", "page-1", SessionId, CancellationToken.None);

        Assert.True(store.Load(SessionId).Count > 5);
        var toolResults = string.Join(" | ", store.Load(SessionId)
            .Where(@event => @event.Kind == AgentEventKind.ToolResult)
            .Select(@event => ((ToolCallCompleted)@event.Data).Content[..Math.Min(120, ((ToolCallCompleted)@event.Data).Content.Length)]));
        Assert.True(acpFirst.Blocks.Count == 1,
            $"blocks={acpFirst.Blocks.Count} entries={acpFirst.ActiveEntries.Count} results=[{toolResults}] " +
            $"firstRefs={string.Join(",", acpFirst.ActiveEntries.Take(3).Select(entry => entry.Ref))}");

        // --- Second runner: pure replay must reproduce equivalent state. ---
        var events = store.Load(SessionId);
        var acpSecond = new AcpContextStore(() => "system prompt", 20_000);
        SeedAcpConversation(acpSecond);
        var secondRunner = NewRunner(
            new ScriptedClient().Respond(_ => [new ChatStreamDelta("第二轮完成。", null, "stop")]),
            new AiToolRegistry(), new ApprovingConfirmation(), store, acpSecond);
        secondRunner.Strategy = new AcpContextStrategy(acpSecond);
        secondRunner.Replay(events);

        // History equivalence: user turn + tool protocol + final report all re-entered.
        Assert.Contains(secondRunner.History, message => message.Role == "user" && message.Content == "开始任务");
        Assert.Contains(secondRunner.History, message => message.Role == "assistant" && message.ToolCalls is { Count: 2 });
        Assert.Contains(secondRunner.History, message => message.Role == "tool" && message.Content == "ran once");
        Assert.Contains(secondRunner.History, message => message.Role == "assistant" && message.Content == "全部完成。");
        // The compression block was rebuilt verbatim from the tool-call arguments.
        Assert.True(acpSecond.Blocks.Count == 1,
            $"blocks={acpSecond.Blocks.Count} history={string.Join("/", secondRunner.History.Select(message => message.Role))} " +
            $"kinds={string.Join(",", events.Select(@event => @event.Kind.ToString()).Distinct())}");
        Assert.Equal(acpFirst.Blocks[0].Summary, acpSecond.Blocks[0].Summary);
        Assert.Equal(acpFirst.Blocks[0].Title, acpSecond.Blocks[0].Title);
        // Turn counter advanced so the next live turn keeps incrementing.
        var nextReason = await secondRunner.RunTurnAsync("再来一轮", "m", null, SessionId, CancellationToken.None);
        Assert.Equal(AgentTurnEndReason.Completed, nextReason);
        var turns = store.Load(SessionId).Where(@event => @event.Kind == AgentEventKind.TurnStart)
            .Select(@event => ((TurnStarted)@event.Data).Turn).ToArray();
        Assert.Equal([1, 2], turns);
    }

    private static void SeedAcpConversation(AcpContextStore store)
    {
        for (var index = 0; index < 6; index++)
        {
            var filler = new string((char)('a' + index), 1_450);
            if (index % 2 == 0) store.AppendUser($"问题 {index}: {filler}");
            else store.AppendAssistant($"回答 {index}: {filler}");
        }
    }

    private static AgentTurnRunner NewRunner(
        IOpenAiChatClient client,
        IAiToolRegistry tools,
        IToolConfirmationService confirmation,
        AgentEventLogStore store,
        AcpContextStore acp)
    {
        return new AgentTurnRunner(
            client,
            new AiToolDispatcher(tools, new DefaultToolPolicyGate(), confirmation),
            () => new AiSettings { MaxContextCharacters = 20_000 },
            () => new AgentMemoryDocument(),
            () => [],
            new AgentTurnRunnerOptions { ToolSelector = () => tools.All },
            logger: null,
            eventLogProvider: () => store);
    }

    private sealed class ApprovingConfirmation : IToolConfirmationService
    {
        public ValueTask<ToolConfirmation> ConfirmAsync(ToolInvocation invocation, string reason, CancellationToken ct) =>
            ValueTask.FromResult(new ToolConfirmation(true, RememberForSession: true));
    }

    private sealed class ScriptedClient : IOpenAiChatClient
    {
        public sealed class Responder
        {
            public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> Handler { get; init; } =
                _ => Array.Empty<ChatStreamDelta>();
        }

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
            foreach (var delta in responder.Handler(request.Messages))
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
            }
        }
    }
}
