using Hackermes.Automation.Packet;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketAuditTrailTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hackermes-audit-tests-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_directory, "audit.json");

    [Fact]
    public void Record_PersistsOnlyMetadataAndSanitizedErrorCode()
    {
        var trail = new PacketAuditTrail(PathName);
        trail.Record(Entry("packet-1", "System.Exception: secret=do-not-store"));

        var text = File.ReadAllText(PathName);
        Assert.Contains("\"version\": 1", text);
        Assert.DoesNotContain("do-not-store", text);
        var saved = Assert.Single(new PacketAuditTrail(PathName).Query(new PacketAuditQuery()));
        Assert.Equal("System.Exception", saved.ErrorCode);
        Assert.Equal(3, saved.Before.Length);
    }

    [Fact]
    public void Load_RejectsInvalidPrimaryAndFallsBackToValidBackup()
    {
        var trail = new PacketAuditTrail(PathName);
        trail.Record(Entry("first"));
        trail.Record(Entry("second")); // atomic replace leaves first snapshot in .bak
        File.WriteAllText(PathName, "{\"version\":1,\"entries\":[{\"side\":\"invalid\"}]}");

        var recovered = new PacketAuditTrail(PathName).Query(new PacketAuditQuery());
        Assert.Single(recovered);
        Assert.Equal("first", recovered[0].PacketId);

        var repaired = new PacketAuditTrail(PathName);
        repaired.Record(Entry("third"));
        recovered = new PacketAuditTrail(PathName).Query(new PacketAuditQuery()).ToArray();
        Assert.Contains(recovered, item => item.PacketId == "third");
        Assert.True(File.Exists(PathName + ".bak"));
    }

    [Fact]
    public void Query_IsNewestFirstBoundedAndFilterable()
    {
        var trail = new PacketAuditTrail(PathName);
        trail.Record(Entry("one")); trail.Record(Entry("two")); trail.Record(Entry("one"));
        var result = trail.Query(new PacketAuditQuery("one", Limit: 1));
        Assert.Single(result);
        Assert.Equal("one", result[0].PacketId);
    }

    private static PacketAuditEntry Entry(string packetId, string? error = null)
    {
        var version = new PacketEditVersion(3, new string('a', 64), "3");
        return new PacketAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "test",
            PacketAuditOperation.Edit, packetId, "request", version, version,
            error is null ? PacketAuditResult.Succeeded : PacketAuditResult.Failed, error);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
