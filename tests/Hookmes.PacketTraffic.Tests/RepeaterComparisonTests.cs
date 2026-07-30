using Hookmes.Traffic.Comparison;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class RepeaterComparisonTests
{
    [Fact]
    public async Task Repeater_send_records_binary_history_and_returns_deep_snapshots()
    {
        var store = new TrafficStore();
        store.Import(Message("source", [0x00, 0xff]));
        var replay = new FakeTrafficService(new TrafficReplayResult(201, "Created",
            [new TrafficHeader("Content-Type", "application/octet-stream")], [0xff, 0x00, 0x41]));
        var repeater = new RepeaterService(store, replay, TempFile("repeater"));
        var changed = new List<string>();
        repeater.Changed += item => changed.Add(item.Operation);
        var draft = repeater.CreateFromPacket("source", "binary send");

        var result = await repeater.SendAsync(draft.Id);

        Assert.Equal(RepeaterSendStatus.Completed, result.Status);
        Assert.Equal(201, result.ResponseStatus);
        Assert.Equal([0xff, 0x00, 0x41], result.ResponseBody);
        Assert.Equal(1, result.Sequence);
        Assert.Equal(["create", "send-started", "send-completed"], changed);
        result.ResponseBody![0] = 0;
        Assert.Equal(0xff, repeater.Get(draft.Id)!.History.Single().ResponseBody![0]);
    }

    [Fact]
    public async Task Repeater_caps_history_at_two_hundred_and_keeps_monotonic_sequences()
    {
        var store = new TrafficStore();
        store.Import(Message("source", []));
        var repeater = new RepeaterService(store, new FakeTrafficService(
            new TrafficReplayResult(204, "No Content", [], [])), TempFile("repeater-cap"));
        var draft = repeater.CreateFromPacket("source");

        for (var index = 0; index < 201; index++) await repeater.SendAsync(draft.Id);

        var history = repeater.Get(draft.Id)!.History;
        Assert.Equal(200, history.Count);
        Assert.Equal(2, history[0].Sequence);
        Assert.Equal(201, history[^1].Sequence);
    }

    [Fact]
    public void Comparer_classifies_binary_bodies_and_reports_first_different_byte()
    {
        var store = new TrafficStore();
        store.Import(Message("left", [0x00, 0x01, 0x02, 0x03]));
        store.Import(Message("right", [0x00, 0x01, 0xff, 0x03]));
        var repeater = new RepeaterService(store, new FakeTrafficService(
            new TrafficReplayResult(200, "OK", [], [])), TempFile("repeater-compare"));
        var comparer = new TrafficComparisonService(store, repeater, TempFile("comparison"));

        var result = comparer.Compare(
            new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "left"),
            new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "right"));

        Assert.False(result.Equal);
        Assert.Equal(BodyContentKind.Binary, result.Body.Left.Kind);
        Assert.Equal(BodyContentKind.Binary, result.Body.Right.Kind);
        Assert.Equal(2, result.Body.FirstDifferentByteOffset);
        Assert.NotEqual(result.Body.Left.Sha256, result.Body.Right.Sha256);
    }

    [Fact]
    public void Comparison_session_recalculates_after_source_changes()
    {
        var store = new TrafficStore();
        store.Import(Message("left", [1, 2]));
        store.Import(Message("right", [1, 3]));
        var comparer = new TrafficComparisonService(store, new RepeaterService(store,
            new FakeTrafficService(new TrafficReplayResult(200, "OK", [], [])), TempFile("repeater-session")),
            TempFile("comparison-session"));
        var sourceLeft = new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "left");
        var sourceRight = new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "right");
        var session = comparer.Create("binary comparison", sourceLeft, sourceRight);

        store.Import(Message("right", [1, 2]));
        var updated = comparer.Recalculate(session.Id);

        Assert.True(updated.Result.Equal);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(session.Id, comparer.GetAll().Single().Id);
    }

    private static TrafficMessage Message(string id, byte[] body) => new(
        id, "page", TrafficStage.Request, TrafficState.Continued, "POST", "https://example.test/api",
        [new TrafficHeader("Content-Type", "application/octet-stream")], body,
        null, null, [], null, "Fetch", DateTimeOffset.UtcNow);

    private static string TempFile(string name) => Path.Combine(
        Path.GetTempPath(), "hookmes-persistence-tests", Guid.NewGuid().ToString("N"), name + ".json");

    private sealed class FakeTrafficService(TrafficReplayResult replay) : ITrafficService
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
