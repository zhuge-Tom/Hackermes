using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hookmes.Automation.Packet;

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
        EnsureSize(content);
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
        "hookmesjson" or "json" => PacketArchiveFormat.HookmesJson,
        "har" => PacketArchiveFormat.Har,
        _ => throw new ArgumentException("Archive format must be hookmesJson or har.", nameof(value))
    };

    private static void EnsureSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaximumUtf8Bytes)
            throw new InvalidDataException($"Agent archive content exceeds {MaximumUtf8Bytes} UTF-8 bytes.");
    }
}
