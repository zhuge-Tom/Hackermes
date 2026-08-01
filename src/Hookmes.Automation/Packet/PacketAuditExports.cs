using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hookmes.Automation.Packet;

public interface IPacketAuditSigningKey
{
    string Algorithm { get; }
    string KeyId { get; }
    string PublicKey { get; }
    byte[] Sign(byte[] canonicalPayload);
}

public sealed record PacketAuditExportPayload(
    int Version,
    DateTimeOffset ExportedAt,
    string KeyId,
    IReadOnlyList<PacketAuditEntry> Entries);

public sealed record PacketAuditExportDocument(
    int Version,
    string Algorithm,
    string PublicKey,
    string Signature,
    PacketAuditExportPayload Payload);

public sealed record PacketAuditVerification(
    bool Valid,
    string? KeyId,
    int EntryCount,
    DateTimeOffset? ExportedAt,
    string? ErrorCode = null);

public interface IPacketAuditExportService
{
    string Export(PacketAuditQuery query);
    PacketAuditVerification Verify(string content, string? expectedKeyId = null);
}

/// <summary>Creates bounded, metadata-only audit documents verifiable without the private signing key.</summary>
public sealed class PacketAuditExportService(IPacketAuditTrail audit, IPacketAuditSigningKey signingKey)
    : IPacketAuditExportService
{
    public const int SchemaVersion = 1;
    public const int MaximumEntries = 500;
    public const int MaximumContentBytes = 2 * 1024 * 1024;
    public const string EcdsaP256Sha256 = "ECDSA_P256_SHA256";
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DisplayJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Export(PacketAuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!signingKey.Algorithm.Equals(EcdsaP256Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported audit signing algorithm.");
        var bounded = query with { Limit = Math.Clamp(query.Limit, 1, MaximumEntries) };
        var entries = audit.Query(bounded).Reverse().ToArray();
        foreach (var entry in entries) PacketAuditTrail.ValidateEntry(entry);
        var payload = new PacketAuditExportPayload(SchemaVersion, DateTimeOffset.UtcNow, signingKey.KeyId, entries);
        var signature = signingKey.Sign(Canonicalize(payload));
        var document = new PacketAuditExportDocument(SchemaVersion, signingKey.Algorithm,
            signingKey.PublicKey, Convert.ToBase64String(signature), payload);
        var content = JsonSerializer.Serialize(document, DisplayJson);
        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes)
            throw new InvalidOperationException("Signed audit export exceeds the content limit.");
        return content;
    }

    public PacketAuditVerification Verify(string content, string? expectedKeyId = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return Invalid("empty_content");
        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes) return Invalid("content_too_large");
        try
        {
            var document = JsonSerializer.Deserialize<PacketAuditExportDocument>(content, CanonicalJson);
            if (document?.Payload is null || document.Version != SchemaVersion || document.Payload.Version != SchemaVersion)
                return Invalid("unsupported_version");
            if (string.IsNullOrWhiteSpace(document.Algorithm) ||
                !document.Algorithm.Equals(EcdsaP256Sha256, StringComparison.Ordinal))
                return Invalid("unsupported_algorithm");
            if (string.IsNullOrWhiteSpace(document.PublicKey) || string.IsNullOrWhiteSpace(document.Signature) ||
                string.IsNullOrWhiteSpace(document.Payload.KeyId) || document.Payload.Entries is null)
                return Invalid("invalid_document");
            if (document.Payload.Entries.Count > MaximumEntries) return Invalid("too_many_entries");
            foreach (var entry in document.Payload.Entries) PacketAuditTrail.ValidateEntry(entry);

            var publicKey = Convert.FromBase64String(document.PublicKey);
            var keyId = Fingerprint(publicKey);
            if (!keyId.Equals(document.Payload.KeyId, StringComparison.OrdinalIgnoreCase))
                return Invalid("key_id_mismatch", keyId, document);
            if (!string.IsNullOrWhiteSpace(expectedKeyId) &&
                !keyId.Equals(expectedKeyId.Trim(), StringComparison.OrdinalIgnoreCase))
                return Invalid("untrusted_key", keyId, document);

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length) return Invalid("invalid_public_key", keyId, document);
            var signature = Convert.FromBase64String(document.Signature);
            var valid = key.VerifyData(Canonicalize(document.Payload), signature, HashAlgorithmName.SHA256);
            return valid
                ? new PacketAuditVerification(true, keyId, document.Payload.Entries.Count, document.Payload.ExportedAt)
                : Invalid("invalid_signature", keyId, document);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            return Invalid("invalid_document");
        }
    }

    public static string Fingerprint(byte[] subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();

    private static byte[] Canonicalize(PacketAuditExportPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);

    private static PacketAuditVerification Invalid(
        string code, string? keyId = null, PacketAuditExportDocument? document = null) =>
        new(false, keyId, document?.Payload?.Entries?.Count ?? 0, document?.Payload?.ExportedAt, code);
}
