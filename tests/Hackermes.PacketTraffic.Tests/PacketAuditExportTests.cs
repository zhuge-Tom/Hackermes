using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketAuditExportTests
{
    [Fact]
    public void Export_is_offline_verifiable_and_supports_trusted_key_pin()
    {
        using var key = new TestKey();
        var service = new PacketAuditExportService(new MemoryAudit(), key);

        var content = service.Export(new PacketAuditQuery(Limit: 10));
        var verified = service.Verify(content, key.KeyId);

        Assert.True(verified.Valid);
        Assert.Equal(key.KeyId, verified.KeyId);
        Assert.Equal(1, verified.EntryCount);
    }

    [Fact]
    public void Verify_rejects_payload_tampering_and_untrusted_key()
    {
        using var key = new TestKey();
        var service = new PacketAuditExportService(new MemoryAudit(), key);
        var content = service.Export(new PacketAuditQuery());

        Assert.Equal("invalid_signature", service.Verify(content.Replace("packet-1", "packet-2", StringComparison.Ordinal)).ErrorCode);
        Assert.Equal("untrusted_key", service.Verify(content, new string('0', 64)).ErrorCode);
    }

    [Fact]
    public void Verify_rejects_empty_and_oversized_content_without_parsing()
    {
        using var key = new TestKey();
        var service = new PacketAuditExportService(new MemoryAudit(), key);

        Assert.Equal("empty_content", service.Verify(" ").ErrorCode);
        Assert.Equal("content_too_large", service.Verify(new string('x', PacketAuditExportService.MaximumContentBytes + 1)).ErrorCode);
    }

    private sealed class MemoryAudit : IPacketAuditTrail
    {
        private static readonly PacketEditVersion Version = new(3, new string('a', 64), "3");
        public void Record(PacketAuditEntry entry) { }
        public IReadOnlyList<PacketAuditEntry> Query(PacketAuditQuery query) =>
            [new("audit-1", DateTimeOffset.UtcNow, "test", PacketAuditOperation.Edit,
                "packet-1", "request", Version, Version, PacketAuditResult.Succeeded)];
    }

    private sealed class TestKey : IPacketAuditSigningKey, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _publicKey;
        public TestKey() => _publicKey = _key.ExportSubjectPublicKeyInfo();
        public string Algorithm => PacketAuditExportService.EcdsaP256Sha256;
        public string KeyId => PacketAuditExportService.Fingerprint(_publicKey);
        public string PublicKey => Convert.ToBase64String(_publicKey);
        public byte[] Sign(byte[] canonicalPayload) => _key.SignData(canonicalPayload, HashAlgorithmName.SHA256);
        public void Dispose() => _key.Dispose();
    }
}
