using Hackermes.Traffic.Annotations;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Services;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficAnnotationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hackermes-annotation-tests", Guid.NewGuid().ToString("N"));
    private string StoragePath => Path.Combine(_directory, "annotations.json");

    [Fact]
    public void Update_query_and_reload_preserve_analysis_context()
    {
        var store = StoreWith("packet-1", "packet-2");
        var service = new TrafficAnnotationService(store, StoragePath);

        service.Update("packet-1", new TrafficAnnotationUpdate(
            Starred: true,
            Tags: ["Auth", "security", "AUTH"],
            Note: "Check token rotation",
            ReplaceNote: true,
            Status: TrafficReviewStatus.InReview));
        service.Update("packet-2", new TrafficAnnotationUpdate(Tags: ["api"], Status: TrafficReviewStatus.Resolved));

        var security = service.Query(new TrafficAnnotationQuery(Tag: "auth", Starred: true));
        Assert.Single(security);
        Assert.Equal(["Auth", "security"], security[0].Tags);
        Assert.Single(service.Query(new TrafficAnnotationQuery(Text: "rotation")));

        var reloaded = new TrafficAnnotationService(store, StoragePath);
        var annotation = reloaded.Get("packet-1")!;
        Assert.Equal(TrafficReviewStatus.InReview, annotation.Status);
        Assert.Equal("Check token rotation", annotation.Note);
        Assert.Equal(1, annotation.Revision);
    }

    [Fact]
    public void Failed_write_does_not_publish_or_change_memory()
    {
        var store = StoreWith("packet-1");
        var service = new TrafficAnnotationService(store, StoragePath);
        service.Update("packet-1", new TrafficAnnotationUpdate(Starred: true));
        var events = 0;
        service.Changed += _ => events++;
        var before = File.ReadAllBytes(StoragePath);

        using (var locked = new FileStream(StoragePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => service.Update("packet-1", new TrafficAnnotationUpdate(Status: TrafficReviewStatus.Resolved)));

        Assert.Equal(TrafficReviewStatus.Unreviewed, service.Get("packet-1")!.Status);
        Assert.Equal(before, File.ReadAllBytes(StoragePath));
        Assert.Equal(0, events);
    }

    [Fact]
    public void Corrupt_primary_falls_back_to_valid_backup()
    {
        var store = StoreWith("packet-1");
        var service = new TrafficAnnotationService(store, StoragePath);
        service.Update("packet-1", new TrafficAnnotationUpdate(Note: "backup", ReplaceNote: true));
        service.Update("packet-1", new TrafficAnnotationUpdate(Note: "primary", ReplaceNote: true));
        File.WriteAllText(StoragePath, "{broken");

        var recovered = new TrafficAnnotationService(store, StoragePath);

        Assert.Equal("backup", recovered.Get("packet-1")!.Note);
    }

    [Fact]
    public void Prune_removes_only_annotations_whose_packets_expired()
    {
        var store = StoreWith("keep", "expire");
        var service = new TrafficAnnotationService(store, StoragePath);
        service.Update("keep", new TrafficAnnotationUpdate(Starred: true));
        service.Update("expire", new TrafficAnnotationUpdate(Starred: true));
        store.Clear();
        store.Import(Packet("keep"));

        Assert.Equal(1, service.PruneMissingPackets());
        Assert.NotNull(service.Get("keep"));
        Assert.Null(service.Get("expire"));
    }

    [Fact]
    public void Cannot_create_annotation_for_unknown_packet()
    {
        var service = new TrafficAnnotationService(new TrafficStore(), StoragePath);
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() =>
            service.Update("missing", new TrafficAnnotationUpdate(Starred: true)));
    }

    [Fact]
    public void Batch_update_is_atomic_and_bounded_to_existing_packets()
    {
        var service = new TrafficAnnotationService(StoreWith("packet-1", "packet-2"), StoragePath);
        var changed = service.UpdateMany(["packet-1", "packet-2", "packet-1"],
            new TrafficAnnotationUpdate(Tags: ["triage"], Status: TrafficReviewStatus.InReview));

        Assert.Equal(2, changed.Count);
        Assert.All(changed, annotation => Assert.Equal(TrafficReviewStatus.InReview, annotation.Status));
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => service.UpdateMany(["packet-1", "missing"], new TrafficAnnotationUpdate(Starred: true)));
        Assert.False(service.Get("packet-1")!.Starred);
    }

    private static TrafficStore StoreWith(params string[] ids)
    {
        var store = new TrafficStore();
        foreach (var id in ids) store.Import(Packet(id));
        return store;
    }

    private static TrafficMessage Packet(string id) => new(
        id, "page", TrafficStage.Request, TrafficState.Continued, "GET", "https://example.test/",
        [], null, null, null, [], null, "Document", DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
