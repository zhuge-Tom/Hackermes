using Hackermes.Assessment;
using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hackermes.App;

public enum AuditKeyStatus { Trusted, Retired, Revoked }

public sealed record AuditKeyGeneration(
    string KeyId,
    string PublicKey,
    AuditKeyStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RotatedAtUtc = null,
    DateTimeOffset? RevokedAtUtc = null,
    string? Note = null);

/// <summary>
/// Local trust anchors for the shared ECDSA signing identity (traffic audit + assessment
/// report exports). The file records every key generation with its lifecycle state; once it
/// exists it acts as an allowlist, so verification rejects documents signed by unknown or
/// revoked keys even when their signatures are cryptographically valid. While the file has
/// never been written, verification stays in legacy mode (caller-side pinning only) so
/// third-party offline flows keep working unchanged.
/// </summary>
internal sealed class AuditKeyTrustFile
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly object _gate = new();
    private List<AuditKeyGeneration>? _generations;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AuditKeyTrustFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Trust file path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _generations = Load(_path);
    }

    /// <summary>True once the trust file exists; from then on verification runs in allowlist mode.</summary>
    public bool TrustFileExists { get { lock (_gate) return _generations is not null; } }

    public IReadOnlyList<AuditKeyGeneration> Generations
    {
        get { lock (_gate) return _generations?.ToArray() ?? []; }
    }

    public bool IsAllowed(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return false;
        var normalized = NormalizeKeyId(keyId);
        lock (_gate)
        {
            if (_generations is null) return true;
            return _generations.Any(entry => NormalizeKeyId(entry.KeyId).Equals(normalized, StringComparison.OrdinalIgnoreCase)
                && entry.Status != AuditKeyStatus.Revoked);
        }
    }

    /// <summary>
    /// Records one rotation atomically: retires <paramref name="retiredKeyId"/> and lists
    /// <paramref name="newKeyId"/> as trusted in a single file write. The trust file must
    /// already exist and contain the retiring generation (see <see cref="RecordInitialGeneration"/>);
    /// an already-revoked old key stays revoked.
    /// </summary>
    public void RotateGeneration(string retiredKeyId, string newKeyId, string newPublicKey, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retiredKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPublicKey);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_generations is null)
                throw new InvalidOperationException("The trust file does not exist; run 'signing-keys adopt' before rotating.");
            var previousSnapshot = _generations.ToList();
            var generations = _generations.ToList();
            var retired = false;
            for (var index = 0; index < generations.Count; index++)
            {
                if (!NormalizeKeyId(generations[index].KeyId).Equals(NormalizeKeyId(retiredKeyId), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (generations[index].Status != AuditKeyStatus.Revoked)
                    generations[index] = generations[index] with { Status = AuditKeyStatus.Retired, RotatedAtUtc = now };
                retired = true;
                break;
            }
            if (!retired)
                throw new InvalidOperationException($"Retiring key '{retiredKeyId}' is not present in the trust file.");
            generations.Add(new AuditKeyGeneration(newKeyId, newPublicKey, AuditKeyStatus.Trusted, now, Note: note));
            try
            {
                Save(generations);
                _generations = generations;
            }
            catch
            {
                // Keep the in-memory snapshot consistent when persistence fails.
                _generations = previousSnapshot;
                throw;
            }
        }
    }

    /// <summary>Records the initial signing generation. Creates the trust file, switching to allowlist mode.</summary>
    public void RecordInitialGeneration(string keyId, string publicKey, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        lock (_gate)
        {
            if (_generations is not null) return;
            var generations = new List<AuditKeyGeneration>
            {
                new(keyId, publicKey, AuditKeyStatus.Trusted, DateTimeOffset.UtcNow, Note: note)
            };
            Save(generations);
            _generations = generations;
        }
    }

    /// <summary>Marks a keyId revoked; unknown ids are recorded as revocation-only entries. Requires adoption.</summary>
    public bool Revoke(string keyId, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var normalized = NormalizeKeyId(keyId);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_generations is null)
                throw new InvalidOperationException("The trust file does not exist; run 'signing-keys adopt' before revoking.");
            var previousSnapshot = _generations.ToList();
            var generations = _generations.ToList();
            var found = false;
            for (var index = 0; index < generations.Count; index++)
            {
                if (!NormalizeKeyId(generations[index].KeyId).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (generations[index].Status != AuditKeyStatus.Revoked)
                    generations[index] = generations[index] with { Status = AuditKeyStatus.Revoked, RevokedAtUtc = now, Note = note ?? generations[index].Note };
                found = true;
                break;
            }
            if (!found)
                generations.Add(new AuditKeyGeneration(normalized, string.Empty, AuditKeyStatus.Revoked, now,
                    RevokedAtUtc: now, Note: note));
            try
            {
                Save(generations);
                _generations = generations;
                return found;
            }
            catch
            {
                _generations = previousSnapshot;
                throw;
            }
        }
    }

    /// <summary>Restores an exact generations snapshot; compensation for a half-finished rotation.</summary>
    public void Restore(IReadOnlyList<AuditKeyGeneration> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            // Legacy mode has nothing to compensate: rotations abort before any write there.
            if (_generations is null) return;
            var restored = snapshot.ToList();
            Save(restored);
            _generations = restored;
        }
    }

    private static string NormalizeKeyId(string keyId) => keyId.Trim();

    private static List<AuditKeyGeneration>? Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var document = JsonSerializer.Deserialize<TrustDocument>(File.ReadAllText(candidate), JsonOptions);
                if (document is { SchemaVersion: SchemaVersion, Generations: not null } &&
                    document.Generations.All(entry => entry is not null &&
                        !string.IsNullOrWhiteSpace(entry.KeyId) && Enum.IsDefined(entry.Status)))
                    return document.Generations.ToList();
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Try the backup; an unreadable primary falls through to it.
            }
        }
        return null;
    }

    private void Save(List<AuditKeyGeneration> generations)
    {
        var temporaryPath = _path + ".tmp";
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(temporaryPath,
            JsonSerializer.Serialize(new TrustDocument(SchemaVersion, generations), JsonOptions), new UTF8Encoding(false));
        if (File.Exists(_path))
        {
            try { File.Copy(_path, _path + ".bak", overwrite: true); }
            catch (IOException) { /* A failed backup copy must not block the primary write. */ }
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed record TrustDocument(int SchemaVersion, IReadOnlyList<AuditKeyGeneration> Generations);
}

/// <summary>Dual-adapter exposing the shared trust file to both signed-export domains.</summary>
internal sealed class PacketAuditTrustPolicy(AuditKeyTrustFile store) : IPacketAuditTrustPolicy, IAssessmentReportTrustPolicy
{
    public bool IsTrusted(string keyId) => store.IsAllowed(keyId);
}
