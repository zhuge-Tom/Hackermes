using System;
using System.Collections.Generic;

namespace Hookmes.Traffic.Models;

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
    bool CaptureResponseBodies = true);
