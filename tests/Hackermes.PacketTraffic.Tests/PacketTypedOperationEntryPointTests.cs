using Hackermes.AiPanel.Tools;
using Hackermes.App;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketTypedOperationEntryPointTests
{
    [Fact]
    public async Task Agent_preserves_json_argument_boundaries_for_show_audit_and_parameter_values()
    {
        var service = new TypedService();
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);

        var list = await InvokeAsync(registry, "packet_list", new { filter = "errors with \"quotes\"" });
        var show = await InvokeAsync(registry, "packet_show", new { id = "packet with spaces", side = "request" });
        var diff = await InvokeAsync(registry, "packet_diff", new
        {
            leftId = "left packet with spaces", rightId = "right \"quoted\" packet", side = "request"
        });
        var audit = await InvokeAsync(registry, "packet_audit", new { packetId = "packet with spaces", limit = 25 });
        var parameter = await InvokeAsync(registry, "packet_parameter_set", new
        {
            id = "packet with spaces",
            side = "request",
            location = "header",
            name = "X-Note",
            occurrence = 0,
            value = "two  spaces and \"quoted text\""
        });
        var replay = await InvokeAsync(registry, "packet_replay", new { id = "packet with spaces" });

        Assert.True(list.Success, list.Content);
        Assert.True(show.Success, show.Content);
        Assert.True(diff.Success, diff.Content);
        Assert.True(audit.Success, audit.Content);
        Assert.True(parameter.Success, parameter.Content);
        Assert.True(replay.Success, replay.Content);
        Assert.Equal("errors with \"quotes\"", service.LastListFilter);
        Assert.Contains(service.RawLookups, lookup => lookup.Id == "left packet with spaces");
        Assert.Contains(service.RawLookups, lookup => lookup.Id == "right \"quoted\" packet");
        Assert.Equal("packet with spaces", service.LastAuditQuery?.PacketId);
        Assert.Contains("X-Note: two  spaces and \"quoted text\"", service.LastEdit);
        Assert.Equal("packet with spaces", service.LastReplayId);
    }

    [Fact]
    public async Task Cli_parser_and_agent_adapter_preserve_the_same_parameter_value()
    {
        const string expected = "two  spaces and \"quoted text\"";
        var agentService = new TypedService();
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, agentService);
        var agent = await InvokeAsync(registry, "packet_parameter_set", new
        {
            id = "packet with spaces", side = "request", location = "header",
            name = "X-Note", occurrence = 0, value = expected
        });

        var cliService = new TypedService();
        var raw = "packet param-set \"packet with spaces\" request header X-Note 0 \"two  spaces and \\\"quoted text\\\"\"";
        var tokens = CommandLineParser.Tokenize(raw);
        var cli = await PacketCommandRegistrar.ExecuteAsync(cliService, new CommandContext
        {
            Args = tokens.Skip(1).ToArray(), PageId = null, RawInput = raw,
            RawArguments = raw[(raw.IndexOf(' ') + 1)..]
        }, CancellationToken.None);

        Assert.True(agent.Success, agent.Content);
        Assert.True(cli.Success, cli.Output);
        Assert.Contains($"X-Note: {expected}", agentService.LastEdit);
        Assert.Equal(agentService.LastEdit, cliService.LastEdit);
    }

    [Fact]
    public async Task Cli_and_agent_return_the_same_query_bounds_error_without_calling_backend()
    {
        var service = new TypedService();
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);
        var agent = await InvokeAsync(registry, "packet_query", new { offset = 0, limit = 501 });
        var cli = await PacketCommandRegistrar.ExecuteAsync(service, Context(
            "query", "*", "*", "*", "*", "all", "0", "501"), CancellationToken.None);

        Assert.False(agent.Success);
        Assert.False(cli.Success);
        Assert.Equal(cli.Output, agent.Content);
        Assert.Null(service.LastQuery);
    }

    private static async Task<ToolResult> InvokeAsync(AiToolRegistry registry, string name, object arguments)
    {
        var tool = registry.All.Single(item => item.Name == name);
        return await tool.Handler(new ToolInvocation(name, JsonSerializer.SerializeToElement(arguments)), CancellationToken.None);
    }

    private static CommandContext Context(params string[] args) => new()
    {
        Args = args, PageId = null, RawInput = "packet " + string.Join(' ', args),
        RawArguments = string.Join(' ', args)
    };

    private sealed class TypedService : IPacketCommandService, IPacketAuditQueryService, IPacketQueryService
    {
        private const string Raw = "GET / HTTP/1.1\r\nX-Note: old\r\nAuthorization: Bearer secret\r\n\r\n";

        public List<(string Id, string Side)> RawLookups { get; } = [];
        public PacketAuditQuery? LastAuditQuery { get; private set; }
        public PacketQuery? LastQuery { get; private set; }
        public string? LastListFilter { get; private set; }
        public string? LastReplayId { get; private set; }
        public string LastEdit { get; private set; } = string.Empty;

        public IReadOnlyList<PacketAuditEntry> QueryAudit(PacketAuditQuery query)
        {
            LastAuditQuery = query;
            return [];
        }

        public Task<PacketQueryPage> QueryPacketsAsync(PacketQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new PacketQueryPage([], 0, query.Offset, query.Limit));
        }

        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken)
        {
            LastListFilter = filter;
            return Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        }

        public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken)
        {
            RawLookups.Add((id, side));
            return Task.FromResult<string?>(id is "packet with spaces" or "left packet with spaces" or "right \"quoted\" packet" ? Raw : null);
        }

        public Task ReplayAsync(string id, CancellationToken cancellationToken)
        {
            LastReplayId = id;
            return Task.CompletedTask;
        }
        public Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken)
        {
            LastEdit = rawPacket;
            return Task.CompletedTask;
        }
    }
}
