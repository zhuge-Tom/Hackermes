using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp.Session;
using Hackermes.Inspector.Models;
using Hackermes.Inspector.Services;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Deterministic capacity contracts for large inspection snapshots. These tests deliberately
/// avoid wall-clock assertions so they remain repeatable on developer and CI machines.
/// </summary>
public sealed class PerformanceCapacityContractTests
{
    private const int LargeInputCount = 10_000;
    private const int MaximumInspectionResults = 1_000;
    private const int MaximumNetworkToolPayloadBytes = 256 * 1024;

    [Fact]
    public void Network_query_over_ten_thousand_entries_returns_a_bounded_snapshot()
    {
        var store = new NetworkStore(new EmptySessionRegistry(), new EventBus(), new NullLogger());
        for (var index = 0; index < LargeInputCount; index++)
        {
            store.Entries.Add(new NetworkEntry
            {
                PageId = "capacity-page",
                RequestId = $"request-{index:D5}",
                Method = "GET",
                Url = $"https://capacity.invalid/items/{index:D5}",
                Status = 200,
                StatusText = "200 OK"
            });
        }

        var snapshot = store.Read(LargeInputCount, pageId: "capacity-page");

        Assert.Equal(MaximumInspectionResults, snapshot.Count);
        Assert.Equal("request-00000", snapshot[0].RequestId);
        Assert.Equal("request-00999", snapshot[^1].RequestId);
    }

    [Fact]
    public async Task Ai_network_query_clamps_ten_thousand_request_and_bounds_serialized_payload()
    {
        var network = new CapacityNetworkQuery();
        var registry = new AiToolRegistry();
        new InspectionToolAdapter(new EmptyConsoleQuery(), network).RegisterAll(registry);
        var tool = registry.All.Single(candidate => candidate.Name == "network_list");
        var arguments = JsonSerializer.SerializeToElement(new { last = LargeInputCount });

        var result = await tool.Handler(
            new ToolInvocation(tool.Name, arguments, "capacity-page"), default);

        Assert.True(result.Success);
        Assert.Equal(MaximumInspectionResults, network.RequestedCount);
        Assert.Equal("capacity-page", network.PageId);

        using var document = JsonDocument.Parse(result.Content);
        Assert.Equal(MaximumInspectionResults, document.RootElement.GetArrayLength());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Content), 1, MaximumNetworkToolPayloadBytes);
    }

    private sealed class CapacityNetworkQuery : INetworkQueryService
    {
        public int RequestedCount { get; private set; }
        public string? PageId { get; private set; }

        public IReadOnlyList<NetworkObservation> Read(
            int last = 100,
            bool failuresOnly = false,
            string? pageId = null)
        {
            RequestedCount = last;
            PageId = pageId;
            return Enumerable.Range(0, last)
                .Select(index => new NetworkObservation(
                    $"request-{index:D4}",
                    "GET",
                    $"https://capacity.invalid/items/{index:D4}",
                    200,
                    "200 OK",
                    false,
                    12.5,
                    "fetch",
                    null))
                .ToArray();
        }
    }

    private sealed class EmptyConsoleQuery : IConsoleQueryService
    {
        public IReadOnlyList<ConsoleObservation> Read(
            int last = 100,
            string? level = null,
            string? pageId = null) => [];
    }

    private sealed class EmptySessionRegistry : ICdpSessionRegistry
    {
        public ICdpSession? Get(string pageId) => null;
        public IReadOnlyList<ICdpSession> All => [];
        public IDisposable Register(ICdpSession session) => throw new NotSupportedException();
        public event Action<ICdpSession>? SessionOpened;
        public event Action<string>? SessionClosed;
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
