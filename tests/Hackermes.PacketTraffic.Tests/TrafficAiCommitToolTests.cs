using Hackermes.AiPanel.Tools;
using Hackermes.App;
using Hackermes.Automation.Packet;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficAiCommitToolTests
{
    [Fact]
    public async Task Structured_commit_tools_preserve_risk_and_return_camel_case_json()
    {
        var registry = new AiToolRegistry();
        var service = new CommitService();
        TrafficAiToolRegistrar.Register(registry, service);

        Assert.Equal(AiToolRisk.Mutating, Tool(registry, "packet_continue").Risk);
        Assert.Equal(AiToolRisk.Dangerous, Tool(registry, "packet_drop").Risk);
        Assert.Equal(AiToolRisk.Dangerous, Tool(registry, "packet_edit").Risk);
        Assert.Equal(AiToolRisk.Mutating, Tool(registry, "packet_edit_discard").Risk);

        var result = await Invoke(registry, "packet_edit", new { id = "p-1", side = "response", rawHttp = "HTTP/1.1 200 OK\r\n\r\nchanged" });

        Assert.True(result.Success);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("edit", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal("p-1", json.RootElement.GetProperty("packetId").GetString());
        Assert.False(json.RootElement.TryGetProperty("PacketId", out _));
        Assert.Equal(("p-1", "response", "HTTP/1.1 200 OK\r\n\r\nchanged"), service.EditCall);
    }

    [Fact]
    public async Task Structured_failure_is_failed_tool_result_with_complete_json_content()
    {
        var registry = new AiToolRegistry();
        var service = new CommitService { FailDrop = true };
        TrafficAiToolRegistrar.Register(registry, service);

        var result = await Invoke(registry, "packet_drop", new { id = "p-2" });

        Assert.False(result.Success);
        using var json = JsonDocument.Parse(result.Content);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("held_state_required", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("not held", json.RootElement.GetProperty("message").GetString());
        Assert.Equal("audit-1", json.RootElement.GetProperty("auditId").GetString());
        Assert.True(json.RootElement.TryGetProperty("before", out _));
        Assert.True(json.RootElement.TryGetProperty("after", out _));
    }

    [Fact]
    public async Task Legacy_backend_keeps_command_adapter_path()
    {
        var registry = new AiToolRegistry();
        var service = new LegacyService();
        TrafficAiToolRegistrar.Register(registry, service);

        var result = await Invoke(registry, "packet_continue", new { id = "legacy-1" });

        Assert.True(result.Success);
        Assert.Equal("Packet continued.", result.Content);
        Assert.Equal("legacy-1", service.ContinuedId);
        Assert.DoesNotContain(registry.All, tool => tool.Name == "packet_edit_discard");
    }

    private static AiToolDefinition Tool(AiToolRegistry registry, string name) =>
        registry.All.Single(tool => tool.Name == name);

    private static ValueTask<ToolResult> Invoke(AiToolRegistry registry, string name, object arguments)
    {
        var tool = Tool(registry, name);
        return tool.Handler(new ToolInvocation(name, JsonSerializer.SerializeToElement(arguments)), CancellationToken.None);
    }

    private sealed class CommitService : IPacketCommandService, IPacketCommitService
    {
        private static readonly PacketEditVersion Before = new(10, "before", "10");
        private static readonly PacketEditVersion After = new(12, "after", "12");

        public bool FailDrop { get; init; }
        public (string Id, string Side, string Raw)? EditCall { get; private set; }

        public Task<PacketCommitResult> CommitContinueAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Result(true, "continue", id, "request"));

        public Task<PacketCommitResult> CommitDropAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(FailDrop
                ? new PacketCommitResult(false, "drop", id, "request", "held", Before, After, "audit-1", "held_state_required", "not held")
                : Result(true, "drop", id, "request"));

        public Task<PacketCommitResult> CommitEditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken)
        {
            EditCall = (id, side, rawPacket);
            return Task.FromResult(Result(true, "edit", id, side));
        }

        public Task<PacketCommitResult> CommitDiscardAsync(string id, string side, CancellationToken cancellationToken) =>
            Task.FromResult(Result(true, "discard", id, side));

        private static PacketCommitResult Result(bool success, string operation, string id, string side) =>
            new(success, operation, id, side, "continued", Before, After, "audit-1", null, "done");

        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Legacy path used.");
        public Task DropAsync(string id, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Legacy path used.");
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Legacy path used.");
    }

    private sealed class LegacyService : IPacketCommandService
    {
        public string? ContinuedId { get; private set; }
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken cancellationToken)
        {
            ContinuedId = id;
            return Task.CompletedTask;
        }
        public Task DropAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
