using Hackermes.AiPanel.Tools;
using Hackermes.App;
using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>Bounded archive exchange must page large stores instead of failing outright.</summary>
public sealed class PacketArchivePagingTests
{
    private static readonly PacketArchiveEntry[] Entries =
        Enumerable.Range(1, 5).Select(index => new PacketArchiveEntry(
            $"packet-{index}", DateTimeOffset.UnixEpoch.AddMinutes(index),
            $"GET /{index} HTTP/1.1\r\nHost: example.test\r\n\r\n")).ToArray();

    [Fact]
    public void Page_slices_middle_batch_and_reports_total()
    {
        var page = PacketArchiveContent.Page(Entries, new PacketArchiveExchangeQuery(null, 1, 2));

        Assert.Equal(5, page.Total);
        Assert.Equal(["packet-2", "packet-3"], page.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void Page_clamps_final_short_batch()
    {
        var page = PacketArchiveContent.Page(Entries, new PacketArchiveExchangeQuery(null, 3, 10));

        Assert.Equal(5, page.Total);
        Assert.Equal(["packet-4", "packet-5"], page.Entries.Select(entry => entry.Id));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void Page_at_or_past_the_end_yields_empty_batch_with_unchanged_total(int offset)
    {
        var page = PacketArchiveContent.Page(Entries, new PacketArchiveExchangeQuery(null, offset, 4));

        Assert.Empty(page.Entries);
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public void Page_rejects_out_of_bounds_offset_and_limit()
    {
        Assert.Throws<ArgumentException>(() =>
            PacketArchiveContent.Page(Entries, new PacketArchiveExchangeQuery(null, -1, 5)));
        Assert.Throws<ArgumentException>(() =>
            PacketArchiveContent.Page(Entries, new PacketArchiveExchangeQuery(null, 0, 0)));
        Assert.Throws<ArgumentException>(() =>
            PacketArchiveContent.Page(Entries, new PacketArchiveExchangeQuery(null, 0, PacketArchiveContent.MaximumEntries + 1)));
    }

    [Fact]
    public async Task Agent_export_pages_bounded_batches_with_total_offset_envelope()
    {
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, new ArchiveService(Entries));
        var tool = registry.All.Single(item => item.Name == "packet_archive_export");

        Assert.Equal(AiToolRisk.Dangerous, tool.Risk);
        Assert.Contains("offset", tool.InputSchema.GetRawText());
        Assert.Contains("limit", tool.InputSchema.GetRawText());

        var result = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { format = "hackermesJson", offset = 2, limit = 2 })),
            CancellationToken.None);

        Assert.True(result.Success);
        using var envelope = JsonDocument.Parse(result.Content);
        Assert.Equal(5, envelope.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, envelope.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, envelope.RootElement.GetProperty("offset").GetInt32());
        var batch = PacketArchiveCodec.Deserialize(
            envelope.RootElement.GetProperty("content").GetString()!, PacketArchiveFormat.HackermesJson);
        Assert.Equal(["packet-3", "packet-4"], batch.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Agent_export_defaults_to_first_full_batch()
    {
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, new ArchiveService(Entries));
        var tool = registry.All.Single(item => item.Name == "packet_archive_export");

        var result = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { format = "hackermesJson" })), CancellationToken.None);

        Assert.True(result.Success);
        using var envelope = JsonDocument.Parse(result.Content);
        Assert.Equal(5, envelope.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(5, envelope.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, envelope.RootElement.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task Agent_export_rejects_bad_paging_arguments_safely()
    {
        var service = new ArchiveService(Entries);
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, service);
        var tool = registry.All.Single(item => item.Name == "packet_archive_export");

        var badLimit = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { format = "hackermesJson", limit = 0 })), CancellationToken.None);
        var badOffset = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { format = "hackermesJson", offset = -3 })), CancellationToken.None);

        Assert.False(badLimit.Success);
        Assert.False(badOffset.Success);
        Assert.Empty(service.ImportedIds);
    }

    [Fact]
    public async Task Agent_oversized_batch_fails_with_retry_guidance()
    {
        var oversized = new[]
        {
            new PacketArchiveEntry("huge", DateTimeOffset.UnixEpoch,
                "POST /upload HTTP/1.1\r\nHost: example.test\r\n\r\n",
                RequestBody: PacketBody.FromText(new string('x', PacketArchiveContent.MaximumUtf8Bytes + 1)))
        };
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, new ArchiveService(oversized));
        var tool = registry.All.Single(item => item.Name == "packet_archive_export");

        var result = await tool.Handler(new ToolInvocation(tool.Name,
            JsonSerializer.SerializeToElement(new { format = "hackermesJson" })), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("smaller limit", result.Content);
    }

    private sealed class ArchiveService(PacketArchiveEntry[] entries) : IPacketCommandService, IPacketArchiveService
    {
        public List<string> ImportedIds { get; } = [];
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<PacketArchiveEntry>> ExportArchiveAsync(string? filter, CancellationToken cancellationToken) =>
            Task.FromResult(PacketArchiveContent.Page(entries, new PacketArchiveExchangeQuery(filter, 0, PacketArchiveContent.MaximumEntries)).Entries);
        public Task<PacketArchivePage> ExportArchivePageAsync(PacketArchiveExchangeQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(PacketArchiveContent.Page(entries, query));
        public Task<int> ImportArchiveAsync(IReadOnlyList<PacketArchiveEntry> import, CancellationToken cancellationToken)
        {
            ImportedIds.AddRange(import.Select(entry => entry.Id));
            return Task.FromResult(import.Count);
        }
    }
}
