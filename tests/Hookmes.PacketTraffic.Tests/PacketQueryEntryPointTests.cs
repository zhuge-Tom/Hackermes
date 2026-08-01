using Hookmes.AiPanel.Tools;
using Hookmes.App;
using Hookmes.Automation.Commands;
using Hookmes.Automation.Packet;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class PacketQueryEntryPointTests
{
    [Fact]
    public async Task Cli_forwards_compound_filters_and_prints_page_metadata()
    {
        var service = new QueryService();
        var result = await PacketCommandRegistrar.ExecuteAsync(service,
            Context("query", "login", "POST", "401", "XHR", "held", "20", "25"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new PacketQuery("login", "POST", 401, "XHR", true, 20, 25), service.LastQuery);
        Assert.StartsWith("total=42\toffset=20\tlimit=25", result.Output);
        Assert.Contains("packet-1\tPOST\t401\theld\thttps://example.test/login", result.Output);
    }

    [Fact]
    public async Task Agent_query_is_read_only_bounded_and_returns_camel_case_page()
    {
        var registry = new AiToolRegistry();
        var service = new QueryService();
        TrafficAiToolRegistrar.Register(registry, service);
        var tool = registry.All.Single(item => item.Name == "packet_query");

        Assert.Equal(AiToolRisk.ReadOnly, tool.Risk);
        Assert.Contains("onlyIntercepted", tool.InputSchema.GetRawText());
        Assert.Contains($"\"maximum\":{PacketQueryLimits.MaximumPageSize}", tool.InputSchema.GetRawText());
        var result = await tool.Handler(new ToolInvocation(tool.Name, JsonSerializer.SerializeToElement(new
        {
            text = "login", method = "POST", statusCode = 401, resourceType = "XHR",
            onlyIntercepted = true, offset = 20, limit = 25
        })), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new PacketQuery("login", "POST", 401, "XHR", true, 20, 25), service.LastQuery);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(42, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("packet-1", json.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.False(json.RootElement.TryGetProperty("Items", out _));
    }

    [Fact]
    public async Task Cli_rejects_unbounded_query_before_calling_backend()
    {
        var service = new QueryService();
        var result = await PacketCommandRegistrar.ExecuteAsync(service,
            Context("query", "*", "*", "*", "*", "all", "0", "501"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(service.LastQuery);
    }

    private static CommandContext Context(params string[] args) => new()
    {
        Args = args,
        PageId = null,
        RawInput = "packet " + string.Join(' ', args),
        RawArguments = string.Join(' ', args)
    };

    private sealed class QueryService : IPacketCommandService, IPacketQueryService
    {
        public PacketQuery? LastQuery { get; private set; }

        public Task<PacketQueryPage> QueryPacketsAsync(PacketQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            IReadOnlyList<PacketSummary> items =
                [new("packet-1", "POST", "https://example.test/login", 401, true)];
            return Task.FromResult(new PacketQueryPage(items, 42, query.Offset, query.Limit));
        }

        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
