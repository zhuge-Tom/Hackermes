using Hookmes.Automation.Commands;
using Hookmes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class PacketAuditCommandTests
{
    [Fact]
    public async Task AuditCommand_QueriesMetadataWithoutBodyOrHeaders()
    {
        var service = new AuditService();
        var result = await PacketCommandRegistrar.ExecuteAsync(service, new CommandContext
        {
            Args = ["audit", "packet-1", "10"], PageId = null,
            RawInput = "packet audit packet-1 10", RawArguments = "audit packet-1 10"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("BodyEdit", result.Output);
        Assert.Contains("3/", result.Output);
        Assert.DoesNotContain("secret-body", result.Output);
        Assert.Equal("packet-1", service.LastQuery?.PacketId);
        Assert.Equal(10, service.LastQuery?.Limit);
    }

    private sealed class AuditService : IPacketCommandService, IPacketAuditQueryService
    {
        public PacketAuditQuery? LastQuery { get; private set; }
        public IReadOnlyList<PacketAuditEntry> QueryAudit(PacketAuditQuery query)
        {
            LastQuery = query;
            var version = new PacketEditVersion(3, new string('a', 64), "3");
            return [new PacketAuditEntry("audit-1", DateTimeOffset.UtcNow, "test", PacketAuditOperation.BodyEdit,
                "packet-1", "request", version, version, PacketAuditResult.Succeeded)];
        }
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken ct) => Task.CompletedTask;
    }
}
