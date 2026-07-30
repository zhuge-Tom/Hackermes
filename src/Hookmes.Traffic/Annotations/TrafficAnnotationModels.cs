using System;
using System.Collections.Generic;

namespace Hookmes.Traffic.Annotations;

public enum TrafficReviewStatus
{
    Unreviewed,
    InReview,
    Resolved,
    Ignored
}

public sealed record TrafficAnnotation(
    string PacketId,
    bool Starred,
    IReadOnlyList<string> Tags,
    string? Note,
    TrafficReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision);

public sealed record TrafficAnnotationUpdate(
    bool? Starred = null,
    IReadOnlyList<string>? Tags = null,
    string? Note = null,
    bool ReplaceNote = false,
    TrafficReviewStatus? Status = null);

public sealed record TrafficAnnotationQuery(
    string? Tag = null,
    TrafficReviewStatus? Status = null,
    bool? Starred = null,
    string? Text = null);

public sealed record TrafficAnnotationChanged(
    string Operation,
    string PacketId,
    TrafficAnnotation? Annotation);
