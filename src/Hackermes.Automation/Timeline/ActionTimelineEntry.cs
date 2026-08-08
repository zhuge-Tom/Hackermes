using Hackermes.Automation.Model;
using System;

namespace Hackermes.Automation.Timeline;

/// <summary>A single immutable action observation in chronological order.</summary>
public sealed record ActionTimelineEntry
{
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? PageId { get; init; }
    public required ActionDescriptor Action { get; init; }
    public required ActionResult Result { get; init; }
    public bool Observed { get; init; }
}
