using Hookmes.Traffic.Models;
using Hookmes.Traffic.Services;
using System;
using System.Linq;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class TrafficStoreSafetyTests
{
    [Fact]
    public void Mark_paused_continued_releases_only_held_items_for_target_page_and_notifies()
    {
        var store = new TrafficStore();
        store.Import(Message("held-a", "page-a", TrafficState.Paused));
        store.Import(Message("done-a", "page-a", TrafficState.Failed));
        store.Import(Message("held-b", "page-b", TrafficState.Paused));
        var notifications = new System.Collections.Generic.List<TrafficMessage>();
        store.Changed += notifications.Add;

        store.MarkPausedContinued("page-a");

        Assert.Equal(TrafficState.Continued, store.Get("held-a")!.State);
        Assert.Equal(TrafficState.Failed, store.Get("done-a")!.State);
        Assert.Equal(TrafficState.Paused, store.Get("held-b")!.State);
        var changed = Assert.Single(notifications);
        Assert.Equal("held-a", changed.Id);
        Assert.Equal(TrafficState.Continued, changed.State);
    }

    private static TrafficMessage Message(string id, string page, TrafficState state) => new(
        id, page, TrafficStage.Request, state, "GET", "https://example.test/", [], null,
        null, null, [], null, "Fetch", DateTimeOffset.UtcNow);
}
