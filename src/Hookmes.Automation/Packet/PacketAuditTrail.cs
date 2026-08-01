using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hookmes.Automation.Packet;

public enum PacketAuditOperation { Edit, BodyEdit, Discard, Continue, Drop, Fulfill, Replay, RuleMatch }
public enum PacketAuditResult { Succeeded, Failed }

public sealed record PacketAuditEntry(
    string AuditId,
    DateTimeOffset Timestamp,
    string EntryPoint,
    PacketAuditOperation Operation,
    string PacketId,
    string Side,
    PacketEditVersion Before,
    PacketEditVersion After,
    PacketAuditResult Result,
    string? ErrorCode = null,
    string? RuleId = null,
    string? RuleAction = null);

public sealed record PacketAuditQuery(string? PacketId = null, PacketAuditOperation? Operation = null, int Limit = 100);

public interface IPacketAuditTrail
{
    void Record(PacketAuditEntry entry);
    IReadOnlyList<PacketAuditEntry> Query(PacketAuditQuery query);
}

public interface IPacketAuditQueryService
{
    IReadOnlyList<PacketAuditEntry> QueryAudit(PacketAuditQuery query);
}

/// <summary>Bounded metadata-only audit trail with versioned, atomic persistence.</summary>
public sealed class PacketAuditTrail : IPacketAuditTrail
{
    public const int SchemaVersion = 1;
    public const int MaximumEntries = 2000;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<PacketAuditEntry> _entries;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PacketAuditTrail(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Audit path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _entries = Load(_path).TakeLast(MaximumEntries).ToList();
    }

    public void Record(PacketAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry);
        lock (_gate)
        {
            _entries.Add(entry with { ErrorCode = SanitizeCode(entry.ErrorCode) });
            if (_entries.Count > MaximumEntries) _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            TrySave();
        }
    }

    public IReadOnlyList<PacketAuditEntry> Query(PacketAuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 500);
        lock (_gate) return _entries.AsEnumerable().Reverse()
            .Where(entry => query.PacketId is null || entry.PacketId.Equals(query.PacketId, StringComparison.Ordinal))
            .Where(entry => query.Operation is null || entry.Operation == query.Operation)
            .Take(limit).ToArray();
    }

    private void TrySave()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var temporary = _path + ".tmp";
        var backup = _path + ".bak";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AuditFile(SchemaVersion, _entries), JsonOptions));
            if (File.Exists(_path) && TryLoadFile(_path, out _)) File.Replace(temporary, _path, backup, true);
            else File.Move(temporary, _path, true); // preserve the last known-good backup when primary is corrupt
        }
        catch (IOException) { TryDeleteTemporary(temporary); }
        catch (UnauthorizedAccessException) { TryDeleteTemporary(temporary); }
    }

    private static IReadOnlyList<PacketAuditEntry> Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (TryLoadFile(candidate, out var entries)) return entries;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return [];
    }

    private static bool TryLoadFile(string path, out IReadOnlyList<PacketAuditEntry> entries)
    {
        entries = [];
        if (!File.Exists(path)) return false;
        try
        {
            var file = JsonSerializer.Deserialize<AuditFile>(File.ReadAllText(path), JsonOptions);
            if (file?.Version != SchemaVersion || file.Entries is null || file.Entries.Count > MaximumEntries) return false;
            foreach (var entry in file.Entries)
            {
                if (entry is null) return false;
                Validate(entry);
            }
            entries = file.Entries;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static void Validate(PacketAuditEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.AuditId) || string.IsNullOrWhiteSpace(entry.PacketId))
            throw new ArgumentException("Audit id and packet id are required.");
        if (string.IsNullOrWhiteSpace(entry.EntryPoint) || entry.EntryPoint.Length > 64)
            throw new ArgumentException("Audit entry point is invalid.");
        if (entry.Side is not ("request" or "response")) throw new ArgumentException("Audit side must be request or response.");
        if (!Enum.IsDefined(entry.Operation) || !Enum.IsDefined(entry.Result) || entry.Timestamp == default)
            throw new ArgumentException("Audit operation metadata is invalid.");
        if (entry.Operation == PacketAuditOperation.RuleMatch &&
            (string.IsNullOrWhiteSpace(entry.RuleId) || entry.RuleId.Length > 128))
            throw new ArgumentException("Audit rule id is invalid.");
        if ((entry.RuleAction?.Length ?? 0) > 64)
            throw new ArgumentException("Audit rule metadata is invalid.");
        ValidateVersion(entry.Before); ValidateVersion(entry.After);
    }

    private static void ValidateVersion(PacketEditVersion version)
    {
        if (version.Length < 0 || version.Sha256.Length != 64 || version.Sha256.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw new ArgumentException("Audit body version is invalid.");
    }

    private static string? SanitizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var safe = new string(code.TakeWhile(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-').Take(128).ToArray());
        return safe.Length == 0 ? "Error" : safe;
    }

    private static void TryDeleteTemporary(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private sealed record AuditFile(int Version, IReadOnlyList<PacketAuditEntry>? Entries);
}
