using Hookmes.Traffic.Comparison;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class TrafficPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hookmes-persistence-tests", Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Traffic_history_round_trip_is_binary_lossless_and_releases_paused_state()
    {
        var path = PathFor("history.json.gz");
        using (var persistence = new TrafficHistoryPersistence(path))
        {
            persistence.ScheduleSave([Message("one", TrafficState.Paused, [0, 255, 1])]);
            persistence.Flush();
        }

        using var loadedPersistence = new TrafficHistoryPersistence(path);
        var loaded = Assert.Single(loadedPersistence.Load());
        Assert.Equal([0, 255, 1], loaded.RequestBody);
        Assert.Equal(TrafficState.Continued, loaded.State);
    }

    [Fact]
    public void Traffic_history_uses_backup_when_primary_is_corrupt()
    {
        var path = PathFor("history.json.gz");
        using var persistence = new TrafficHistoryPersistence(path);
        persistence.ScheduleSave([Message("first")]);
        persistence.Flush();
        persistence.ScheduleSave([Message("first"), Message("second")]);
        persistence.Flush();
        File.WriteAllText(path, "not gzip");

        var loaded = persistence.Load();

        Assert.Equal(["first"], System.Linq.Enumerable.Select(loaded, x => x.Id));
    }

    [Fact]
    public void Traffic_history_rejects_unknown_version()
    {
        var path = PathFor("history.json.gz");
        Directory.CreateDirectory(_directory);
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            writer.Write("{\"schemaVersion\":999,\"messages\":[]}");

        using var persistence = new TrafficHistoryPersistence(path);
        Assert.Empty(persistence.Load());
    }

    [Fact]
    public void Traffic_history_failed_write_leaves_primary_readable_and_unchanged()
    {
        var path = PathFor("history.json.gz");
        using var persistence = new TrafficHistoryPersistence(path);
        persistence.ScheduleSave([Message("stable")]);
        persistence.Flush();
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        persistence.ScheduleSave([Message("replacement")]);
        persistence.Flush();

        Assert.Equal("stable", Assert.Single(persistence.Load()).Id);
    }

    [Fact]
    public async Task Repeater_round_trip_preserves_completed_binary_history()
    {
        var path = PathFor("repeater.json");
        var store = new TrafficStore();
        store.Import(Message("source", body: [0, 255]));
        var replay = new ReplayTrafficService(new TrafficReplayResult(201, "Created", [], [255, 0]));
        var service = new RepeaterService(store, replay, path);
        var draft = service.CreateFromPacket("source", "persist me");
        await service.SendAsync(draft.Id);

        var restored = new RepeaterService(store, replay, path);
        var loaded = Assert.Single(restored.GetAll());

        Assert.Equal(draft.Id, loaded.Id);
        var result = Assert.Single(loaded.History);
        Assert.Equal(RepeaterSendStatus.Completed, result.Status);
        Assert.Equal([255, 0], result.ResponseBody);
    }

    [Fact]
    public void Repeater_and_comparer_reject_unknown_versions()
    {
        var store = new TrafficStore();
        var replay = new ReplayTrafficService(new TrafficReplayResult(200, "OK", [], []));
        var repeaterPath = PathFor("repeater.json");
        var comparisonPath = PathFor("comparisons.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(repeaterPath, "{\"schemaVersion\":999,\"drafts\":[]}");
        File.WriteAllText(comparisonPath, "{\"schemaVersion\":999,\"sessions\":[]}");
        var repeater = new RepeaterService(store, replay, repeaterPath);

        Assert.Empty(repeater.GetAll());
        Assert.Empty(new TrafficComparisonService(store, repeater, comparisonPath).GetAll());
    }

    [Fact]
    public void Repeater_and_comparer_fall_back_to_backups()
    {
        var store = new TrafficStore();
        store.Import(Message("left"));
        store.Import(Message("right", body: [2]));
        var replay = new ReplayTrafficService(new TrafficReplayResult(200, "OK", [], []));
        var repeaterPath = PathFor("repeater.json");
        var repeater = new RepeaterService(store, replay, repeaterPath);
        var first = repeater.CreateFromPacket("left", "first");
        repeater.CreateFromPacket("right", "second"); // backup contains first
        File.WriteAllText(repeaterPath, "broken");
        var restoredRepeater = new RepeaterService(store, replay, repeaterPath);
        Assert.Equal(first.Id, Assert.Single(restoredRepeater.GetAll()).Id);

        var comparisonPath = PathFor("comparisons.json");
        var comparer = new TrafficComparisonService(store, restoredRepeater, comparisonPath);
        var left = new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "left");
        var right = new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "right");
        var comparison = comparer.Create("first", left, right);
        comparer.Create("second", left, right); // backup contains first
        File.WriteAllText(comparisonPath, "broken");

        var restoredComparer = new TrafficComparisonService(store, restoredRepeater, comparisonPath);
        Assert.Equal(comparison.Id, Assert.Single(restoredComparer.GetAll()).Id);
    }

    [Fact]
    public void Repeater_failed_atomic_write_rolls_back_memory_and_preserves_primary()
    {
        var path = PathFor("repeater-atomic.json");
        var store = new TrafficStore();
        store.Import(Message("source"));
        var service = new RepeaterService(store,
            new ReplayTrafficService(new TrafficReplayResult(200, "OK", [], [])), path);
        var draft = service.CreateFromPacket("source", "stable");
        var before = File.ReadAllBytes(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => service.Rename(draft.Id, "must roll back"));

        Assert.Equal("stable", service.Get(draft.Id)!.Name);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Comparer_failed_atomic_write_rolls_back_memory_and_preserves_primary()
    {
        var path = PathFor("comparison-atomic.json");
        var store = new TrafficStore();
        store.Import(Message("left"));
        store.Import(Message("right", body: [2]));
        var replay = new ReplayTrafficService(new TrafficReplayResult(200, "OK", [], []));
        var repeater = new RepeaterService(store, replay, PathFor("comparison-atomic-repeater.json"));
        var service = new TrafficComparisonService(store, repeater, path);
        var session = service.Create("stable",
            new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "left"),
            new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "right"));
        var before = File.ReadAllBytes(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => service.Rename(session.Id, "must roll back"));

        Assert.Equal("stable", service.Get(session.Id)!.Name);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    private static TrafficMessage Message(string id, TrafficState state = TrafficState.Continued, byte[]? body = null) => new(
        id, "page", TrafficStage.Request, state, "POST", "https://example.test/api",
        [new TrafficHeader("Content-Type", "application/octet-stream")], body ?? [1],
        null, null, [], null, "Fetch", DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class ReplayTrafficService(TrafficReplayResult replay) : ITrafficService
    {
        public bool ModificationsEnabled => true;
        public void SetModificationsEnabled(bool enabled) { }
        public Task StartCaptureAsync(string pageId, TrafficCaptureOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopCaptureAsync(string pageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ContinueAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(string id, string reason = "BlockedByClient", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FulfillAsync(string id, TrafficResponseEdit response, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TrafficReplayResult> ReplayAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default) => Task.FromResult(replay);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
