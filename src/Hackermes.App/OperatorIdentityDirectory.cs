using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hackermes.App;

public sealed record OperatorIdentity(string Id, string Name, DateTimeOffset CreatedAtUtc);

/// <summary>
/// Local multi-profile operator identities for the audit chain. The active profile name is
/// what the traffic audit stamps as its Operator field; with no profiles the chain falls
/// back to <c>traffic.operatorName</c> and then <c>Environment.UserName</c> (composed by DI).
/// Names follow the audit-trail identity rules: trimmed, at most 64 characters, no control
/// characters — an identity that the audit chain would reject can never be recorded here.
/// </summary>
internal sealed class OperatorIdentityDirectory
{
    private const int SchemaVersion = 1;
    internal const int MaximumNameLength = 64;
    private readonly string _path;
    private readonly object _gate = new();
    private IdentityDocument? _document;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OperatorIdentityDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Identity file path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _document = Load(_path);
    }

    /// <summary>Active profile name, or null when no local profiles exist.</summary>
    public string? ResolveActiveName()
    {
        lock (_gate)
            return _document?.Identities.FirstOrDefault(identity => identity.Id == _document!.ActiveId)?.Name;
    }

    public IReadOnlyList<OperatorIdentity> Identities
    {
        get { lock (_gate) return _document?.Identities.ToArray() ?? []; }
    }

    public string? ActiveId { get { lock (_gate) return _document?.ActiveId; } }

    /// <summary>Creates a profile and activates it; adopting an existing name (case-insensitive) just activates it.</summary>
    public OperatorIdentity Adopt(string name)
    {
        var normalized = NormalizeName(name);
        lock (_gate)
        {
            var document = _document ?? new IdentityDocument(SchemaVersion, [], null);
            var existing = document.Identities.FirstOrDefault(identity =>
                identity.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            OperatorIdentity identity;
            if (existing is not null)
            {
                identity = existing;
            }
            else
            {
                identity = new OperatorIdentity(Guid.NewGuid().ToString("N"), normalized, DateTimeOffset.UtcNow);
                document = document with { Identities = [.. document.Identities, identity] };
            }
            Save(document with { ActiveId = identity.Id });
            _document = document with { ActiveId = identity.Id };
            return identity;
        }
    }

    /// <summary>Activates a profile by id or case-insensitive name; returns false when unknown.</summary>
    public bool Use(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return false;
        var needle = NormalizeName(idOrName);
        lock (_gate)
        {
            if (_document is null) return false;
            var match = _document.Identities.FirstOrDefault(identity =>
                    identity.Id.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
                    identity.Name.Equals(needle, StringComparison.OrdinalIgnoreCase));
            if (match is null) return false;
            Save(_document with { ActiveId = match.Id });
            _document = _document with { ActiveId = match.Id };
            return true;
        }
    }

    private static string NormalizeName(string candidate)
    {
        var name = candidate.Trim();
        if (name.Length == 0)
            throw new ArgumentException("Operator name is required.", nameof(candidate));
        if (name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"Operator name must be at most {MaximumNameLength} characters without control characters.", nameof(candidate));
        return name;
    }

    private static IdentityDocument? Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var document = JsonSerializer.Deserialize<IdentityDocument>(File.ReadAllText(candidate), JsonOptions);
                if (document is { SchemaVersion: SchemaVersion, Identities: not null } &&
                    document.Identities.All(identity => identity is not null &&
                        !string.IsNullOrWhiteSpace(identity.Id) && !string.IsNullOrWhiteSpace(identity.Name)))
                {
                    // An activeId pointing nowhere degrades gracefully to fallback resolution.
                    if (document.ActiveId is not null && document.Identities.All(identity => identity.Id != document.ActiveId))
                        document = document with { ActiveId = null };
                    return document;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Fall through to the backup copy.
            }
        }
        return null;
    }

    private void Save(IdentityDocument document)
    {
        var temporaryPath = _path + ".tmp";
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(temporaryPath,
            JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(_path))
        {
            try { File.Copy(_path, _path + ".bak", overwrite: true); }
            catch (IOException) { /* A failed backup copy must not block the primary write. */ }
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed record IdentityDocument(int SchemaVersion, IReadOnlyList<OperatorIdentity> Identities, string? ActiveId);
}
