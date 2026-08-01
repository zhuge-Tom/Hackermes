using Hookmes.Traffic.Models;
using System;

namespace Hookmes.Traffic.Rules;

public enum TrafficRuleAction { None, Pause, Fail, EditRequest, FulfillResponse }
public enum TrafficRuleExecutionResult { Matched, Succeeded, Failed, Skipped }

/// <summary>Secret-free packet metadata. URL query, header values and body content are intentionally excluded.</summary>
public sealed record TrafficRulePacketMetadata(
    string Method,
    string? Scheme,
    string? Host,
    string PathHash,
    int? Status,
    int HeaderCount,
    long BodyLength,
    TrafficState State);

public sealed record TrafficRuleExecutionEvent(
    string RuleId,
    string PacketId,
    string PageId,
    TrafficStage Stage,
    TrafficRuleAction Action,
    TrafficRuleExecutionResult Result,
    TrafficRulePacketMetadata Before,
    TrafficRulePacketMetadata After,
    DateTimeOffset Timestamp,
    string? ErrorCode = null);

public interface ITrafficRuleExecutionSource
{
    event Action<TrafficRuleExecutionEvent>? RuleExecuted;
}
