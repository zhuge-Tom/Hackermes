using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using System;
using System.Collections.Generic;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Append-only session event vocabulary (deepseek-harness lineage): every observable fact
/// of an agent turn lands here exactly once. The transcript UI subscribes to
/// <see cref="AgentSessionLog.Appended"/> instead of the runner mutating view models, and
/// future replay/fork/telemetry features derive from this stream rather than from ad-hoc state.
/// </summary>
public enum AgentEventKind
{
    TurnStart,
    TurnEnd,
    StepStart,
    StepEnd,
    UserMessage,
    AssistantChunk,
    ReasoningChunk,
    AssistantMessage,
    Usage,
    ToolCall,
    ToolResult,
    RequestRetry,
    ContextCompacted,
    ApprovalAudited,
    RequestHeader,
}

/// <summary>
/// One request-shape epoch (dsh request/header lineage): the canonical fingerprint of
/// model + system prompt + tool list changed (or was first seen). Frequent `change`
/// epochs mean the provider's prompt cache keeps getting invalidated — visible drift
/// instead of silent per-request variation.
/// </summary>
public sealed record RequestHeaderLogged(string Fingerprint, string Reason, string Model, int ToolCount) : AgentEventData;

public enum AgentTurnEndReason
{
    Completed,
    Aborted,
    Blocked,
    Error,

    /// <summary>Sticky: a step hit the provider's per-reply length cap and its output was cut.</summary>
    LengthCapped,
    MaxRounds,
}

public abstract record AgentEventData;

public sealed record TurnStarted(int Turn) : AgentEventData;

public sealed record TurnEnded(AgentTurnEndReason Reason, string? Detail = null) : AgentEventData;

public sealed record StepStarted(int Turn, int Step) : AgentEventData;

public sealed record StepFinished(int Turn, int Step) : AgentEventData;

/// <param name="Steered">True when the message was claimed from the mid-turn instruction queue.</param>
/// <param name="Priority">True for operator-promoted steering instructions.</param>
/// <param name="Injected">True for tool-provided additional context (not operator input).</param>
public sealed record UserMessageReceived(string Text, bool Steered = false, bool Priority = false, bool Injected = false) : AgentEventData;

public sealed record AssistantDelta(string Text) : AgentEventData;

/// <summary>Reasoning-model thinking delta (provider `reasoning_content`); never part of model history.</summary>
public sealed record ReasoningDelta(string Text) : AgentEventData;

/// <summary>Final assistant message of one step; <paramref name="IsFinalReport"/> marks a turn-closing report.</summary>
public sealed record AssistantReply(string Content, bool HasToolCalls, bool IsFinalReport) : AgentEventData;

public sealed record UsageRecorded(StreamUsage Usage) : AgentEventData;

/// <summary>Logged before dispatch, mirroring dsh's durable tool/call-before-prepare ordering.</summary>
public sealed record ToolCallRequested(string CallId, string Name, string ArgumentsJson) : AgentEventData;

public sealed record ToolCallCompleted(string CallId, string Name, bool Success, string Content) : AgentEventData;

public sealed record RequestRetried(int Attempt, int MaxAttempts, string Error, TimeSpan Delay) : AgentEventData;

/// <param name="Range">Human-readable compressed range, e.g. "[m00003]–[m00018]".</param>
/// <param name="Summary">The landed summary text; required so event-log replay can rebuild the block.</param>
public sealed record ContextCompacted(
    long ReclaimedChars,
    string Range,
    bool Automatic,
    string? Warning = null,
    string? Summary = null) : AgentEventData;

/// <summary>Wraps a dispatcher approval-audit fact for the durable log.</summary>
public sealed record ApprovalAudited(AiToolAuditRecord Record) : AgentEventData;

/// <summary>
/// One immutable log entry. <see cref="Seq"/> is contiguous and equals the index in the log,
/// like dsh's session events; <see cref="Turn"/>/<see cref="Step"/> locate the entry inside
/// the turn/step structure (0 when outside any step).
/// </summary>
public sealed record AgentSessionEvent(
    int Seq,
    DateTimeOffset Time,
    AgentEventKind Kind,
    int Turn,
    int Step,
    AgentEventData Data);

/// <summary>Append-only event log with synchronous fan-out to live observers.</summary>
public sealed class AgentSessionLog
{
    private readonly List<AgentSessionEvent> _events = [];
    private readonly object _gate = new();

    /// <summary>Raised synchronously after commit; observer exceptions are contained so one
    /// bad listener cannot corrupt the loop (dsh's emit-dispatch containment rule).</summary>
    public event Action<AgentSessionEvent>? Appended;

    public int Count { get { lock (_gate) return _events.Count; } }

    public IReadOnlyList<AgentSessionEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public AgentSessionEvent Append(AgentEventKind kind, int turn, int step, AgentEventData data)
    {
        AgentSessionEvent @event;
        lock (_gate)
        {
            @event = new AgentSessionEvent(_events.Count, DateTimeOffset.UtcNow, kind, turn, step, data);
            _events.Add(@event);
        }
        var handlers = Appended;
        if (handlers is null) return @event;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<AgentSessionEvent>)handler)(@event); }
            catch { /* listener failures never propagate into the loop */ }
        }
        return @event;
    }

    /// <summary>
    /// Seeds this in-memory log from a previously persisted stream (resume path) WITHOUT
    /// raising <see cref="Appended"/> — the transcript was already projected by the caller.
    /// After import, live appends continue the contiguous sequence, so anything deriving
    /// from the log sees one unbroken stream instead of a post-restart gap.
    /// </summary>
    public void Import(IReadOnlyList<AgentSessionEvent> events)
    {
        lock (_gate)
        {
            foreach (var @event in events)
                _events.Add(new AgentSessionEvent(_events.Count, @event.Time, @event.Kind, @event.Turn, @event.Step, @event.Data));
        }
    }
}
