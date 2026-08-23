using Hackermes.Assessment;
using Hackermes.Automation.Packet;
using Hackermes.Platform.Services;
using System;
using System.Security.Cryptography;

namespace Hackermes.App;

/// <summary>Application-owned audit signing identity backed by the protected secret store.
/// The same ECDSA P-256 key signs assessment report exports. Rotation overwrites the stored
/// private key (old private material is destroyed) while the trust file keeps the retired
/// public keys verifiable for historical documents.</summary>
internal sealed class PacketAuditSigningKey : IPacketAuditSigningKey, IAssessmentReportSigningKey, IDisposable
{
    internal const string SecretName = "traffic.audit.ecdsa-p256.pkcs8.v1";
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private readonly ISecretStore _secrets;
    private readonly object _gate = new();
    private ECDsa _key = null!;
    private string _keyId = null!;
    private string _publicKey = null!;

    public PacketAuditSigningKey(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        Install(LoadOrCreate(secrets));
    }

    public string Algorithm => PacketAuditExportService.EcdsaP256Sha256;
    public string KeyId { get { lock (_gate) return _keyId; } }
    public string PublicKey { get { lock (_gate) return _publicKey; } }

    public byte[] Sign(byte[] canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        try
        {
            lock (_gate)
                return _key.SignData(canonicalPayload, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            // Never propagate provider details which may include protected-key diagnostics.
            throw new InvalidOperationException("Audit payload signing failed.");
        }
    }

    /// <summary>
    /// Rotates to a fresh P-256 key: retires the current generation in the trust file and
    /// lists the new one as trusted in one atomic write, then overwrites the stored private
    /// key. Historical documents stay verifiable through the retired public key.
    /// </summary>
    public void Rotate(AuditKeyTrustFile trust, string? note)
    {
        ArgumentNullException.ThrowIfNull(trust);
        lock (_gate)
        {
            var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] newPublicKey;
            try
            {
                newPublicKey = created.ExportSubjectPublicKeyInfo();
                var previousSnapshot = trust.Generations;
                trust.RotateGeneration(_keyId,
                    PacketAuditExportService.Fingerprint(newPublicKey),
                    Convert.ToBase64String(newPublicKey), note);
                try
                {
                    _secrets.Set(SecretName, Convert.ToBase64String(created.ExportPkcs8PrivateKey()));
                }
                catch
                {
                    // Compensate a failed secret write so the trust file never lists a key
                    // that the active identity does not actually use.
                    trust.Restore(previousSnapshot);
                    throw;
                }
            }
            catch
            {
                created.Dispose();
                throw;
            }
            var previous = _key;
            Install(created);
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate) _key.Dispose();
    }

    private void Install(ECDsa key)
    {
        _key = key;
        var publicKey = key.ExportSubjectPublicKeyInfo();
        _publicKey = Convert.ToBase64String(publicKey);
        _keyId = PacketAuditExportService.Fingerprint(publicKey);
    }

    private static ECDsa LoadOrCreate(ISecretStore secrets)
    {
        var encoded = secrets.Get(SecretName);
        if (!string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                var stored = Convert.FromBase64String(encoded);
                var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(stored, out var bytesRead);
                if (bytesRead == stored.Length && key.KeySize == 256 &&
                    key.ExportParameters(false).Curve.Oid.Value == P256Oid) return key;
                key.Dispose();
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                // An unreadable secret cannot be repaired; replace it without exposing its value.
            }
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        secrets.Set(SecretName, Convert.ToBase64String(created.ExportPkcs8PrivateKey()));
        return created;
    }
}
