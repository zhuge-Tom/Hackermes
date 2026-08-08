using Hackermes.AiPanel.Tools;
using Hackermes.App;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketParameterEntryPointTests
{
    [Fact]
    public async Task Agent_lists_headers_and_cookies_without_exposing_sensitive_values()
    {
        var service = new ParameterService();
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);
        var tool = registry.All.Single(item => item.Name == "packet_parameters");

        var result = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { id = "p-1", side = "request" })), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("header[0]\tAuthorization\t<redacted>", result.Content);
        Assert.Contains("cookie[0]\tsid\t<redacted>", result.Content);
        Assert.DoesNotContain("Bearer secret", result.Content);
        Assert.DoesNotContain("cookie-secret", result.Content);
    }

    [Fact]
    public async Task Agent_and_cli_share_header_and_cookie_mutation_contract()
    {
        var service = new ParameterService();
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);
        var agentTool = registry.All.Single(item => item.Name == "packet_parameter_set");

        Assert.Equal(AiToolRisk.Dangerous, agentTool.Risk);
        Assert.Contains("header", agentTool.InputSchema.GetRawText());
        Assert.Contains("cookie", agentTool.InputSchema.GetRawText());
        var agent = await agentTool.Handler(new ToolInvocation(agentTool.Name,
            JsonSerializer.SerializeToElement(new
            {
                id = "p-1", side = "request", location = "header",
                name = "X-Mode", occurrence = 0, value = "agent"
            })), CancellationToken.None);
        var cli = await PacketCommandRegistrar.ExecuteAsync(service,
            Context("param-set", "p-1", "request", "cookie", "sid", "0", "cli"), CancellationToken.None);

        Assert.True(agent.Success);
        Assert.True(cli.Success);
        Assert.Contains("X-Mode: agent", service.Edits[0]);
        Assert.Contains("Cookie: sid=cli; theme=dark", service.Edits[1]);
    }

    [Fact]
    public async Task Agent_reports_header_injection_as_safe_tool_failure()
    {
        var service = new ParameterService();
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);
        var tool = registry.All.Single(item => item.Name == "packet_parameter_set");

        var result = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new
            {
                id = "p-1", side = "request", location = "header",
                name = "X-Mode", occurrence = 0, value = "safe\r\nInjected: yes"
            })), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("must not contain line breaks", result.Content);
        Assert.Empty(service.Edits);
    }

    private static CommandContext Context(params string[] args) => new()
    {
        Args = args,
        PageId = null,
        RawInput = "packet " + string.Join(' ', args),
        RawArguments = string.Join(' ', args)
    };

    private sealed class ParameterService : IPacketCommandService
    {
        private const string Raw = "GET / HTTP/1.1\r\nAuthorization: Bearer secret\r\nX-Mode: old\r\nCookie: sid=cookie-secret; theme=dark\r\n\r\n";
        public List<string> Edits { get; } = [];
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken) => Task.FromResult<string?>(Raw);
        public Task ReplayAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken)
        {
            Edits.Add(rawPacket);
            return Task.CompletedTask;
        }
    }
}
