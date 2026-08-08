using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Hackermes.Automation.Packet;
using Hackermes.Base.Diagnostics;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>
/// Opt-in desktop acceptance runner. It deliberately drives the same public traffic contracts as
/// CLI/UI clients while using a real WebView2 CDP session; no UI automation or timing-sensitive clicks.
/// </summary>
internal static class TrafficSelfTestRunner
{
    private static readonly ConcurrentDictionary<string, byte> StartedPages = new(StringComparer.Ordinal);

    public static void TryStart(IServiceProvider services)
    {
        if (Environment.GetEnvironmentVariable("HACKERMES_SELFTEST") != "1") return;

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
        var modes = services.GetRequiredService<IPacketInterceptionModeService>();
        var bodyEdits = services.GetRequiredService<IPacketBodyEditService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var passed = 0;

        try
        {
            var captured = await WaitForAsync(store, session.PageId,
                item => item.Url.Contains("/api/capture", StringComparison.Ordinal) && item.ResponseStatus is not null,
                timeout.Token).ConfigureAwait(false);
            Require(captured.ResponseStatus == 200, $"capture status was {captured.ResponseStatus}");
            Require(captured.RequestBody is not null && Encoding.UTF8.GetString(captured.RequestBody) == "capture-body",
                "capture body mismatch");
            passed++;
            log.Info($"TRAFFIC_SELFTEST PASS capture id={captured.Id} status={captured.ResponseStatus}");

            var replay = await traffic.ReplayAsync(captured.Id, cancellationToken: timeout.Token).ConfigureAwait(false);
            Require(replay.Status == 200, $"replay status was {replay.Status}");
            Require(Encoding.UTF8.GetString(replay.Body).Contains("capture-body", StringComparison.Ordinal),
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

            await modes.SetInterceptionModeAsync(PacketInterceptionMode.Request, timeout.Token).ConfigureAwait(false);
            var requestNonce = Guid.NewGuid().ToString("N");
            var requestBytes = new byte[] { 0x00, 0x41, 0xFF, 0x7F, 0x0A };
            var requestExpression = $"void fetch('/api/request-edit?nonce={requestNonce}',{{method:'POST',body:'original-body'}})";
            await session.SendAsync("Runtime.evaluate", CdpJson.Params(("expression", requestExpression)), timeout.Token)
                .ConfigureAwait(false);
            var requestHeld = await WaitForAsync(store, session.PageId,
                item => item.Url.Contains(requestNonce, StringComparison.Ordinal) && item.Stage == TrafficStage.Request &&
                        item.State == TrafficState.Paused,
                timeout.Token).ConfigureAwait(false);
            await bodyEdits.EditBodyAsync(requestHeld.Id, "request",
                new BinaryBodyEdit(BinaryEditKind.Replace, 0, requestHeld.RequestBody?.LongLength ?? 0,
                    Convert.ToBase64String(requestBytes), BinaryTextEncoding.Base64), timeout.Token).ConfigureAwait(false);
            await packets.ContinueAsync(requestHeld.Id, timeout.Token).ConfigureAwait(false);
            var requestReleased = await WaitForAsync(store, session.PageId,
                item => item.Id == requestHeld.Id && item.Stage == TrafficStage.Response && item.ResponseStatus == 200,
                timeout.Token).ConfigureAwait(false);
            Require(ReadEchoBodyBase64(requestReleased.ResponseBody) == Convert.ToBase64String(requestBytes),
                "request edit bytes received by loopback server did not match");
            passed++;
            log.Info($"TRAFFIC_SELFTEST PASS request-edit id={requestHeld.Id} bytes={requestBytes.Length}");

            await modes.SetInterceptionModeAsync(PacketInterceptionMode.Response, timeout.Token).ConfigureAwait(false);
            var responseNonce = Guid.NewGuid().ToString("N");
            var resultSlot = "__hackermes_response_" + responseNonce;
            var responseBytes = new byte[] { 0x00, 0x52, 0xFF, 0x0A, 0x7F };
            var responseExpression = $"globalThis['{resultSlot}']=null;void fetch('/api/response-edit?nonce={responseNonce}').then(r=>r.arrayBuffer()).then(b=>{{const a=new Uint8Array(b);let s='';for(let i=0;i<a.length;i++)s+=String.fromCharCode(a[i]);globalThis['{resultSlot}']=btoa(s)}}).catch(e=>globalThis['{resultSlot}']='ERROR:'+e)";
            await session.SendAsync("Runtime.evaluate", CdpJson.Params(("expression", responseExpression)), timeout.Token)
                .ConfigureAwait(false);
            var responseHeld = await WaitForAsync(store, session.PageId,
                item => item.Url.Contains(responseNonce, StringComparison.Ordinal) && item.Stage == TrafficStage.Response &&
                        item.State == TrafficState.Paused,
                timeout.Token).ConfigureAwait(false);
            await bodyEdits.EditBodyAsync(responseHeld.Id, "response",
                new BinaryBodyEdit(BinaryEditKind.Replace, 0, responseHeld.ResponseBody?.LongLength ?? 0,
                    Convert.ToBase64String(responseBytes), BinaryTextEncoding.Base64), timeout.Token).ConfigureAwait(false);
            await packets.ContinueAsync(responseHeld.Id, timeout.Token).ConfigureAwait(false);
            var browserBody = await WaitForRuntimeStringAsync(session, $"globalThis['{resultSlot}']", timeout.Token)
                .ConfigureAwait(false);
            Require(browserBody == Convert.ToBase64String(responseBytes),
                $"fulfilled response bytes received by browser did not match ({browserBody})");
            passed++;
            log.Info($"TRAFFIC_SELFTEST PASS response-fulfill id={responseHeld.Id} bytes={responseBytes.Length}");

            log.Info($"TRAFFIC_SELFTEST RESULT {passed}/5 PASS");
            ExitIfRequested(0);
        }
        catch (Exception error)
        {
            log.Error($"TRAFFIC_SELFTEST RESULT {passed}/5 FAIL: {error.Message}", error);
            ExitIfRequested(2);
        }
        finally
        {
            try { await modes.SetInterceptionModeAsync(PacketInterceptionMode.Off, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private static string? ReadEchoBodyBase64(byte[]? body)
    {
        if (body is null) return null;
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("bodyBase64", out var value) ? value.GetString() : null;
    }

    private static async Task<string> WaitForRuntimeStringAsync(
        ICdpSession session, string expression, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await session.SendAsync("Runtime.evaluate",
                CdpJson.Params(("expression", expression), ("returnByValue", true)), cancellationToken).ConfigureAwait(false);
            var value = CdpJson.TryGetString(json, "result", "value");
            if (!string.IsNullOrEmpty(value)) return value;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
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
        if (Environment.GetEnvironmentVariable("HACKERMES_SELFTEST_EXIT") != "1") return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(exitCode);
        });
    }
}
