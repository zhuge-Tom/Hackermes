using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hackermes.Assessment;

/// <summary>Signing identity for exported assessment reports; mirrors the audit signing key shape.</summary>
public interface IAssessmentReportSigningKey
{
    string Algorithm { get; }
    string KeyId { get; }
    string PublicKey { get; }
    byte[] Sign(byte[] canonicalPayload);
}

public sealed record AssessmentReportExportPayload(
    int Version,
    DateTimeOffset ExportedAt,
    string KeyId,
    AssessmentReport Report);

public sealed record AssessmentReportExportDocument(
    int Version,
    string Algorithm,
    string PublicKey,
    string Signature,
    AssessmentReportExportPayload Payload);

public sealed record AssessmentReportVerification(
    bool Valid,
    string? KeyId,
    string? JobId,
    DateTimeOffset? ExportedAt,
    string? ErrorCode = null);

public interface IAssessmentReportExportService
{
    string Export(string jobId);
    AssessmentReportVerification Verify(string content, string? expectedKeyId = null);
}

/// <summary>
/// Optional local trust anchors for signed reports; mirrors the audit trust policy shape.
/// When attached, a validly-signed document must additionally carry a listed (non-revoked) keyId.
/// </summary>
public interface IAssessmentReportTrustPolicy
{
    bool IsTrusted(string keyId);
}

/// <summary>
/// Creates bounded signed assessment report documents. The canonical payload is compact
/// camelCase JSON signed with ECDSA P-256/SHA-256 (P1363); the embedded SPKI public key makes
/// every document verifiable offline without the private signing key.
/// </summary>
public sealed class AssessmentReportExportService(IAssessmentControlPlane plane, IAssessmentReportSigningKey signingKey,
    IAssessmentReportTrustPolicy? trustPolicy = null) : IAssessmentReportExportService
{
    public const int SchemaVersion = 1;
    public const int MaximumListEntries = 500;
    public const int MaximumContentBytes = 2 * 1024 * 1024;
    public const int MaximumJobIdLength = 128;
    public const string EcdsaP256Sha256 = "ECDSA_P256_SHA256";
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DisplayJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Export(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Assessment job id is required.", nameof(jobId));
        if (!signingKey.Algorithm.Equals(EcdsaP256Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported report signing algorithm.");
        var snapshot = plane.ReadCase(jobId.Trim());
        var report = new AssessmentReport(snapshot.Job, snapshot.Scope, snapshot.Plan,
            snapshot.Evidence, snapshot.Findings, snapshot.Audit, snapshot.AuditVerification);
        var payload = new AssessmentReportExportPayload(SchemaVersion, DateTimeOffset.UtcNow, signingKey.KeyId, report);
        var signature = signingKey.Sign(Canonicalize(payload));
        var document = new AssessmentReportExportDocument(SchemaVersion, signingKey.Algorithm,
            signingKey.PublicKey, Convert.ToBase64String(signature), payload);
        var content = JsonSerializer.Serialize(document, DisplayJson);
        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes)
            throw new InvalidOperationException("Signed report export exceeds the content limit.");
        return content;
    }

    public AssessmentReportVerification Verify(string content, string? expectedKeyId = null)
    {
        var result = VerifyDocument(content, expectedKeyId);
        if (result.Valid && trustPolicy is { } policy && !policy.IsTrusted(result.KeyId!.Trim()))
            return result with { Valid = false, ErrorCode = "untrusted_key" };
        return result;
    }

    /// <summary>Offline verification entry point; needs no control plane and no private key.</summary>
    public static AssessmentReportVerification VerifyDocument(string content, string? expectedKeyId = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return Invalid("empty_content");
        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes) return Invalid("content_too_large");
        try
        {
            var document = JsonSerializer.Deserialize<AssessmentReportExportDocument>(content, CanonicalJson);
            if (document?.Payload is null || document.Version != SchemaVersion || document.Payload.Version != SchemaVersion)
                return Invalid("unsupported_version");
            if (string.IsNullOrWhiteSpace(document.Algorithm) ||
                !document.Algorithm.Equals(EcdsaP256Sha256, StringComparison.Ordinal))
                return Invalid("unsupported_algorithm");
            if (!IsValidPayload(document.Payload.Report))
                return Invalid("invalid_document");
            if (string.IsNullOrWhiteSpace(document.PublicKey) || string.IsNullOrWhiteSpace(document.Signature) ||
                string.IsNullOrWhiteSpace(document.Payload.KeyId))
                return Invalid("invalid_document");

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
                ? new AssessmentReportVerification(true, keyId, document.Payload.Report.Job.Id, document.Payload.ExportedAt)
                : Invalid("invalid_signature", keyId, document);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            return Invalid("invalid_document");
        }
    }

    public static string Fingerprint(byte[] subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();

    private static bool IsValidPayload(AssessmentReport? report)
    {
        if (report?.Job is null || string.IsNullOrWhiteSpace(report.Job.Id) ||
            report.Job.Id.Length > MaximumJobIdLength ||
            report.Evidence is null || report.Findings is null || report.Audit is null ||
            report.AuditVerification is null) return false;
        return report.Evidence.Count <= MaximumListEntries &&
               report.Findings.Count <= MaximumListEntries &&
               report.Audit.Count <= MaximumListEntries;
    }

    private static byte[] Canonicalize(AssessmentReportExportPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);

    private static AssessmentReportVerification Invalid(
        string code, string? keyId = null, AssessmentReportExportDocument? document = null) =>
        new(false, keyId, document?.Payload?.Report?.Job?.Id, document?.Payload?.ExportedAt, code);
}
