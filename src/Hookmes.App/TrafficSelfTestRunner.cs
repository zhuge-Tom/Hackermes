using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Hookmes.Automation.Packet;
using Hookmes.Base.Diagnostics;
using Hookmes.Cdp;
using Hookmes.Cdp.Session;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

/// <summary>
/// Opt-in desktop acceptance runner. It deliberately drives the same public traffic contracts as
/// CLI/UI clients while using a real WebView2 CDP session; no UI automation or timing-sensitive clicks.
/// </summary>
internal static class TrafficSelfTestRunner
{
    private static readonly ConcurrentDictionary<string, byte> StartedPages = new(StringComparer.Ordinal);

    public static void TryStart(IServiceProvider services)
    {
        if (Environment.GetEnvironmentVariable("HOOKMES_SELFTEST") != "1") return;

        var sessions = services.GetRequiredService<ICdpSessionRegistry>();
        foreach (var session in sessions.All) Start(session);
        sessions.SessionOpened += Start;

        void Start(ICdpSession session)
        {
            if (!StartedPages.TryAdd(session.PageId, 0)) return;
            _ = RunAsync(services, session);
        }
    }

    private static async Task RunAsync(IServiceProvider services, ICdpSession session)
    {
        var log = services.GetRequiredService<IAppLogger>().ForCategory("TrafficSelfTest");
        var store = services.GetRequiredService<ITrafficStore>();
        var traffic = services.GetRequiredService<ITrafficService>();
        var packets = services.GetRequiredService<IPacketCommandService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var passed = 0;

        try
        {
            var captured = await WaitForAsync(store, session.PageId,
                item => item.Url.Contains("/api/capture", StringComparison.Ordinal) && item.ResponseStatus is not null,
                timeout.Token).ConfigureAwait(false);
            Require(captured.ResponseStatus == 200, $"capture status was {captured.ResponseStatus}");
            Require(captured.RequestBody is not null &&
                System.Text.Encoding.UTF8.GetString(captured.RequestBody) == "capture-body", "capture body mismatch");
            passed++;
            log.Info($"TRAFFIC_SELFTEST PASS capture id={captured.Id} status={captured.ResponseStatus}");

            var replay = await traffic.ReplayAsync(captured.Id, cancellationToken: timeout.Token).ConfigureAwait(false);
            Require(replay.Status == 200, $"replay status was {replay.Status}");
            Require(System.Text.Encoding.UTF8.GetString(replay.Body).Contains("capture-body", StringComparison.Ordinal),
                "replay response did not contain request body");
            passed++;
            log.Info($"TRAFFIC_SELFTEST PASS replay status={replay.Status} bytes={replay.Body.Length}");

            await packets.SetInterceptionAsync(true, timeout.Token).ConfigureAwait(false);
            var nonce = Guid.NewGuid().ToString("N");
            var expression = $"void fetch('/api/intercept?nonce={nonce}',{{method:'POST',body:'held-body'}})";
            await session.SendAsync("Runtime.evaluate", CdpJson.Params(("expression", expression)), timeout.Token)
                .ConfigureAwait(false);
            var held = await WaitForAsync(store, session.PageId,
                item => item.Url.Contains(nonce, StringComparison.Ordinal) && item.State == TrafficState.Paused,
                timeout.Token).ConfigureAwait(false);
            await packets.ContinueAsync(held.Id, timeout.Token).ConfigureAwait(false);
            var released = await WaitForAsync(store, session.PageId,
                item => item.Id == held.Id && item.ResponseStatus == 200 && item.State != TrafficState.Paused,
                timeout.Token).ConfigureAwait(false);
            Require(released.ResponseBody is not null, "intercept response body was not captured");
            passed++;
            log.Info($"TRAFFIC_SELFTEST PASS intercept id={held.Id} status={released.ResponseStatus}");

            log.Info($"TRAFFIC_SELFTEST RESULT {passed}/3 PASS");
            ExitIfRequested(0);
        }
        catch (Exception error)
        {
            log.Error($"TRAFFIC_SELFTEST RESULT {passed}/3 FAIL: {error.Message}", error);
            ExitIfRequested(2);
        }
        finally
        {
            try { await packets.SetInterceptionAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<TrafficMessage> WaitForAsync(
        ITrafficStore store, string pageId, Func<TrafficMessage, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = store.Read(5000, pageId).FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExitIfRequested(int exitCode)
    {
        if (Environment.GetEnvironmentVariable("HOOKMES_SELFTEST_EXIT") != "1") return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(exitCode);
        });
    }
}
