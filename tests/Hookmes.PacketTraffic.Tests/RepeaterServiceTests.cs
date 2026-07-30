using Hookmes.Traffic.Models;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class RepeaterServiceTests
{
    [Fact]
    public async Task CreateEditAndSend_RecordsImmutableHistoryAndMetrics()
    {
        var store = new TrafficStore();
        store.Import(new TrafficMessage("packet-1", "page-1", TrafficStage.Request, TrafficState.Continued,
            "POST", "https://example.test/api", [new TrafficHeader("Content-Type", "application/octet-stream")],
            [0, 1, 255], null, null, [], null, "Fetch", DateTimeOffset.UtcNow));
        var transport = new FakeTrafficService();
        var service = new RepeaterService(store, transport);

        var draft = service.CreateFromPacket("packet-1", "binary request");
        var originalBody = draft.Request.Body!;
        originalBody[0] = 99;
        service.Update(draft.Id, new RepeaterDraftUpdate(Method: "PUT", Body: [4, 5], ReplaceBody: true));
        var result = await service.SendAsync(draft.Id);
        var saved = service.Get(draft.Id)!;

        Assert.Equal(RepeaterSendStatus.Completed, result.Status);
        Assert.Equal(204, result.ResponseStatus);
        Assert.True(result.RequestSize > 2);
        Assert.True(result.ResponseSize > 3);
        Assert.Equal("PUT", saved.Request.Method);
        Assert.Equal([4, 5], saved.Request.Body);
        Assert.Single(saved.History);
        Assert.Equal([4, 5], saved.History[0].Request.Body);
        Assert.Equal([7, 8, 9], saved.History[0].ResponseBody);
    }

    [Fact]
    public void DeleteAndClearHistory_KeepDraftLifecycleConsistent()
    {
        var store = new TrafficStore();
        var service = new RepeaterService(store, new FakeTrafficService());
        var draft = service.Create("manual", "source", "page",
            new RepeaterRequest("GET", "https://example.test/", [], null));

        service.ClearHistory(draft.Id);
        Assert.True(service.Delete(draft.Id));
        Assert.Null(service.Get(draft.Id));
        Assert.False(service.Delete(draft.Id));
    }

    private sealed class FakeTrafficService : ITrafficService
    {
        public bool ModificationsEnabled => true;
        public void SetModificationsEnabled(bool enabled) { }
        public Task StartCaptureAsync(string pageId, TrafficCaptureOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopCaptureAsync(string pageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ContinueAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(string id, string reason = "BlockedByClient", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FulfillAsync(string id, TrafficResponseEdit response, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TrafficReplayResult> ReplayAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrafficReplayResult(204, "No Content", [new TrafficHeader("X-Test", "ok")], [7, 8, 9]));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
