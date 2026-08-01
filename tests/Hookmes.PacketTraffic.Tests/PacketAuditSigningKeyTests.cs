using Hookmes.App;
using Hookmes.Automation.Packet;
using Hookmes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class PacketAuditSigningKeyTests
{
    [Fact]
    public void First_use_persists_pkcs8_and_reuses_stable_public_identity()
    {
        var secrets = new SecretStoreFake();
        string firstKeyId;
        string firstPublicKey;
        using (var first = new PacketAuditSigningKey(secrets))
        {
            firstKeyId = first.KeyId;
            firstPublicKey = first.PublicKey;
        }

        var encodedPrivateKey = Assert.IsType<string>(secrets.Get(PacketAuditSigningKey.SecretName));
        using (var imported = ECDsa.Create())
        {
            var pkcs8 = Convert.FromBase64String(encodedPrivateKey);
            imported.ImportPkcs8PrivateKey(pkcs8, out var read);
            Assert.Equal(pkcs8.Length, read);
            Assert.Equal(256, imported.KeySize);
        }

        using var second = new PacketAuditSigningKey(secrets);
        Assert.Equal(firstKeyId, second.KeyId);
        Assert.Equal(firstPublicKey, second.PublicKey);
        Assert.Equal(PacketAuditExportService.Fingerprint(Convert.FromBase64String(second.PublicKey)), second.KeyId);
        Assert.DoesNotContain(encodedPrivateKey, second.PublicKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_verifies_with_spki_public_key()
    {
        using var key = new PacketAuditSigningKey(new SecretStoreFake());
        var payload = Encoding.UTF8.GetBytes("bounded metadata-only audit payload");
        var signature = key.Sign(payload);

        using var verifier = ECDsa.Create();
        var spki = Convert.FromBase64String(key.PublicKey);
        verifier.ImportSubjectPublicKeyInfo(spki, out var read);
        Assert.Equal(spki.Length, read);
        Assert.True(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256));
        Assert.Equal(PacketAuditExportService.EcdsaP256Sha256, key.Algorithm);
    }

    [Fact]
    public void Corrupt_secret_is_replaced_without_exposing_it_through_public_contract()
    {
        var secrets = new SecretStoreFake();
        secrets.Set(PacketAuditSigningKey.SecretName, "private-material-that-is-not-pkcs8");

        using var key = new PacketAuditSigningKey(secrets);

        Assert.NotEqual("private-material-that-is-not-pkcs8", secrets.Get(PacketAuditSigningKey.SecretName));
        Assert.DoesNotContain("private-material", key.KeyId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-material", key.PublicKey, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SecretStoreFake : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public void Set(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) _values.Remove(key);
            else _values[key] = value;
        }
        public bool Contains(string key) => _values.ContainsKey(key);
        public void Remove(string key) => _values.Remove(key);
    }
}
