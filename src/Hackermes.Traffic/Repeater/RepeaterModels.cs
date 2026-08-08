using Hackermes.Traffic.Models;
using System;
using System.Collections.Generic;

namespace Hackermes.Traffic.Repeater;

public enum RepeaterSendStatus
{
    Sending,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record RepeaterSendOptions
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    public RepeaterSendOptions(TimeSpan timeout) => Timeout = timeout;

    public TimeSpan Timeout { get; }

    public static RepeaterSendOptions Default { get; } = new(DefaultTimeout);
}

public sealed record RepeaterRequest(
    string Method,
    string Url,
    IReadOnlyList<TrafficHeader> Headers,
    byte[]? Body);

public sealed record RepeaterSendResult(
    string Id,
    int Sequence,
    RepeaterSendStatus Status,
    RepeaterRequest Request,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long DurationMilliseconds,
    int RequestSize,
    int? ResponseStatus,
    string? ResponseStatusText,
    IReadOnlyList<TrafficHeader> ResponseHeaders,
    byte[]? ResponseBody,
    int ResponseSize,
    string? Error);

public sealed record RepeaterDraft(
    string Id,
    string Name,
    string SourcePacketId,
    string PageId,
    RepeaterRequest Request,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision,
    IReadOnlyList<RepeaterSendResult> History);

public sealed record RepeaterDraftUpdate(
    string? Name = null,
    string? Method = null,
    string? Url = null,
    IReadOnlyList<TrafficHeader>? Headers = null,
    byte[]? Body = null,
    bool ReplaceBody = false);

public sealed record RepeaterChangedEvent(
    string Operation,
    string DraftId,
    RepeaterDraft? Draft);
