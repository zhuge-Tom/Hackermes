using System;
using System.Collections.Generic;

namespace Hookmes.Traffic.Comparison;

public enum ComparisonSourceKind
{
    TrafficRequest,
    TrafficResponse,
    RepeaterRequest,
    RepeaterResponse
}

public sealed record ComparisonSource(
    ComparisonSourceKind Kind,
    string? PacketId = null,
    string? DraftId = null,
    string? SendResultId = null);

public enum DifferenceKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public sealed record StartLineFieldDifference(
    string Field,
    string? Left,
    string? Right,
    DifferenceKind Kind);

public sealed record HeaderDifference(
    string Name,
    IReadOnlyList<string> LeftValues,
    IReadOnlyList<string> RightValues,
    DifferenceKind Kind);

public enum BodyContentKind
{
    Empty,
    Text,
    Binary
}

public sealed record BodySummary(
    BodyContentKind Kind,
    int Length,
    string Sha256,
    string? ContentType,
    string? Text);

public sealed record BodyDifference(
    bool Equal,
    BodySummary Left,
    BodySummary Right,
    int? FirstDifferentByteOffset);

public sealed record TrafficComparisonResult(
    IReadOnlyList<StartLineFieldDifference> StartLine,
    IReadOnlyList<HeaderDifference> Headers,
    BodyDifference Body,
    bool Equal);

public sealed record TrafficComparisonSession(
    string Id,
    string Name,
    ComparisonSource Left,
    ComparisonSource Right,
    TrafficComparisonResult Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision);

public sealed record TrafficComparisonChanged(
    string Operation,
    string SessionId,
    TrafficComparisonSession? Session);
