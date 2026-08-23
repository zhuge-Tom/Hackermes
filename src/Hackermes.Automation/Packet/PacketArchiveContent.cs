using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hackermes.Automation.Packet;

/// <summary>
/// Bounded, path-free archive exchange used by Agent integrations. File-oriented human/CLI flows
/// stay separate; callers can never smuggle an arbitrary local path through this contract.
/// </summary>
public static class PacketArchiveContent
{
    public const int MaximumEntries = 500;
    public const int MaximumUtf8Bytes = 2 * 1024 * 1024;

    public static string Serialize(IReadOnlyList<PacketArchiveEntry> entries, PacketArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaximumEntries)
            throw new InvalidDataException($"Agent archives cannot contain more than {MaximumEntries} entries; narrow the filter.");
        var content = PacketArchiveCodec.Serialize(entries, format);
        if (Encoding.UTF8.GetByteCount(content) > MaximumUtf8Bytes)
            throw new InvalidDataException(
                $"Agent archive content exceeds {MaximumUtf8Bytes} UTF-8 bytes; retry with a smaller limit so each batch fits.");
        return content;
    }

    public static IReadOnlyList<PacketArchiveEntry> Deserialize(string content, PacketArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureSize(content);
        var entries = PacketArchiveCodec.Deserialize(content, format);
        if (entries.Count > MaximumEntries)
            throw new InvalidDataException($"Agent archives cannot contain more than {MaximumEntries} entries.");
        return entries;
    }

    public static PacketArchiveFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "hackermesjson" or "json" => PacketArchiveFormat.HackermesJson,
        "har" => PacketArchiveFormat.Har,
        _ => throw new ArgumentException("Archive format must be hackermesJson or har.", nameof(value))
    };

    /// <summary>
    /// Slices one exchange batch out of the full matched entry list. Offset may sit at or
    /// past the end (yielding an empty page with the unchanged total) so callers can walk
    /// batches until they have seen <see cref="PacketArchivePage.Total"/> entries.
    /// </summary>
    public static PacketArchivePage Page(IReadOnlyList<PacketArchiveEntry> entries, PacketArchiveExchangeQuery query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0)
            throw new ArgumentException("Archive page offset must not be negative.", nameof(query));
        if (query.Limit is < 1 or > MaximumEntries)
            throw new ArgumentException($"Archive page limit must be between 1 and {MaximumEntries}.", nameof(query));

        var total = entries.Count;
        var count = Math.Min(query.Limit, Math.Max(0, total - query.Offset));
        var slice = new PacketArchiveEntry[count];
        for (var index = 0; index < count; index++)
            slice[index] = entries[query.Offset + index];
        return new PacketArchivePage(slice, total);
    }

    private static void EnsureSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaximumUtf8Bytes)
            throw new InvalidDataException($"Agent archive content exceeds {MaximumUtf8Bytes} UTF-8 bytes.");
    }
}
