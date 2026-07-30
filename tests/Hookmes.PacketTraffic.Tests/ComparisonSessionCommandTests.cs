using Hookmes.Automation.Commands;
using Hookmes.Automation.Traffic;
using Hookmes.Traffic.Comparison;
using Hookmes.Traffic.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class ComparisonSessionCommandTests
{
    [Fact]
    public async Task Commands_provide_persistent_session_crud_parity()
    {
        var service = new FakeComparisonService();

        var created = await Execute(service,
            "create traffic-request:page:left traffic-response:page:right Authentication regression");
        var id = service.GetAll().Single().Id;
        var listed = await Execute(service, "list");
        var renamed = await Execute(service, $"rename {id} Renamed comparison");
        var recalculated = await Execute(service, $"recalculate {id}");
        var deleted = await Execute(service, $"delete {id}");

        Assert.True(created.Success);
        Assert.Contains("Authentication regression", created.Output);
        Assert.Contains(id, listed.Output);
        Assert.Contains("Renamed comparison", renamed.Output);
        Assert.Contains("rev=3", recalculated.Output);
        Assert.Equal("Comparison session deleted.", deleted.Output);
        Assert.Empty(service.GetAll());
    }

    [Theory]
    [InlineData("traffic-request:page:fetch", ComparisonSourceKind.TrafficRequest, "page:fetch", null, null)]
    [InlineData("traffic-response:page:fetch", ComparisonSourceKind.TrafficResponse, "page:fetch", null, null)]
    [InlineData("repeater-request:draft:send", ComparisonSourceKind.RepeaterRequest, null, "draft", "send")]
    [InlineData("repeater-response:draft:send", ComparisonSourceKind.RepeaterResponse, null, "draft", "send")]
    public void Source_references_are_unambiguous(
        string value, ComparisonSourceKind kind, string? packetId, string? draftId, string? sendId)
    {
        var source = ComparisonSessionCommandRegistrar.ParseSource(value);
        Assert.Equal(kind, source.Kind);
        Assert.Equal(packetId, source.PacketId);
        Assert.Equal(draftId, source.DraftId);
        Assert.Equal(sendId, source.SendResultId);
    }

    [Fact]
    public async Task Invalid_source_and_missing_session_are_returned_as_command_failures()
    {
        var service = new FakeComparisonService();
        Assert.False((await Execute(service, "create packet:a packet:b name")).Success);
        Assert.False((await Execute(service, "recalculate missing")).Success);
        Assert.True((await Execute(service, "delete missing")).Success);
    }

    private static Task<CommandResult> Execute(ITrafficComparisonService service, string arguments) =>
        ComparisonSessionCommandRegistrar.ExecuteAsync(service, new CommandContext
        {
            Args = CommandLineParser.Tokenize(arguments),
            PageId = null,
            RawInput = ComparisonSessionCommandRegistrar.CommandName + " " + arguments,
            RawArguments = arguments
        });

    private sealed class FakeComparisonService : ITrafficComparisonService
    {
        private readonly Dictionary<string, TrafficComparisonSession> _sessions = new(StringComparer.Ordinal);
        public event Action<TrafficComparisonChanged>? Changed;
        public string StorageFilePath => "memory";
        public TrafficComparisonResult Compare(ComparisonSource left, ComparisonSource right) => Result(false);
        public IReadOnlyList<TrafficComparisonSession> GetAll() => _sessions.Values.ToArray();
        public TrafficComparisonSession? Get(string id) => _sessions.GetValueOrDefault(id);
        public TrafficComparisonSession Create(string name, ComparisonSource left, ComparisonSource right)
        {
            var now = DateTimeOffset.UtcNow;
            var item = new TrafficComparisonSession(Guid.NewGuid().ToString("N"), name, left, right, Result(false), now, now, 1);
            _sessions.Add(item.Id, item);
            Changed?.Invoke(new TrafficComparisonChanged("create", item.Id, item));
            return item;
        }
        public TrafficComparisonSession Rename(string id, string name)
        {
            var current = Required(id);
            return _sessions[id] = current with { Name = name, Revision = current.Revision + 1 };
        }
        public TrafficComparisonSession UpdateSources(string id, ComparisonSource left, ComparisonSource right)
        {
            var current = Required(id);
            return _sessions[id] = current with { Left = left, Right = right, Revision = current.Revision + 1 };
        }
        public TrafficComparisonSession Recalculate(string id)
        {
            var current = Required(id);
            return _sessions[id] = current with { Result = Result(true), Revision = current.Revision + 1 };
        }
        public bool Delete(string id) => _sessions.Remove(id);
        public void Reload() { }
        private TrafficComparisonSession Required(string id) => _sessions.TryGetValue(id, out var item)
            ? item : throw new KeyNotFoundException(id);
        private static TrafficComparisonResult Result(bool equal) => new([], [],
            new BodyDifference(equal,
                new BodySummary(BodyContentKind.Empty, 0, string.Empty, null, string.Empty),
                new BodySummary(BodyContentKind.Empty, 0, string.Empty, null, string.Empty), null), equal);
    }
}
