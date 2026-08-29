using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Supplies model-visible messages for one request and receives the durable message feeds,
/// so the runner stays ignorant of which context manager (ACP store or legacy compactor)
/// owns the window. Mirrors dsh's seam between the agent loop and prompt assembly.
/// </summary>
public interface IAgentContextStrategy
{
    IReadOnlyList<ChatMessage> BuildRequest(
        IReadOnlyList<ChatMessage> history,
        AgentMemoryDocument memory,
        IReadOnlyList<AgentSkill> skills,
        AiSettings settings);

    void OnUser(string text) { }
    void OnAssistant(string? content) { }
    void OnAssistantToolCalls(string? content, IReadOnlyList<AssistantToolCall> toolCalls) { }
    void OnToolResult(string callId, string content, string? toolName) { }
    void OnToolResult(string callId, string content, string? toolName, IReadOnlyList<ChatImage>? images) =>
        OnToolResult(callId, content, toolName);

    /// <summary>Optional one-line usage status for the chat status bar.</summary>
    string? DescribeUsage(AiSettings settings) => null;
}

/// <summary>Legacy deterministic compaction strategy: request assembly only.</summary>
public sealed class CompactorContextStrategy(AgentContextCompactor compactor) : IAgentContextStrategy
{
    public IReadOnlyList<ChatMessage> BuildRequest(
        IReadOnlyList<ChatMessage> history,
        AgentMemoryDocument memory,
        IReadOnlyList<AgentSkill> skills,
        AiSettings settings) =>
        compactor.BuildRequest(history, memory, skills, settings);
}

/// <summary>ACP strategy: the store owns both the window and the compression tool surface.</summary>
public sealed class AcpContextStrategy : IAgentContextStrategy
{
    public AcpContextStore Store { get; }

    public AcpContextStrategy(AcpContextStore store)
    {
        Store = store;
    }

    public IReadOnlyList<ChatMessage> BuildRequest(
        IReadOnlyList<ChatMessage> history,
        AgentMemoryDocument memory,
        IReadOnlyList<AgentSkill> skills,
        AiSettings settings) =>
        Store.BuildRequest(memory, skills, settings);

    public void OnUser(string text) => Store.AppendUser(text);
    public void OnAssistant(string? content) => Store.AppendAssistant(content);
    public void OnAssistantToolCalls(string? content, IReadOnlyList<AssistantToolCall> toolCalls) =>
        Store.AppendAssistantToolCalls(content, toolCalls);
    public void OnToolResult(string callId, string content, string? toolName) =>
        Store.AppendToolResult(callId, content, toolName);

    public void OnToolResult(string callId, string content, string? toolName, IReadOnlyList<ChatImage>? images) =>
        Store.AppendToolResult(callId, content, toolName, images);
    public string? DescribeUsage(AiSettings settings) =>
        Store.UsageLine(AcpContextStore.EffectiveBudget(settings));
}

public sealed class AgentTurnRunnerOptions
{
    /// <summary>Narrows the tool surface per request (workflow skills); null means every registered tool.</summary>
    public Func<IReadOnlyList<AiToolDefinition>>? ToolSelector { get; set; }

    /// <summary>Pressure-triggered context compaction consulted before each request build.</summary>
    public AcpAutoCompactor? AutoCompactor { get; set; }

    /// <summary>Model-request retry attempts after a clean transport failure. dsh default: 2.</summary>
    public int MaxRequestRetries { get; set; } = 2;

    /// <summary>Backoff for retry attempt n (0-based); defaults to the dsh curve 0.5s→10s with jitter.</summary>
    public Func<int, TimeSpan>? RetryBackoff { get; set; }

    /// <summary>Raised when a turn opens — used to roll over transient surfaces (todo checklist).</summary>
    public Action? TurnStarting { get; set; }

    /// <summary>Session objective (dsh goal lineage): drives automatic continuation rounds.</summary>
    public AgentGoalRegistry? Goals { get; set; }

    /// <summary>Pre-step interception waterfall, evaluated in order before every model call.</summary>
    public IReadOnlyList<IAgentPreStepHook> PreStepHooks { get; set; } = [];

    /// <summary>Sidecar evidence that survives compaction; observed on every tool commit.</summary>
    public AgentEvidenceLedger? Evidence { get; set; }
}

/// <summary>
/// Headless turn/step driver ported from deepseek-harness's ReactLoopAgent: a turn opens for
/// each operator input and runs steps (one model request plus its tool executions) until the
/// model stops calling tools and the steering inbox drains. Everything observable is appended
/// to <see cref="Log"/> (and optionally persisted through <see cref="AgentEventLogStore"/>);
/// the transcript view models subscribe instead of being mutated inline.
///
/// Ported dsh semantics: instructions arriving mid-turn are claimed at step boundaries
/// (steering), operator-promoted instructions preempt remaining tool calls of the current
/// step, tool results always commit in model order regardless of completion order, aborted
/// steps still leave a complete call/result protocol in the log, clean request failures retry
/// with bounded backoff while terminal ones fail fast, context-overflow failures recover via
/// forced compaction, and tools can inject next-step context or conclude the turn.
/// </summary>
public sealed class AgentTurnRunner
{
    private const int MaximumParallelReadOnlyTools = 8;
    private const string PreemptedToolNotice =
        "操作者提交了优先指示；此工具调用未执行，控制权将在本轮协议安全收尾后转向新指示。";
    private const string AbortedToolNotice =
        "工具调用因操作者停止而中止；此为协议占位结果，未产生任何实际效果。如需继续请重新发起。";

    private readonly IOpenAiChatClient _client;
    private readonly AiToolDispatcher _dispatcher;
    private readonly Func<AiSettings> _settings;
    private readonly Func<AgentMemoryDocument> _memory;
    private readonly Func<IReadOnlyList<AgentSkill>> _skills;
    private readonly AgentTurnRunnerOptions _options;
    private readonly IAppLogger? _logger;
    /// <summary>Resolved per append so runtime setting changes take effect on the next event.</summary>
    private readonly Func<AgentEventLogStore?>? _eventLogProvider;
    private readonly List<InboxInstruction> _inbox = [];
    private readonly object _stepAbortGate = new();
    private CancellationTokenSource? _stepAbort;
    private int _turnCounter;
    private string? _currentSessionId;
    private string? _turnPageId;

    public AgentTurnRunner(
        IOpenAiChatClient client,
        AiToolDispatcher dispatcher,
        Func<AiSettings> settings,
        Func<AgentMemoryDocument> memory,
        Func<IReadOnlyList<AgentSkill>> skills,
        AgentTurnRunnerOptions? options = null,
        IAppLogger? logger = null,
        Func<AgentEventLogStore?>? eventLogProvider = null)
    {
        _client = client;
        _dispatcher = dispatcher;
        _settings = settings;
        _memory = memory;
        _skills = skills;
        _options = options ?? new AgentTurnRunnerOptions();
        _logger = logger?.ForCategory(nameof(AgentTurnRunner));
        _eventLogProvider = eventLogProvider;
    }

    /// <summary>Durable event stream for this session's turns.</summary>
    public AgentSessionLog Log { get; } = new();

    /// <summary>Model-facing history (legacy path input and persistence export).</summary>
    public List<ChatMessage> History { get; } = [];

    /// <summary>Context manager selected by the host before each turn; falls back to the legacy compactor.</summary>
    public IAgentContextStrategy? Strategy { get; set; }

    /// <summary>Seeds restored conversation without emitting events (startup restore path).</summary>
    public void SeedHistory(IEnumerable<ChatMessage> messages) => History.AddRange(messages);

    #region Steering inbox

    private sealed record InboxInstruction(string Text, bool Priority, bool Injected);

    public int PendingInstructionCount { get { lock (_inbox) return _inbox.Count; } }

    public string? PeekNextInstruction()
    {
        lock (_inbox) return _inbox.Count == 0 ? null : _inbox[0].Text;
    }

    public bool IsNextInstructionPriority()
    {
        lock (_inbox) return _inbox.Count > 0 && _inbox[0].Priority;
    }

    public void EnqueueInstruction(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_inbox) _inbox.Add(new InboxInstruction(text.Trim(), false, Injected: false));
    }

    private void InjectContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_inbox) _inbox.Add(new InboxInstruction(text.Trim(), false, Injected: true));
    }

    /// <summary>Moves the latest queued instruction to the front as a priority steer.</summary>
    public bool PromoteLatestInstruction()
    {
        lock (_inbox)
        {
            // Operator promotion jumps the queue, including ahead of injected goal rounds.
            var candidates = _inbox.Where(instruction => !instruction.Injected).ToList();
            if (candidates.Count == 0) return false;
            var latest = candidates[^1] with { Priority = true };
            _inbox.Remove(candidates[^1]);
            _inbox.Insert(0, latest);
        }
        lock (_stepAbortGate) _stepAbort?.Cancel();
        return true;
    }

    public bool DropNextInstruction()
    {
        lock (_inbox)
        {
            if (_inbox.Count == 0) return false;
            _inbox.RemoveAt(0);
            return true;
        }
    }

    private bool TryClaimInstruction(out InboxInstruction instruction)
    {
        lock (_inbox)
        {
            if (_inbox.Count == 0)
            {
                instruction = null!;
                return false;
            }
            instruction = _inbox[0];
            _inbox.RemoveAt(0);
            return true;
        }
    }

    #endregion

    #region Durable emission

    private void Emit(AgentEventKind kind, int turn, int step, AgentEventData data)
    {
        var @event = Log.Append(kind, turn, step, data);
        if (_currentSessionId is not { Length: > 0 } session || !IsPersistable(kind)) return;
        var store = _eventLogProvider?.Invoke();
        store?.Append(session, @event);
    }

    private static bool IsPersistable(AgentEventKind kind) =>
        kind is not (AgentEventKind.AssistantChunk or AgentEventKind.ReasoningChunk);

    /// <summary>Bridges dispatcher approval audits into this session's durable log.</summary>
    public void AppendAudit(AiToolAuditRecord record) =>
        Emit(AgentEventKind.ApprovalAudited, _turnCounter, 0, new ApprovalAudited(record));

    #endregion

    /// <summary>
    /// Runs one turn to completion. Never throws for request failures — the outcome is
    /// reported through <see cref="AgentEventKind.TurnEnd"/>; operator cancellation maps
    /// to <see cref="AgentTurnEndReason.Aborted"/>.
    /// </summary>
    public async Task<AgentTurnEndReason> RunTurnAsync(
        string userText,
        string model,
        string? pageId,
        string? sessionId,
        CancellationToken ct)
    {
        _currentSessionId = sessionId;
        _turnPageId = pageId;
        var turn = ++_turnCounter;
        Emit(AgentEventKind.TurnStart, turn, 0, new TurnStarted(turn));
        var reason = AgentTurnEndReason.Completed;
        string? errorDetail = null;
        // Sticky length cap (dsh max-tokens semantics): once any step was cut off by the
        // provider's per-reply limit, later clean steps do not downgrade the verdict.
        var lengthCapped = false;
        try
        {
            var strategy = Strategy ?? new CompactorContextStrategy(new AgentContextCompactor());
            _options.TurnStarting?.Invoke();
            History.Add(new ChatMessage("user", userText));
            strategy.OnUser(userText);
            Emit(AgentEventKind.UserMessage, turn, 0, new UserMessageReceived(userText));

            var maxRounds = Math.Clamp(_settings().MaxToolRounds, 1, 256);
            var step = 0;
            // Messages claimed for the NEXT step (steering/goal/injected) are held here and
            // appended only after the pre-step waterfall accepts them (dsh pre-step contract).
            List<InboxInstruction>? entering = null;
            for (var round = 1; ; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (round > maxRounds)
                {
                    reason = AgentTurnEndReason.MaxRounds;
                    var limitMessage = $"已达到 {maxRounds} 轮工具调用上限，任务停止继续调用工具。";
                    History.Add(new ChatMessage("assistant", limitMessage));
                    strategy.OnAssistant(limitMessage);
                    Emit(AgentEventKind.AssistantMessage, turn, step,
                        new AssistantReply(limitMessage, HasToolCalls: false, IsFinalReport: false));
                    break;
                }

                step++;
                Emit(AgentEventKind.StepStart, turn, step, new StepStarted(turn, step));
                StepOutcome outcome;
                IReadOnlyList<ChatMessage>? ephemeralAppendix = null;
                try
                {
                    // Pre-step waterfall: reject ends the turn as Blocked without spending a
                    // model call; rewrites replace the entering messages; ephemeral appendices
                    // ride this request only.
                    var appendix = await RunPreStepWaterfallAsync(strategy, turn, step, entering, ct)
                        .ConfigureAwait(true);
                    if (appendix.Rejected)
                    {
                        reason = AgentTurnEndReason.Blocked;
                        errorDetail = appendix.RejectReason;
                        break;
                    }
                    foreach (var instruction in entering ?? [])
                        AppendEnteringMessage(strategy, turn, step, instruction);
                    ephemeralAppendix = appendix.Messages;
                    entering = null;

                    outcome = await ExecuteStepAsync(strategy, turn, step, model, _turnPageId, sessionId,
                        ephemeralAppendix, ct).ConfigureAwait(true);
                }
                finally
                {
                    Emit(AgentEventKind.StepEnd, turn, step, new StepFinished(turn, step));
                }

                if (outcome.Failed)
                {
                    reason = AgentTurnEndReason.Error;
                    errorDetail = outcome.ErrorText;
                    break;
                }
                if (outcome.LengthCapped) lengthCapped = true;

                // Steering claims happen exactly here: after a step settles, before the next
                // decision — the dsh inbox boundary between steps. A concluded turn still
                // drains claimed steering before closing (dsh conclusion semantics).
                var claimed = TryClaimInstruction(out var queued);

                // Goal continuation (dsh goal-round driver): an unfinished objective keeps the
                // turn going with a synthetic round message, up to the registry's round cap.
                if (!claimed && _options.Goals is { } goals && goals.TryBeginRound(out var goalRound))
                {
                    InjectContext(goalRound);
                    claimed = TryClaimInstruction(out queued);
                }
                if (claimed) (entering ??= []).Add(queued);

                if ((!outcome.HasToolCalls || outcome.Concluded) && entering is null)
                {
                    if (lengthCapped) reason = AgentTurnEndReason.LengthCapped;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = AgentTurnEndReason.Aborted;
        }
        catch (Exception ex)
        {
            _logger?.Error("Agent turn failed unexpectedly.", ex);
            reason = AgentTurnEndReason.Error;
            errorDetail = ex.Message;
        }
        finally
        {
            Emit(AgentEventKind.TurnEnd, turn, 0,
                new TurnEnded(reason,
                    reason is AgentTurnEndReason.Error or AgentTurnEndReason.Blocked ? errorDetail : null));
        }
        return reason;
    }

    private readonly record struct StepOutcome(
        bool HasToolCalls,
        bool Failed,
        string? ErrorText,
        bool LengthCapped,
        bool Concluded);

    private async Task<StepOutcome> ExecuteStepAsync(
        IAgentContextStrategy strategy,
        int turn,
        int step,
        string model,
        string? pageId,
        string? sessionId,
        IReadOnlyList<ChatMessage>? ephemeralAppendix,
        CancellationToken ct)
    {
        var tools = _options.ToolSelector?.Invoke() ?? [];
        // Pressure-triggered auto-compaction lands before request assembly so the rebuilt
        // window is what the model actually sees (dsh compacts inside agent/pre-step).
        if (_options.AutoCompactor is { } compactor)
        {
            var compacted = await compactor.CompactIfNeededAsync(ct).ConfigureAwait(true);
            if (compacted is not null)
                Emit(AgentEventKind.ContextCompacted, turn, step, compacted);
        }

        var settings = _settings();
        var messages = strategy.BuildRequest(History, _memory(), _skills(), settings);
        LogRequestHeaderEpoch(turn, step, model);

        // Retry loop: only clean failures (nothing streamed yet) restart the request, so a
        // partially rendered answer is never duplicated — mirrors dsh's assembler contract.
        // Transient shapes retry with backoff; context overflow gets up to 3 recovery attempts
        // (forced compaction, or head-trim on the legacy strategy); terminal failures end
        // the turn immediately.
        StringBuilder content = new();
        var calls = new Dictionary<int, StreamingToolCall>();
        var attempt = 0;
        var overflowRetries = 0;
        string? finishReason = null;
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_stepAbortGate) _stepAbort = stepCts;
        try
        {
        while (true)
        {
            var receivedAnything = false;
            string? attemptFinish = null;
            try
            {
                var outgoing = ephemeralAppendix is { Count: > 0 }
                    ? messages.Concat(ephemeralAppendix).ToArray()
                    : messages;
                var request = new OpenAiChatRequest(model, outgoing, tools);
                await foreach (var delta in _client.StreamChatAsync(request, stepCts.Token).ConfigureAwait(true))
                {
                    receivedAnything = true;
                    if (delta.Reasoning is { } reasoningText)
                        Emit(AgentEventKind.ReasoningChunk, turn, step, new ReasoningDelta(reasoningText));
                    if (delta.Content is { } text)
                    {
                        content.Append(text);
                        Emit(AgentEventKind.AssistantChunk, turn, step, new AssistantDelta(text));
                    }
                    if (delta.FinishReason is { } finished) attemptFinish = finished;
                    if (delta.Usage is { } usage)
                        Emit(AgentEventKind.Usage, turn, step, new UsageRecorded(usage));
                    if (delta.ToolCall is { } part)
                    {
                        if (!calls.TryGetValue(part.Index, out var call))
                            calls[part.Index] = call = new StreamingToolCall();
                        if (!string.IsNullOrEmpty(part.Id)) call.Id = part.Id;
                        if (!string.IsNullOrEmpty(part.Name)) call.Name.Append(part.Name);
                        if (!string.IsNullOrEmpty(part.Arguments)) call.Arguments.Append(part.Arguments);
                    }
                }
                finishReason = attemptFinish ?? finishReason ?? "stop";
                break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Operator 优先执行 aborted this in-flight request; the turn claims the inbox next.
                return new StepOutcome(HasToolCalls: false, Failed: false, ErrorText: null,
                    LengthCapped: false, Concluded: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!receivedAnything &&
                                       overflowRetries < 3 &&
                                       AgentRequestError.IsContextOverflow(ex))
            {
                // dsh compaction-basic overflow path: force a useful reduction and rebuild
                // the request against it (up to 3 times). ACP recovers through the
                // compactor; the legacy strategy trims its oldest completed turn instead.
                overflowRetries++;
                var recovered = _options.AutoCompactor is { } autoCompactor
                    ? await autoCompactor.CompactNowAsync(ct).ConfigureAwait(true)
                    : null;
                recovered ??= strategy is CompactorContextStrategy ? TrimOldestLegacyTurn() : null;
                if (recovered is null) throw;
                Emit(AgentEventKind.ContextCompacted, turn, step, recovered);
                messages = strategy.BuildRequest(History, _memory(), _skills(), settings);
            }
            catch (Exception ex) when (attempt < _options.MaxRequestRetries &&
                                       AgentRequestError.IsTransient(ex) &&
                                       (!receivedAnything || AgentRequestError.IsPrematureStreamEnd(ex)))
            {
                attempt++;
                content.Clear();
                calls.Clear();
                var delay = BackoffDelay(attempt - 1);
                Emit(AgentEventKind.RequestRetry, turn, step,
                    new RequestRetried(attempt, _options.MaxRequestRetries, ex.Message, delay));
                _logger?.Warn($"模型请求瞬态失败（{attempt}/{_options.MaxRequestRetries}），{delay.TotalMilliseconds:0} ms 后重试：{ex.Message}");
                await Task.Delay(delay, ct).ConfigureAwait(true);
            }
        }

        // dsh assembler rule: a length-capped reply drops unfinished tool-call fragments
        // entirely — they were never fully requested by the provider and cannot be honored.
        if (finishReason == "length")
        {
            calls.Clear();
        }

        if (calls.Count == 0)
        {
            var reply = content.ToString();
            History.Add(new ChatMessage("assistant", reply));
            strategy.OnAssistant(reply);
            var isFinalReport = PendingInstructionCount == 0 && finishReason != "length";
            Emit(AgentEventKind.AssistantMessage, turn, step,
                new AssistantReply(reply, HasToolCalls: false, IsFinalReport: isFinalReport));
            return new StepOutcome(HasToolCalls: false, Failed: false, ErrorText: null,
                LengthCapped: finishReason == "length", Concluded: false);
        }

        var toolCalls = calls.OrderBy(pair => pair.Key).Select(pair => pair.Value.Build(pair.Key)).ToArray();
        var preamble = content.Length == 0 ? null : content.ToString();
        History.Add(new ChatMessage("assistant", preamble, ToolCalls: toolCalls));
        strategy.OnAssistantToolCalls(preamble, toolCalls);
        Emit(AgentEventKind.AssistantMessage, turn, step,
            new AssistantReply(content.ToString(), HasToolCalls: true, IsFinalReport: false));

        // Every call is logged before any dispatch, then results commit strictly in model order.
        foreach (var call in toolCalls)
            Emit(AgentEventKind.ToolCall, turn, step,
                new ToolCallRequested(call.Id, call.Name, call.Arguments));

        var parallelCap = Math.Clamp(settings.MaxParallelReadOnlyTools, 1, MaximumParallelReadOnlyTools);
        var definitions = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var concluded = await ExecuteToolBatchesAsync(toolCalls, definitions, parallelCap, turn, step, pageId, sessionId, strategy, ct)
            .ConfigureAwait(true);
        return new StepOutcome(HasToolCalls: true, Failed: false, ErrorText: null,
            LengthCapped: finishReason == "length", Concluded: concluded);
        }
        finally
        {
            lock (_stepAbortGate)
            {
                if (ReferenceEquals(_stepAbort, stepCts)) _stepAbort = null;
            }
        }
    }

    /// <summary>
    /// Request-shape epoch (dsh request/header): fingerprint the STABLE request skeleton —
    /// model, permission mode, enabled-workflow signature and the tool list. Volatile
    /// payloads (memory digest, conversation tail) are deliberately excluded: they change
    /// every turn by design, and counting them as drift would drown real cache-invalidating
    /// changes in per-turn noise.
    /// </summary>
    private void LogRequestHeaderEpoch(int turn, int step, string model)
    {
        var settings = _settings();
        var skillSignature = string.Join(";", _skills()
            .Where(skill => skill.Enabled)
            .Select(skill => $"{skill.Name}:{skill.Instructions.Length}"));
        var toolNames = string.Join(",", (_options.ToolSelector?.Invoke() ?? []).Select(tool => tool.Name));
        var payload =
            $"{model}\u0001{settings.PermissionMode}\u0001{settings.AcpEnabled}\u0001{skillSignature}\u0001{toolNames}";
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..24];
        if (string.Equals(fingerprint, _lastHeaderFingerprint, StringComparison.Ordinal)) return;
        Emit(AgentEventKind.RequestHeader, turn, step,
            new RequestHeaderLogged(fingerprint,
                _lastHeaderFingerprint is null ? "initial" : "change",
                model, toolNames.Length == 0 ? 0 : toolNames.Split(',').Length));
        _lastHeaderFingerprint = fingerprint;
    }

    private string? _lastHeaderFingerprint;

    private readonly record struct PreStepOutcome(
        bool Rejected, string? RejectReason, IReadOnlyList<ChatMessage>? Messages);

    /// <summary>
    /// Evaluates the pre-step waterfall in order: first Reject wins; RewriteEntering chains
    /// across hooks (each sees the previous output); AppendEphemeral accumulates. Hook
    /// failures are contained — a throwing hook is skipped, never fatal.
    /// </summary>
    private async Task<PreStepOutcome> RunPreStepWaterfallAsync(
        IAgentContextStrategy strategy,
        int turn,
        int step,
        List<InboxInstruction>? entering,
        CancellationToken ct)
    {
        var hooks = _options.PreStepHooks;
        if (hooks is not { Count: > 0 })
            return new PreStepOutcome(false, null, null);

        var current = (entering ?? []).Select(instruction =>
            new ChatMessage("user", instruction.Text)).ToArray();
        List<ChatMessage>? ephemeral = null;

        foreach (var hook in hooks)
        {
            PreStepDecision decision;
            try
            {
                decision = await hook.BeforeStepAsync(
                    new PreStepInput(turn, step, current), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn($"Pre-step hook {hook.GetType().Name} failed and was skipped: {ex.Message}");
                continue;
            }

            switch (decision)
            {
                case PreStepDecision.RejectDecision reject:
                    return new PreStepOutcome(true, reject.Reason, null);
                case PreStepDecision.RewriteDecision rewrite:
                    current = rewrite.Rewritten.ToArray();
                    break;
                case PreStepDecision.EphemeralDecision append:
                    ephemeral ??= [];
                    ((List<ChatMessage>)ephemeral).AddRange(append.Appendix);
                    break;
            }
        }
        // Persist rewritten entering text back onto the queued instructions so what lands
        // in history/strategy/log is exactly the redacted form.
        if (entering is not null && current.Length == entering.Count)
            for (var index = 0; index < entering.Count; index++)
                if (!string.Equals(entering[index].Text, current[index].Content, StringComparison.Ordinal))
                    entering[index] = entering[index] with { Text = current[index].Content ?? string.Empty };
        return new PreStepOutcome(false, null, ephemeral);
    }

    private void AppendEnteringMessage(IAgentContextStrategy strategy, int turn, int step, InboxInstruction instruction)
    {
        History.Add(new ChatMessage("user", instruction.Text));
        strategy.OnUser(instruction.Text);
        Emit(AgentEventKind.UserMessage, turn, step,
            new UserMessageReceived(instruction.Text, Steered: !instruction.Injected,
                Priority: instruction.Priority, Injected: instruction.Injected));
    }

    /// <summary>dsh overflow fallback for the legacy strategy: drop the oldest completed turn.</summary>
    private ContextCompacted TrimOldestLegacyTurn()    {
        var secondUser = -1;
        var seenFirst = false;
        for (var index = 0; index < History.Count; index++)
        {
            if (History[index].Role != "user") continue;
            if (seenFirst) { secondUser = index; break; }
            seenFirst = true;
        }
        if (secondUser <= 0) return null!;
        var reclaimed = History.Take(secondUser).Sum(message => (message.Content?.Length ?? 0) + 200L);
        History.RemoveRange(0, secondUser);
        return new ContextCompacted(reclaimed, "最旧回合（legacy 窗口修剪）", Automatic: true, Summary: null);
    }

    private TimeSpan BackoffDelay(int zeroBasedAttempt)
    {
        if (_options.RetryBackoff is { } custom) return custom(zeroBasedAttempt);
        // dsh llm-retry defaults: 500ms doubling capped at 10s, ±10% jitter.
        var baseMs = Math.Min(500d * Math.Pow(2, zeroBasedAttempt), 10_000d);
        var jitter = 1d + (Random.Shared.NextDouble() * 0.2d - 0.1d);
        return TimeSpan.FromMilliseconds(Math.Max(50, baseMs * jitter));
    }

    private async Task<bool> ExecuteToolBatchesAsync(
        IReadOnlyList<AssistantToolCall> calls,
        Dictionary<string, AiToolDefinition> definitions,
        int parallelCap,
        int turn,
        int step,
        string? pageId,
        string? sessionId,
        IAgentContextStrategy strategy,
        CancellationToken ct)
    {
        static bool CanParallelize(AssistantToolCall call, Dictionary<string, AiToolDefinition> defs) =>
            defs.TryGetValue(call.Name, out var definition) && definition.Risk == AiToolRisk.ReadOnly;

        // Single commit funnel: history, context feed, durable log, injected contexts and the
        // conclude flag move together, so cancellation can fill exactly the gaps that remain.
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var concluded = false;

        void Commit(AssistantToolCall call, ToolResult result)
        {
            History.Add(new ChatMessage("tool", result.Content, ToolCallId: call.Id, Images: result.Images));
            strategy.OnToolResult(call.Id, result.Content, call.Name, result.Images);
            _options.Evidence?.Observe(call.Name, result.Content, result.Success);
            Emit(AgentEventKind.ToolResult, turn, step,
                new ToolCallCompleted(call.Id, call.Name, result.Success, result.Content));
            committed.Add(call.Id);
            // dsh additionalContexts land in the NEXT-step inbox, not in this result.
            if (result.AdditionalContexts is { Count: > 0 } contexts)
                foreach (var context in contexts) InjectContext(context);
            concluded |= result.ConcludesTurn;
        }

        try
        {
            var index = 0;
            while (index < calls.Count)
            {
                ct.ThrowIfCancellationRequested();
                if (parallelCap > 1 && CanParallelize(calls[index], definitions))
                {
                    var end = index + 1;
                    while (end < calls.Count && CanParallelize(calls[end], definitions)) end++;
                    await ExecuteParallelPoolAsync(calls, index, end, parallelCap, pageId, sessionId, Commit, ct)
                        .ConfigureAwait(true);
                    index = end;
                }
                else
                {
                    var result = await InvokeToolAsync(calls[index], pageId, sessionId, ct).ConfigureAwait(true);
                    Commit(calls[index], result);
                    index++;
                }
            }
            return concluded;
        }
        catch (OperationCanceledException)
        {
            // Protocol completion (dsh skipped-tool-call semantics): every logged tool/call
            // must end with a matching result so replay and the next request stay valid —
            // an orphaned assistant tool_calls message would make the next API call fail.
            foreach (var orphan in calls.Where(call => !committed.Contains(call.Id)))
                Commit(orphan, ToolResult.Fail(AbortedToolNotice));
            throw;
        }
    }

    /// <summary>
    /// Bounded rolling pool over consecutive read-only calls; completions may land out of
    /// order but results commit contiguously in model order (dsh executeToolCalls invariant).
    /// </summary>
    private async Task ExecuteParallelPoolAsync(
        IReadOnlyList<AssistantToolCall> calls,
        int start,
        int end,
        int parallelCap,
        string? pageId,
        string? sessionId,
        Action<AssistantToolCall, ToolResult> commit,
        CancellationToken ct)
    {
        var slots = new ToolResult?[end - start];
        var commitIndex = 0;
        using var gate = new SemaphoreSlim(Math.Min(parallelCap, end - start));
        var pending = new List<Task<(int Slot, ToolResult Result)>>();
        for (var i = start; i < end; i++)
        {
            var localIndex = i;
            pending.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await InvokeToolAsync(calls[localIndex], pageId, sessionId, ct).ConfigureAwait(false);
                    return (localIndex - start, result);
                }
                finally { gate.Release(); }
            }, CancellationToken.None));
        }

        try
        {
            while (pending.Count > 0)
            {
                var settled = await Task.WhenAny(pending).ConfigureAwait(true);
                pending.Remove(settled);
                var (slot, result) = await settled.ConfigureAwait(true);
                slots[slot] = result;
                while (commitIndex < slots.Length && slots[commitIndex] is { } ready)
                {
                    commit(calls[start + commitIndex], ready);
                    commitIndex++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Harvest whatever finished, commit the ready prefix, then let the batch-level
            // catch fill the remaining gaps with synthetic aborted results.
            foreach (var task in pending)
            {
                if (task.Status != TaskStatus.RanToCompletion) continue;
                var (slot, result) = task.Result;
                slots[slot] ??= result;
            }
            while (commitIndex < slots.Length && slots[commitIndex] is { } ready)
            {
                commit(calls[start + commitIndex], ready);
                commitIndex++;
            }
            // Drain so no sibling completion is observed unhandled.
            try { await Task.WhenAll(pending).ConfigureAwait(true); }
            catch { /* drain-only */ }
            throw;
        }
    }

    private async Task<ToolResult> InvokeToolAsync(AssistantToolCall call, string? pageId, string? sessionId, CancellationToken ct)
    {
        // Operator-promoted steering preempts remaining dispatches of this step.
        if (IsNextInstructionPriority())
            return ToolResult.Ok(PreemptedToolNotice);

        try
        {
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            var result = await _dispatcher.InvokeAsync(
                new ToolInvocation(call.Name, arguments.RootElement.Clone(), _turnPageId ?? pageId, sessionId, call.Id), ct)
                .ConfigureAwait(true);
            if (result.Success && !string.IsNullOrWhiteSpace(result.AttachedPageId))
                _turnPageId = result.AttachedPageId;
            return result;
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail("工具参数不是有效 JSON: " + ex.Message);
        }
    }

    #region Event-log replay (resume)

    /// <summary>
    /// Rebuilds model-visible state from a persisted event stream: messages re-enter
    /// <see cref="History"/> and the active strategy; compression side-effects (manual
    /// context_compress calls and automatic compactions) are re-applied against the strategy's
    /// ACP store. Streaming deltas were never persisted, so replay fidelity is message-level.
    /// </summary>
    public void Replay(IReadOnlyList<AgentSessionEvent> events)
    {
        // Import first so the in-memory log mirrors the persisted stream one-to-one and
        // live appends continue the same contiguous sequence (no post-resume gap).
        Log.Import(events);

        var strategy = Strategy ?? new CompactorContextStrategy(new AgentContextCompactor());
        var projector = new AgentHistoryProjector.MessageProjector();
        var toolNames = events.Select(@event => @event.Data).OfType<ToolCallRequested>()
            .GroupBy(requested => requested.CallId)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        // Manual context_compress calls re-execute against the store during replay so the
        // rebuilt ACP window matches the original ref numbering exactly.
        var pendingCompressArguments = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var @event in events)
        {
            switch (@event.Data)
            {
                case TurnStarted started:
                    _turnCounter = Math.Max(_turnCounter, started.Turn);
                    break;

                case ToolCallRequested requested when strategy is AcpContextStrategy &&
                    requested.Name.Equals("context_compress", StringComparison.OrdinalIgnoreCase):
                    pendingCompressArguments[requested.CallId] = requested.ArgumentsJson;
                    break;

                case ContextCompacted compacted when strategy is AcpContextStrategy:
                    ReplayAutoCompaction(((AcpContextStrategy)strategy).Store, compacted);
                    continue;
            }

            if (@event.Data is ToolCallCompleted done &&
                strategy is AcpContextStrategy target &&
                done.Success &&
                pendingCompressArguments.TryGetValue(done.CallId, out var arguments))
            {
                // Side-effects land BEFORE the result message feeds the store, mirroring
                // original execution order (handler ran first).
                foreach (var range in ParseCompressRanges(arguments))
                    target.Store.Compress(range.StartRef, range.EndRef, range.Summary, range.Title);
            }

            foreach (var message in projector.Feed(@event.Data))
            {
                History.Add(message);
                if (message.ToolCalls is { Count: > 0 } calls)
                    strategy.OnAssistantToolCalls(message.Content, calls);
                else if (message.Role == "tool" && message.ToolCallId is { } callId)
                    strategy.OnToolResult(callId, message.Content ?? string.Empty,
                        toolNames.GetValueOrDefault(callId));
                else if (message.Role == "user") strategy.OnUser(message.Content ?? string.Empty);
                else if (message.Role == "assistant") strategy.OnAssistant(message.Content);
            }
        }
    }

    private static void ReplayAutoCompaction(AcpContextStore store, ContextCompacted compacted)
    {
        if (compacted.Summary is null) return;
        var refs = ParseRangeRefs(compacted.Range);
        if (refs is not { } pair) return;
        store.Compress(pair.StartRef, pair.EndRef, compacted.Summary, "(自动压缩)");
    }

    internal static (string StartRef, string EndRef)? ParseRangeRefs(string range)
    {
        var numbers = new List<string>();
        var digits = new StringBuilder();
        char previous = default;
        foreach (var character in range)
        {
            if (char.IsDigit(character)) digits.Append(character);
            else
            {
                if (digits.Length > 0 && previous == 'm') numbers.Add(digits.ToString());
                digits.Clear();
            }
            previous = character;
        }
        if (digits.Length > 0 && previous == 'm') numbers.Add(digits.ToString());
        if (numbers.Count < 2) return null;
        return ($"m{int.Parse(numbers[0]):D5}", $"m{int.Parse(numbers[^1]):D5}");
    }

    private static (string StartRef, string EndRef, string Summary, string? Title)[] ParseCompressRanges(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("ranges", out var ranges) ||
                ranges.ValueKind != JsonValueKind.Array)
                return [];
            var parsed = new List<(string, string, string, string?)>();
            foreach (var range in ranges.EnumerateArray())
            {
                var startRef = range.TryGetProperty("start", out var startElement) ? startElement.GetString() : null;
                var endRef = range.TryGetProperty("end", out var endElement) ? endElement.GetString() : null;
                var summary = range.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
                var title = range.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                if (startRef is { Length: > 0 } && endRef is { Length: > 0 } && summary is { Length: > 0 })
                    parsed.Add((startRef, endRef, summary, title));
            }
            return parsed.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    #endregion

    private sealed class StreamingToolCall
    {
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();

        public AssistantToolCall Build(int index) => new(
            Id ?? $"call_{index}_{Guid.NewGuid():N}", Name.ToString(), Arguments.ToString());
    }
}
