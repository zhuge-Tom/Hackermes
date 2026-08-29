using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.App;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Agent tool-usage capability: the system prompt carries an explicit tool-use protocol and
/// the dispatcher steers the model away from repeating identical read-only calls.
/// </summary>
public sealed class AgentToolUseProtocolTests
{
    [Fact]
    public void System_prompt_carries_the_tool_use_protocol()
    {
        var compactor = new AgentContextCompactor();

        var request = compactor.BuildRequest(
            [new ChatMessage("user", "检查捕获的数据包。")],
            new AgentMemoryDocument(), [], new AiSettings { PermissionMode = AiPermissionMode.RequestApproval });

        var system = request.Single(message => message.Role == "system").Content ?? string.Empty;
        Assert.Contains("Tool use protocol", system);
        Assert.Contains("offset/limit", system);
        Assert.Contains("never repeat an unchanged call", system);
        Assert.Contains("may ask for confirmation", system);
        Assert.Contains("standalone final completion report", system);
    }

    [Fact]
    public async Task Duplicate_read_only_call_gets_corrective_hint()
    {
        var registry = new AiToolRegistry();
        registry.Register(new AiToolDefinition("probe", "read", EmptySchema(), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("data"))));
        var dispatcher = CreateDispatcher(registry);

        var invocation = new ToolInvocation("probe", JsonDocument.Parse("{\"id\":\"p-1\"}").RootElement.Clone(), null, "session-1");
        var first = await dispatcher.InvokeAsync(invocation, CancellationToken.None);
        var second = await dispatcher.InvokeAsync(invocation, CancellationToken.None);

        Assert.DoesNotContain("[提示]", first.Content);
        Assert.Contains("[提示]", second.Content);
        Assert.Contains("offset/limit", second.Content);
    }

    [Fact]
    public async Task Changed_arguments_or_mutating_tools_are_never_annotated()
    {
        var registry = new AiToolRegistry();
        registry.Register(new AiToolDefinition("probe", "read", EmptySchema(), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("data"))));
        registry.Register(new AiToolDefinition("mutate", "write", EmptySchema(), AiToolRisk.Mutating,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("done"))));
        var dispatcher = CreateDispatcher(registry);

        var first = new ToolInvocation("probe", JsonDocument.Parse("{\"id\":\"p-1\"}").RootElement.Clone(), null, "session-1");
        var changed = new ToolInvocation("probe", JsonDocument.Parse("{\"id\":\"p-2\"}").RootElement.Clone(), null, "session-1");
        var mutating = new ToolInvocation("mutate", JsonDocument.Parse("{\"id\":\"p-1\"}").RootElement.Clone(), null, "session-1");

        Assert.True((await dispatcher.InvokeAsync(first, CancellationToken.None)).Success);
        Assert.DoesNotContain("[提示]", (await dispatcher.InvokeAsync(changed, CancellationToken.None)).Content);
        Assert.DoesNotContain("[提示]", (await dispatcher.InvokeAsync(mutating, CancellationToken.None)).Content);
        // Mutating repeats stay unannotated even when identical.
        Assert.DoesNotContain("[提示]", (await dispatcher.InvokeAsync(mutating, CancellationToken.None)).Content);
    }

    [Fact]
    public async Task Hint_survives_result_truncation()
    {
        var registry = new AiToolRegistry();
        registry.Register(new AiToolDefinition("probe", "read", EmptySchema(), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(new string('x', 400)))));
        var dispatcher = CreateDispatcher(registry, maxToolResultCharacters: 256);

        var invocation = new ToolInvocation("probe", JsonDocument.Parse("{}").RootElement.Clone(), null, "session-1");
        _ = await dispatcher.InvokeAsync(invocation, CancellationToken.None);
        var second = await dispatcher.InvokeAsync(invocation, CancellationToken.None);

        Assert.Contains("已截断", second.Content);
        // The corrective hint must be appended after truncation so it is never cut away.
        Assert.True(second.Content.IndexOf("[提示]", StringComparison.Ordinal) > second.Content.IndexOf("已截断", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paging_tools_expose_parameter_descriptions()
    {
        var registry = new AiToolRegistry();
        TrafficAiToolRegistrar.Register(registry, new ArchiveService());
        var archive = registry.All.Single(tool => tool.Name == "packet_archive_export");
        var query = registry.All.Single(tool => tool.Name == "packet_query");
        var chunk = registry.All.Single(tool => tool.Name == "packet_body_chunk");

        Assert.Contains("Entry index of the first packet", archive.InputSchema.GetRawText());
        Assert.Contains("walk offset until you have collected total entries", archive.InputSchema.GetRawText());
        Assert.Contains("Page start index", query.InputSchema.GetRawText());
        Assert.Contains("walk offsets for large bodies", chunk.InputSchema.GetRawText());
        await Task.CompletedTask;
    }

    private static AiToolDispatcher CreateDispatcher(AiToolRegistry registry, int maxToolResultCharacters = AiToolDispatcher.DefaultMaxToolResultCharacters) =>
        new(registry, new DefaultToolPolicyGate(), new RejectingToolConfirmationService(),
            TimeProvider.System, AiToolDispatcher.DefaultSessionGrantLifetime,
            maxToolResultCharacters, AiToolDispatcher.DefaultToolCallTimeout);

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });

    private sealed class ArchiveService : IPacketCommandService, IPacketArchiveService, IPacketQueryService, IPacketBodyReadService
    {
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
            Task.FromResult<IReadOnlyList<PacketArchiveEntry>>([]);
        public Task<PacketArchivePage> ExportArchivePageAsync(PacketArchiveExchangeQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new PacketArchivePage([], 0));
        public Task<int> ImportArchiveAsync(IReadOnlyList<PacketArchiveEntry> entries, CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<PacketQueryPage> QueryPacketsAsync(PacketQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new PacketQueryPage([], 0, 0, query.Limit));
        public Task<PacketBodyDescriptor> DescribeBodyAsync(string id, string side, CancellationToken cancellationToken) =>
            Task.FromResult(new PacketBodyDescriptor(0, new string('0', 64), null, null));
        public Task<PacketBodyChunk> ReadBodyChunkAsync(
            string id, string side, long offset, int count, PacketBodyChunkEncoding encoding, CancellationToken cancellationToken) =>
            Task.FromResult(new PacketBodyChunk(offset, 0, 0, string.Empty, encoding));
    }
}
