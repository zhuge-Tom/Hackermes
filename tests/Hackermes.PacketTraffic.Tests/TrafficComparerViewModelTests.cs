using Hackermes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficComparerViewModelTests
{
    [Fact]
    public void Current_traffic_and_repeater_selection_fill_source_references()
    {
        var service = new ComparerWorkbenchFake();
        var model = new TrafficComparerViewModel(service);

        model.SelectedLeftSource = model.Sources.Single(item => item.Kind == "Traffic request");
        model.SelectedRightSource = model.Sources.Single(item => item.Kind == "Repeater response");

        Assert.Equal("traffic-request:packet", model.LeftSourceReference);
        Assert.Equal("repeater-response:draft:send", model.RightSourceReference);
    }

    [Fact]
    public async Task Persistent_session_commands_cover_create_rename_recalculate_and_delete()
    {
        var service = new ComparerWorkbenchFake();
        var model = new TrafficComparerViewModel(service)
        {
            LeftSourceReference = "traffic-request:packet",
            RightSourceReference = "repeater-response:draft:send",
            SessionName = "Initial comparison"
        };

        await model.CreateSessionCommand.ExecuteAsync(null);
        Assert.Single(model.Sessions);
        Assert.Equal("Initial comparison", model.SelectedSession!.Name);

        model.SessionName = "Renamed comparison";
        await model.RenameSessionCommand.ExecuteAsync(null);
        Assert.Equal("Renamed comparison", model.SelectedSession!.Name);

        await model.RecalculateSessionCommand.ExecuteAsync(null);
        Assert.Equal(3, model.SelectedSession!.Revision);
        Assert.Contains("recalculated", model.Result);

        await model.DeleteSessionCommand.ExecuteAsync(null);
        Assert.Empty(model.Sessions);
        Assert.Null(model.SelectedSession);
    }

    [Fact]
    public async Task Ad_hoc_compare_uses_source_references_without_creating_session()
    {
        var service = new ComparerWorkbenchFake();
        var model = new TrafficComparerViewModel(service)
        {
            LeftSourceReference = "traffic-response:left",
            RightSourceReference = "traffic-response:right"
        };

        await model.CompareCommand.ExecuteAsync(null);

        Assert.Equal(("traffic-response:left", "traffic-response:right"), service.LastCompared);
        Assert.Equal("ad-hoc result", model.Result);
        Assert.Empty(model.Sessions);
    }

    private sealed class ComparerWorkbenchFake : ITrafficComparerWorkbenchService
    {
        private readonly List<TrafficComparerSessionItem> _sessions = [];
        public IReadOnlyList<TrafficComparerSessionItem> Sessions => _sessions.ToArray();
        public IReadOnlyList<TrafficComparerSourceItem> Sources =>
        [
            new("traffic-request:packet", "Traffic request", "Traffic request"),
            new("repeater-response:draft:send", "Repeater response", "Repeater response")
        ];
        public event Action? Changed;
        public (string Left, string Right)? LastCompared { get; private set; }

        public Task<string> CompareAsync(string leftPacketId, string rightPacketId, string side, CancellationToken cancellationToken) =>
            Task.FromResult("legacy result");
        public Task<string> CompareSourcesAsync(string leftReference, string rightReference, CancellationToken cancellationToken)
        {
            LastCompared = (leftReference, rightReference);
            return Task.FromResult("ad-hoc result");
        }
        public Task<TrafficComparerSessionItem> CreateSessionAsync(string name, string leftReference, string rightReference, CancellationToken cancellationToken)
        {
            var item = Item(Guid.NewGuid().ToString("N"), name, leftReference, rightReference, 1, "created");
            _sessions.Add(item);
            return Task.FromResult(item);
        }
        public Task<TrafficComparerSessionItem> RenameSessionAsync(string id, string name, CancellationToken cancellationToken)
        {
            var index = _sessions.FindIndex(item => item.Id == id);
            var updated = _sessions[index] with { Name = name, Revision = _sessions[index].Revision + 1 };
            _sessions[index] = updated;
            return Task.FromResult(updated);
        }
        public Task<TrafficComparerSessionItem> RecalculateSessionAsync(string id, CancellationToken cancellationToken)
        {
            var index = _sessions.FindIndex(item => item.Id == id);
            var updated = _sessions[index] with { Result = "recalculated result", Revision = _sessions[index].Revision + 1 };
            _sessions[index] = updated;
            return Task.FromResult(updated);
        }
        public Task DeleteSessionAsync(string id, CancellationToken cancellationToken)
        {
            _sessions.RemoveAll(item => item.Id == id);
            return Task.CompletedTask;
        }

        private static TrafficComparerSessionItem Item(
            string id, string name, string left, string right, int revision, string result) =>
            new(id, name, left, right, "Different", result, revision, DateTimeOffset.UtcNow);
    }
}
