using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketEditDraftCommandTests
{
    private static readonly PacketEditDraftStatus Draft = new("packet-1", "request", true,
        new PacketEditVersion(3, new string('a', 64), "3"),
        new PacketEditVersion(5, new string('b', 64), "5"),
        new PacketEditCommitFailure("CDP submit failed", DateTimeOffset.Parse("2026-07-30T12:00:00Z"), 2));

    [Fact]
    public async Task DraftList_ExposesBeforeAfterAndFailureEvidence()
    {
        var result = await Execute(new DraftService(), "draft-list");
        Assert.True(result.Success);
        Assert.Contains("before=3/", result.Output);
        Assert.Contains("after=5/", result.Output);
        Assert.Contains("cl:5", result.Output);
        Assert.Contains("attempts=2:CDP submit failed", result.Output);
    }

    [Fact]
    public async Task DraftDiscard_UsesRequestedSideAndReportsMissing()
    {
        var service = new DraftService();
        var discarded = await Execute(service, "draft-discard", "packet-1", "request");
        var missing = await Execute(service, "draft-discard", "packet-1", "request");
        Assert.True(discarded.Success);
        Assert.False(missing.Success);
        Assert.Equal("request", service.DiscardedSide);
    }

    [Fact]
    public async Task DraftCommands_ReportUnsupportedLegacyBackend()
    {
        var result = await Execute(new LegacyService(), "draft-list");
        Assert.False(result.Success);
        Assert.Contains("does not support", result.Output);
    }

    private static Task<CommandResult> Execute(IPacketCommandService service, params string[] args) =>
        PacketCommandRegistrar.ExecuteAsync(service, new CommandContext
        {
            Args = args, PageId = null, RawArguments = string.Join(' ', args), RawInput = "packet " + string.Join(' ', args)
        }, CancellationToken.None);

    private class LegacyService : IPacketCommandService
    {
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DraftService : LegacyService, IPacketEditDraftService
    {
        private bool _pending = true;
        public string? DiscardedSide { get; private set; }
        public Task<IReadOnlyList<PacketEditDraftStatus>> ListPendingEditsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PacketEditDraftStatus>>(_pending ? [Draft] : []);
        public Task<PacketEditDraftStatus?> GetPendingEditAsync(string id, string side, CancellationToken ct) =>
            Task.FromResult<PacketEditDraftStatus?>(_pending && id == Draft.Id && side == Draft.Side ? Draft : null);
        public Task<bool> DiscardPendingEditAsync(string id, string side, CancellationToken ct)
        {
            DiscardedSide = side;
            var result = _pending && id == Draft.Id && side == Draft.Side;
            _pending = false;
            return Task.FromResult(result);
        }
    }
}
