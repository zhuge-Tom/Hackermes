using Hackermes.Inspector.ViewModels;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Workspace policy switches must surface in the workbench history panel without a
/// manual refresh: the view model loads the overview once up front and re-reads it
/// whenever the service announces a policy switch.
/// </summary>
public sealed class TrafficWorkbenchHistoryPolicyRefreshTests
{
    [Fact]
    public void Constructor_loads_history_overview_once()
    {
        var service = new WorkbenchServiceFake
        {
            NextHistoryOverview = new(3, 300, 3000, null, null, 1234, 4096, 21, true,
                [], "workspace")
        };

        using var model = new TrafficWorkbenchViewModel(service);

        Assert.Equal(1, service.HistoryOverviewRequests);
        Assert.Contains("policy workspace", model.HistoryStatus);
        Assert.Equal("1234", model.HistoryMaxEntries);
    }

    [Fact]
    public void Policy_changed_event_reloads_overview_and_updates_status()
    {
        var service = new WorkbenchServiceFake();
        using var model = new TrafficWorkbenchViewModel(service);
        Assert.Contains("policy global", model.HistoryStatus);

        service.NextHistoryOverview = new(7, 700, 7000, null, null, 42, 8192, 5, false,
            [], "workspace");
        service.RaiseHistoryPolicyChanged();

        Assert.Equal(2, service.HistoryOverviewRequests);
        Assert.Contains("policy workspace", model.HistoryStatus);
        Assert.Contains("7 entries", model.HistoryStatus);
        Assert.Equal("42", model.HistoryMaxEntries);
        Assert.Equal("8192", model.HistoryMaxBytes);
        Assert.Equal("5", model.HistoryRetentionDays);
        Assert.False(model.HistoryAutoPrune);
    }

    [Fact]
    public void Disposed_view_model_stops_listening_for_policy_changes()
    {
        var service = new WorkbenchServiceFake();
        var model = new TrafficWorkbenchViewModel(service);
        var requestsAfterConstruction = service.HistoryOverviewRequests;

        model.Dispose();
        service.RaiseHistoryPolicyChanged();

        Assert.Equal(requestsAfterConstruction, service.HistoryOverviewRequests);
    }
}
