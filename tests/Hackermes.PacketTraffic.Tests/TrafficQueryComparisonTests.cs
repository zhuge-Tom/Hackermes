using Hackermes.Traffic.Comparison;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using Hackermes.Traffic.Repeater;
using Hackermes.Traffic.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficQueryComparisonTests
{
    [Fact]
    public void Query_AppliesCompositeFiltersAndPagination()
    {
        var store = new TrafficStore();
        store.Import(Message("a", "GET", "https://one.test/a", 200, "Fetch", TrafficState.Continued));
        store.Import(Message("b", "POST", "https://two.test/api", 404, "XHR", TrafficState.Paused));
        store.Import(Message("c", "POST", "https://two.test/other", 404, "XHR", TrafficState.Paused));

        var first = store.Query(new TrafficQuery(Text: "two.test", Method: "POST", Status: 404,
            ResourceType: "XHR", State: TrafficState.Paused, Offset: 0, Limit: 1));
        var second = store.Query(new TrafficQuery(Text: "two.test", Method: "POST", Status: 404,
            ResourceType: "XHR", State: TrafficState.Paused, Offset: 1, Limit: 1));

        Assert.Equal(2, first.Total);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
    }

    [Fact]
    public void Compare_BinaryBodiesUsesHashesAndFirstDifferentOffset()
    {
        var store = new TrafficStore();
        store.Import(Message("left", "POST", "https://test/api", 200, "Fetch", TrafficState.Continued, [0, 1, 2]));
        store.Import(Message("right", "POST", "https://test/api", 200, "Fetch", TrafficState.Continued, [0, 9, 2]));
        var comparer = new TrafficComparisonService(store, new RepeaterService(store, new FakeTrafficService()));

        var result = comparer.Compare(
            new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "left"),
            new ComparisonSource(ComparisonSourceKind.TrafficRequest, PacketId: "right"));

        Assert.False(result.Equal);
        Assert.Equal(BodyContentKind.Binary, result.Body.Left.Kind);
        Assert.Equal(1, result.Body.FirstDifferentByteOffset);
        Assert.NotEqual(result.Body.Left.Sha256, result.Body.Right.Sha256);
    }

    private static TrafficMessage Message(string id, string method, string url, int status,
        string resourceType, TrafficState state, byte[]? body = null) =>
        new(id, "page", TrafficStage.Response, state, method, url, [], body, status, "OK", [], [],
            resourceType, DateTimeOffset.UtcNow);

    private sealed class FakeTrafficService : ITrafficService
    {
        public event Action<TrafficRuleExecutionEvent>? RuleExecuted;
        public bool ModificationsEnabled => true;
        public void SetModificationsEnabled(bool enabled) { }
        public Task StartCaptureAsync(string pageId, TrafficCaptureOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopCaptureAsync(string pageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ContinueAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(string id, string reason = "BlockedByClient", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FulfillAsync(string id, TrafficResponseEdit response, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TrafficReplayResult> ReplayAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrafficReplayResult(200, "OK", [], []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
