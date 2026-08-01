using Hookmes.Automation.Packet;
using Hookmes.Platform.Services;
using System;
using System.Security.Cryptography;

namespace Hookmes.App;

/// <summary>Application-owned audit signing identity backed by the protected secret store.</summary>
internal sealed class PacketAuditSigningKey : IPacketAuditSigningKey, IDisposable
{
    internal const string SecretName = "traffic.audit.ecdsa-p256.pkcs8.v1";
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private readonly ECDsa _key;
    private readonly object _gate = new();

    public PacketAuditSigningKey(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _key = LoadOrCreate(secrets);
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        PublicKey = Convert.ToBase64String(publicKey);
        KeyId = PacketAuditExportService.Fingerprint(publicKey);
    }

    public string Algorithm => PacketAuditExportService.EcdsaP256Sha256;
    public string KeyId { get; }
    public string PublicKey { get; }

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

    public void Dispose()
    {
        lock (_gate) _key.Dispose();
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
