using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser;
using Hackermes.Browser.Services;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Locks down the lifecycle gate used by browser auto-open. This is intentionally
/// one test because layout readiness is a process-wide, one-way startup boundary.
/// </summary>
[Collection("App data environment serial")]
public sealed class StartupPerformanceTests
{
    [Fact]
    public async Task Browser_auto_open_waits_for_layout_and_opens_exactly_once()
    {
        const string autoOpenVariable = "HACKERMES_AUTOOPEN_URL";
        const string target = "https://auto-open.invalid/probe";
        var previous = Environment.GetEnvironmentVariable(autoOpenVariable);

        try
        {
            StartupPerformance.ResetForTests((action, _) => action());
            Environment.SetEnvironmentVariable(autoOpenVariable, target);
            Assert.False(StartupPerformance.IsLayoutReady);

            var tabs = new RecordingBrowserTabManager();
            var services = new ServiceCollection()
                .AddSingleton<IEventBus>(new EventBus())
                .AddSingleton<IAppLogger>(new NullLogger())
                .AddSingleton<IBrowserTabManager>(tabs)
                .BuildServiceProvider();

            new BrowserModule().Initialize(services);

            Assert.Equal(0, Volatile.Read(ref tabs.OpenCount));

            StartupPerformance.MarkLayoutReady();
            StartupPerformance.MarkLayoutReady();

            await WaitUntilAsync(() => Volatile.Read(ref tabs.OpenCount) == 1);
            Assert.Equal(1, Volatile.Read(ref tabs.OpenCount));
            Assert.Equal(target, tabs.LastUrl);

            var afterReady = 0;
            StartupPerformance.RunWhenLayoutReady(() => Interlocked.Increment(ref afterReady));
            await WaitUntilAsync(() => Volatile.Read(ref afterReady) == 1);
            Assert.Equal(1, Volatile.Read(ref afterReady));
        }
        finally
        {
            Environment.SetEnvironmentVariable(autoOpenVariable, previous);
            StartupPerformance.ResetForTests();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordingBrowserTabManager : IBrowserTabManager
    {
        public int OpenCount;
        public string? LastUrl { get; private set; }
        public IReadOnlyList<string> OpenPageIds => [];

        public string OpenTab(string? url = null)
        {
            LastUrl = url;
            Interlocked.Increment(ref OpenCount);
            return "page-auto-open";
        }
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
