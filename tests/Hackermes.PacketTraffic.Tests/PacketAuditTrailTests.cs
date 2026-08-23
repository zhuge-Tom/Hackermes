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

    [Fact]
    public void Record_StampsOperatorFromProviderAtTheSingleSeam()
    {
        var trail = new PacketAuditTrail(PathName, () => "analyst-one");
        trail.Record(Entry("stamped"));
        trail.Record(Entry("explicit", null) with { Operator = "explicit-operator" });

        var entries = new PacketAuditTrail(PathName, () => "analyst-one").Query(new PacketAuditQuery());
        Assert.Equal("analyst-one", entries.Single(e => e.PacketId == "stamped").Operator);
        Assert.Equal("explicit-operator", entries.Single(e => e.PacketId == "explicit").Operator);
    }

    [Fact]
    public void Record_SanitizesOperatorAndPersistsIt()
    {
        var trail = new PacketAuditTrail(PathName, () => "  padded name exceeding sixty four characters in total length limit  ");
        trail.Record(Entry("sanitized"));

        var saved = Assert.Single(trail.Query(new PacketAuditQuery()));
        Assert.Equal(64, saved.Operator!.Length);
        Assert.StartsWith("padded", saved.Operator);

        var empty = new PacketAuditTrail(PathName, () => "   ");
        empty.Record(Entry("blank"));
        Assert.Null(empty.Query(new PacketAuditQuery()).Single(e => e.PacketId == "blank").Operator);
    }

    [Fact]
    public void Load_AcceptsLegacyEntriesWithoutOperatorField()
    {
        // Mirrors a v0.8.0 audit file: same schema version, entries without an operator member.
        var sha = new string('a', 64);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathName, $$"""
        {
          "version": 1,
          "entries": [
            {
              "auditId": "legacy-1",
              "timestamp": "2026-08-14T00:00:00Z",
              "entryPoint": "workbench",
              "operation": 0,
              "packetId": "legacy",
              "side": "request",
              "before": { "length": 3, "sha256": "{{sha}}", "contentLength": "3" },
              "after": { "length": 3, "sha256": "{{sha}}", "contentLength": "3" },
              "result": 1
            }
          ]
        }
        """);

        var restored = Assert.Single(new PacketAuditTrail(PathName).Query(new PacketAuditQuery()));
        Assert.Equal("legacy", restored.PacketId);
        Assert.Null(restored.Operator);
    }

    [Fact]
    public void Record_RejectsControlCharactersInOperator()
    {
        var trail = new PacketAuditTrail(PathName);
        Assert.Throws<ArgumentException>(() => trail.Record(Entry("bad") with { Operator = "bad\u0001operator" }));
        Assert.Throws<ArgumentException>(() => trail.Record(Entry("long") with { Operator = new string('x', 65) }));
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
