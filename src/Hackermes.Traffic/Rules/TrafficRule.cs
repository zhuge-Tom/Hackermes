using Hackermes.Traffic.Models;
using System;

namespace Hackermes.Traffic.Rules;

public sealed record TrafficRule(
    string Id,
    string UrlPattern = "*",
    string? Method = null,
    TrafficStage? Stage = null,
    TrafficRequestEdit? RequestEdit = null,
    TrafficResponseEdit? ResponseEdit = null,
    bool Fail = false,
    string FailureReason = "BlockedByClient",
    bool Pause = false,
    bool Enabled = true);
