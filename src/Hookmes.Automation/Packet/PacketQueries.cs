using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Packet;

public sealed record PacketQuery(
    string? Text = null,
    string? Method = null,
    int? StatusCode = null,
    string? ResourceType = null,
    bool OnlyIntercepted = false,
    int Offset = 0,
    int Limit = 100);

public sealed record PacketQueryPage(
    IReadOnlyList<PacketSummary> Items,
    int Total,
    int Offset,
    int Limit);

/// <summary>Optional bounded compound query shared by human, CLI and Agent entry points.</summary>
public interface IPacketQueryService
{
    Task<PacketQueryPage> QueryPacketsAsync(PacketQuery query, CancellationToken cancellationToken);
}

public static class PacketQueryLimits
{
    public const int MaximumPageSize = 500;
    public const int MaximumOffset = 1_000_000;

    public static PacketQuery Validate(PacketQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset is < 0 or > MaximumOffset)
            throw new ArgumentException($"Offset must be between 0 and {MaximumOffset}.");
        if (query.Limit is < 1 or > MaximumPageSize)
            throw new ArgumentException($"Limit must be between 1 and {MaximumPageSize}.");
        if (query.StatusCode is < 100 or > 999)
            throw new ArgumentException("Status code must be between 100 and 999.");
        ValidateText(query.Text, nameof(query.Text), 512);
        ValidateText(query.Method, nameof(query.Method), 32);
        ValidateText(query.ResourceType, nameof(query.ResourceType), 64);
        return query with
        {
            Text = Normalize(query.Text),
            Method = Normalize(query.Method),
            ResourceType = Normalize(query.ResourceType)
        };
    }

    private static void ValidateText(string? value, string name, int maximum)
    {
        if (value?.Length > maximum) throw new ArgumentException($"{name} must not exceed {maximum} characters.");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
