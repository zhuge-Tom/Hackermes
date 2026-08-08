using Hackermes.App;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketInterceptionModeTests
{
    [Fact]
    public void IntegrationModule_RegistersIndependentInterceptionModeService()
    {
        var services = new ServiceCollection();

        new TrafficIntegrationModule().RegisterServices(services);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPacketInterceptionModeService));
    }

    [Theory]
    [InlineData("request", PacketInterceptionMode.Request)]
    [InlineData("response", PacketInterceptionMode.Response)]
    [InlineData("both", PacketInterceptionMode.Both)]
    [InlineData("off", PacketInterceptionMode.Off)]
    public async Task InterceptMode_MapsAllIndependentModes(string argument, PacketInterceptionMode expected)
    {
        var service = new ModeService();
        var result = await Execute(service, "intercept-mode", argument);

        Assert.True(result.Success);
        Assert.Equal(expected, service.InterceptionMode);
        Assert.Equal(1, service.ModeChanges);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task LegacyIntercept_RemainsRequestBoolean(string argument, bool expected)
    {
        var service = new ModeService();
        var result = await Execute(service, "intercept", argument);

        Assert.True(result.Success);
        Assert.Equal(expected, service.LegacyRequestEnabled);
        Assert.Equal(0, service.ModeChanges);
    }

    [Fact]
    public async Task InterceptMode_ReportsUnsupportedBackend()
    {
        var result = await Execute(new LegacyService(), "intercept-mode", "response");
        Assert.False(result.Success);
        Assert.Contains("does not support", result.Output);
    }

    private static Task<CommandResult> Execute(IPacketCommandService service, params string[] args) =>
        PacketCommandRegistrar.ExecuteAsync(service, new CommandContext
        {
            Args = args, PageId = null, RawArguments = string.Join(' ', args), RawInput = "packet " + string.Join(' ', args)
        }, CancellationToken.None);

    private class LegacyService : IPacketCommandService
    {
        public bool LegacyRequestEnabled { get; private set; }
        public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<PacketSummary>>([]);
        public Task<string?> GetRawAsync(string id, string side, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task ReplayAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task SetInterceptionAsync(bool enabled, CancellationToken ct) { LegacyRequestEnabled = enabled; return Task.CompletedTask; }
        public Task ContinueAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task DropAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task EditAsync(string id, string side, string rawPacket, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ModeService : LegacyService, IPacketInterceptionModeService
    {
        public PacketInterceptionMode InterceptionMode { get; private set; }
        public int ModeChanges { get; private set; }
        public Task SetInterceptionModeAsync(PacketInterceptionMode mode, CancellationToken ct)
        {
            InterceptionMode = mode;
            ModeChanges++;
            return Task.CompletedTask;
        }
    }
}
