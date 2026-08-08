using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketCommitCommandTests
{
    [Theory]
    [InlineData("continue", "Continue")]
    [InlineData("drop", "Drop")]
    [InlineData("draft-discard", "Discard")]
    public async Task StructuredCommit_UsesStableKeyValueOutput(string command, string operation)
    {
        var service = new CommitService { Operation = operation };
        var args = command == "draft-discard" ? new[] { command, "packet:1", "response" } : [command, "packet:1"];
        var result = await Execute(service, args);

        Assert.True(result.Success);
        Assert.Equal(string.Join(System.Environment.NewLine,
            "success=true", $"operation={operation}", "id=packet:1", "side=request", "state=Continued", "auditId=audit-1",
            "before.length=3", $"before.sha256={new string('a', 64)}", "before.contentLength=3",
            "after.length=5", $"after.sha256={new string('b', 64)}", "after.contentLength=5", "error=-"), result.Output);
        Assert.False(service.LegacyCalled);
        if (command == "draft-discard") Assert.Equal("response", service.LastSide);
    }

    [Fact]
    public async Task StructuredEdit_PassesDecodedRawHttpToCommitService()
    {
        var service = new CommitService { Operation = "Edit" };
        var result = await Execute(service, "edit", "packet:1", "request", "POST", "/", "HTTP/1.1\\r\\nContent-Length:", "0\\r\\n\\r\\n");

        Assert.True(result.Success);
        Assert.Contains("operation=Edit", result.Output);
        Assert.Contains("\r\n\r\n", service.RawEdit);
        Assert.False(service.LegacyCalled);
    }

    [Fact]
    public async Task FailedStructuredCommit_ReturnsFailedResultAndNoMessageText()
    {
        var service = new CommitService { Operation = "Drop", Success = false, Error = "Network.Timeout", Message = "secret response body" };
        var result = await Execute(service, "drop", "packet:1");

        Assert.False(result.Success);
        Assert.Contains("success=false", result.Output);
        Assert.Contains("error=Network.Timeout", result.Output);
        Assert.DoesNotContain("secret response body", result.Output);
    }

    [Fact]
    public async Task LegacyService_KeepsOriginalMutationBehavior()
    {
        var service = new LegacyService();
        var result = await Execute(service, "continue", "packet:1");
        Assert.True(result.Success);
        Assert.Equal("Packet continued.", result.Output);
        Assert.True(service.LegacyCalled);
    }

    private static Task<CommandResult> Execute(IPacketCommandService service, params string[] args) =>
        PacketCommandRegistrar.ExecuteAsync(service, new CommandContext
        {
            Args = args, PageId = null, RawInput = "packet " + string.Join(' ', args), RawArguments = string.Join(' ', args)
        }, CancellationToken.None);

    private class LegacyService : IPacketCommandService
    {
        public bool LegacyCalled { get; protected set; }
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task ContinueAsync(string id, CancellationToken ct) { LegacyCalled = true; return Task.CompletedTask; }
        public Task DropAsync(string id, CancellationToken ct) { LegacyCalled = true; return Task.CompletedTask; }
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken ct) { LegacyCalled = true; return Task.CompletedTask; }
    }

    private sealed class CommitService : LegacyService, IPacketCommitService
    {
        public bool Success { get; init; } = true;
        public string Operation { get; init; } = "Continue";
        public string? Error { get; init; }
        public string Message { get; init; } = "ok";
        public string? RawEdit { get; private set; }
        public Task<PacketCommitResult> CommitContinueAsync(string id, CancellationToken ct) => Task.FromResult(Result(id));
        public Task<PacketCommitResult> CommitDropAsync(string id, CancellationToken ct) => Task.FromResult(Result(id));
        public string? LastSide { get; private set; }
        public Task<PacketCommitResult> CommitEditAsync(string id, string side, string rawPacket, CancellationToken ct) { LastSide = side; RawEdit = rawPacket; return Task.FromResult(Result(id)); }
        public Task<PacketCommitResult> CommitDiscardAsync(string id, string side, CancellationToken ct) { LastSide = side; return Task.FromResult(Result(id)); }
        private PacketCommitResult Result(string id) => new(Success, Operation, id, "request", "Continued",
            new PacketEditVersion(3, new string('a', 64), "3"), new PacketEditVersion(5, new string('b', 64), "5"),
            "audit-1", Error, Message);
    }
}
