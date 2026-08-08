using Hackermes.AiPanel.Tools;
using Hackermes.App;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketAuditExportEntryPointTests
{
    [Fact]
    public async Task Cli_exports_to_path_and_verifies_through_same_service()
    {
        var service = new AuditExportFake();
        var path = Path.Combine(Path.GetTempPath(), $"hackermes-audit-{Guid.NewGuid():N}.json");
        try
        {
            var exported = await PacketCommandRegistrar.ExecuteAsync(service, Context("audit-export", path, "packet-1", "25"), CancellationToken.None);
            var verified = await PacketCommandRegistrar.ExecuteAsync(service, Context("audit-verify", path, "trusted-key"), CancellationToken.None);

            Assert.True(exported.Success);
            Assert.True(verified.Success);
            Assert.Equal("packet-1", service.Query?.PacketId);
            Assert.Equal(25, service.Query?.Limit);
            Assert.Equal("trusted-key", service.ExpectedKeyId);
            Assert.Equal("{\"signed\":true}", service.VerifiedContent);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Agent_tools_have_expected_risk_and_never_accept_paths()
    {
        var registry = new AiToolRegistry();
        var service = new AuditExportFake();
        TrafficAiToolRegistrar.Register(registry, service);

        var export = registry.All.Single(tool => tool.Name == "packet_audit_export");
        var verify = registry.All.Single(tool => tool.Name == "packet_audit_verify");
        Assert.Equal(AiToolRisk.Dangerous, export.Risk);
        Assert.Equal(AiToolRisk.ReadOnly, verify.Risk);
        Assert.DoesNotContain("path", export.InputSchema.GetRawText().ToLowerInvariant());
        Assert.DoesNotContain("path", verify.InputSchema.GetRawText().ToLowerInvariant());

        var exportResult = await export.Handler(new ToolInvocation(export.Name,
            JsonSerializer.SerializeToElement(new { packetId = "packet-2", limit = 10 })), CancellationToken.None);
        var verifyResult = await verify.Handler(new ToolInvocation(verify.Name,
            JsonSerializer.SerializeToElement(new { content = exportResult.Content, expectedKeyId = "trusted-key" })), CancellationToken.None);

        Assert.True(exportResult.Success);
        Assert.Equal("{\"signed\":true}", exportResult.Content);
        Assert.True(verifyResult.Success);
    }

    [Fact]
    public async Task Entry_points_reject_out_of_range_limits()
    {
        var service = new AuditExportFake();
        var cli = await PacketCommandRegistrar.ExecuteAsync(service,
            Context("audit-export", "unused.json", "*", "501"), CancellationToken.None);
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);
        var tool = registry.All.Single(item => item.Name == "packet_audit_export");
        var agent = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { limit = 501 })), CancellationToken.None);

        Assert.False(cli.Success);
        Assert.False(agent.Success);
        Assert.Null(service.Query);
    }

    private static CommandContext Context(params string[] args) => new()
    {
        Args = args,
        PageId = null,
        RawInput = "packet " + string.Join(' ', args),
        RawArguments = string.Join(' ', args)
    };

    private sealed class AuditExportFake : IPacketCommandService, IPacketAuditExportService
    {
        public PacketAuditQuery? Query { get; private set; }
        public string? VerifiedContent { get; private set; }
        public string? ExpectedKeyId { get; private set; }
        public string Export(PacketAuditQuery query) { Query = query; return "{\"signed\":true}"; }
        public PacketAuditVerification Verify(string content, string? expectedKeyId = null)
        {
            VerifiedContent = content;
            ExpectedKeyId = expectedKeyId;
            return new(true, "trusted-key", 1, DateTimeOffset.UtcNow);
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
