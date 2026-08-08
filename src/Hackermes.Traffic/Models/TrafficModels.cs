using System;
using System.Collections.Generic;

namespace Hackermes.Traffic.Models;

public enum TrafficStage { Request, Response }
public enum TrafficState { Paused, Continued, Failed, Fulfilled }

public sealed record TrafficHeader(string Name, string Value);

public sealed record TrafficMessage(
    string Id,
    string PageId,
    TrafficStage Stage,
    TrafficState State,
    string Method,
    string Url,
    IReadOnlyList<TrafficHeader> RequestHeaders,
    byte[]? RequestBody,
    int? ResponseStatus,
    string? ResponseStatusText,
    IReadOnlyList<TrafficHeader> ResponseHeaders,
    byte[]? ResponseBody,
    string ResourceType,
    DateTimeOffset CapturedAt,
    string? AppliedRuleId = null,
    string? Error = null);

public sealed record TrafficRequestEdit(
    string? Url = null,
    string? Method = null,
    IReadOnlyList<TrafficHeader>? Headers = null,
    byte[]? Body = null);

public sealed record TrafficResponseEdit(
    int Status = 200,
    string? StatusText = null,
    IReadOnlyList<TrafficHeader>? Headers = null,
    byte[]? Body = null);

public sealed record TrafficReplayResult(
    int Status,
    string? StatusText,
    IReadOnlyList<TrafficHeader> Headers,
    byte[] Body);

public sealed record TrafficCaptureOptions(
    bool PauseRequests = false,
    bool PauseResponses = false,
    bool CaptureResponseBodies = true,
    int MaxResponseBodyBytes = 2 * 1024 * 1024)
{
    /// <summary>
    /// A lower bound keeps the capture feature useful, while the upper bound
    /// prevents a malformed integration call from retaining an unbounded body
    /// in the in-memory traffic history.
    /// </summary>
    public const int MinResponseBodyBytes = 64 * 1024;
    public const int DefaultMaxResponseBodyBytes = 2 * 1024 * 1024;
    public const int MaxAllowedResponseBodyBytes = 64 * 1024 * 1024;

    public TrafficCaptureOptions Normalize() => this with
    {
        MaxResponseBodyBytes = Math.Clamp(
            MaxResponseBodyBytes,
            MinResponseBodyBytes,
            MaxAllowedResponseBodyBytes)
    };
}

public sealed record TrafficQuery(
    string? PageId = null,
    string? Text = null,
    string? Method = null,
    int? Status = null,
    string? ResourceType = null,
    TrafficState? State = null,
    string? RuleId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Offset = 0,
    int Limit = 200);

public sealed record TrafficQueryResult(
    IReadOnlyList<TrafficMessage> Items,
    int Total,
    int Offset,
    int Limit);
