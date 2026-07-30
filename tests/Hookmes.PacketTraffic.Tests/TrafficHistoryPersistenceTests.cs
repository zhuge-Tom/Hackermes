using Hookmes.Traffic.Models;
using Hookmes.Traffic.Services;
using System;
using System.IO;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class TrafficHistoryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hookmes-history-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FlushAndLoad_RoundTripsBinaryAndNormalizesPausedState()
    {
        var path = Path.Combine(_root, "history.gz");
        using (var persistence = new TrafficHistoryPersistence(path))
        {
            persistence.ScheduleSave([Message("one", TrafficState.Paused, [0, 255, 1])]);
            persistence.Flush();
        }

        using var loadedPersistence = new TrafficHistoryPersistence(path);
        var loaded = Assert.Single(loadedPersistence.Load());
        Assert.Equal(TrafficState.Continued, loaded.State);
        Assert.Equal([0, 255, 1], loaded.RequestBody);
    }

    [Fact]
    public void CorruptMain_FallsBackToPreviousBackup()
    {
        var path = Path.Combine(_root, "history.gz");
        using (var persistence = new TrafficHistoryPersistence(path))
        {
            persistence.ScheduleSave([Message("first", TrafficState.Continued, [1])]);
            persistence.Flush();
            persistence.ScheduleSave([Message("second", TrafficState.Continued, [2])]);
            persistence.Flush();
        }
        File.WriteAllText(path, "not gzip");

        using var recovered = new TrafficHistoryPersistence(path);
        Assert.Equal("first", Assert.Single(recovered.Load()).Id);
    }

    [Fact]
    public void TrafficStore_RestoresAndPersistsClear()
    {
        var path = Path.Combine(_root, "history.gz");
        using (var persistence = new TrafficHistoryPersistence(path))
        {
            var store = new TrafficStore(persistence);
            store.Import(Message("saved", TrafficState.Continued, [5]));
            persistence.Flush();
        }
        using (var persistence = new TrafficHistoryPersistence(path))
        {
            var restored = new TrafficStore(persistence);
            Assert.NotNull(restored.Get("saved"));
            restored.Clear();
            persistence.Flush();
        }
        using var finalPersistence = new TrafficHistoryPersistence(path);
        Assert.Empty(finalPersistence.Load());
    }

    private static TrafficMessage Message(string id, TrafficState state, byte[] body) => new(
        id, "page", TrafficStage.Request, state, "POST", "https://example.test/api", [], body,
        null, null, [], null, "Fetch", DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
