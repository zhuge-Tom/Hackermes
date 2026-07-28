using Hookmes.Automation.Packet;
using Hookmes.Base.Events;
using Hookmes.Cdp.Session;
using Hookmes.Inspector.ViewModels;
using Hookmes.Platform.Events;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

/// <summary>让人工工作台、CLI 和 AI 共用同一个 Traffic 核心。</summary>
public sealed class TrafficIntegrationService : IPacketCommandService, ITrafficWorkbenchService, IDisposable
{
    private readonly ITrafficService _traffic;
    private readonly ITrafficStore _store;
    private readonly ICdpSessionRegistry _sessions;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly IDisposable _activeSubscription;
    private string? _activePageId;
    private bool _intercept;

    public TrafficIntegrationService(
        ITrafficService traffic, ITrafficStore store, ICdpSessionRegistry sessions, IEventBus eventBus)
    {
        _traffic = traffic;
        _store = store;
        _sessions = sessions;
        _store.Changed += OnStoreChanged;
        _sessions.SessionOpened += OnSessionOpened;
        _sessions.SessionClosed += OnSessionClosed;
        _activeSubscription = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(e =>
            _activePageId = e.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal) ? id : null);
        foreach (var session in sessions.All) _ = StartForPageAsync(session.PageId);
    }

    public event Action? Changed;

    public IReadOnlyList<TrafficExchange> Exchanges => _store.Read(5000)
        .Select(ToExchange).ToArray();

    public bool IsInterceptEnabled
    {
        get => _intercept;
        set
        {
            if (_intercept == value) return;
            _intercept = value;
            _traffic.SetModificationsEnabled(value);
            _ = RestartAllAsync();
            Changed?.Invoke();
        }
    }

    public Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<TrafficMessage> query = _store.Read(5000, _activePageId);
        if (!string.IsNullOrWhiteSpace(filter))
            query = query.Where(item => item.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Method.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<PacketSummary>>(query.Select(item => new PacketSummary(
            item.Id, item.Method, item.Url, item.ResponseStatus, item.State == TrafficState.Paused)).ToArray());
    }

    public Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = _store.Get(id);
        return Task.FromResult(item is null ? null : side == "response" ? FormatResponse(item) : FormatRequest(item));
    }

    public async Task ReplayAsync(string id, CancellationToken cancellationToken) =>
        _ = await _traffic.ReplayAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken)
    {
        _intercept = enabled;
        _traffic.SetModificationsEnabled(enabled);
        await RestartAllAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public Task ContinueAsync(string id, CancellationToken cancellationToken) =>
        _traffic.ContinueAsync(id, cancellationToken: cancellationToken);

    public Task DropAsync(string id, CancellationToken cancellationToken) =>
        _traffic.FailAsync(id, cancellationToken: cancellationToken);

    public async Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken)
    {
        var source = Required(id);
        var packet = HttpPacketCodec.Parse(rawPacket);
        if (side == "response")
            await _traffic.FulfillAsync(id, ToResponseEdit(packet), cancellationToken).ConfigureAwait(false);
        else
            await _traffic.ContinueAsync(id, ToRequestEdit(packet, source), cancellationToken).ConfigureAwait(false);
    }

    public Task<TrafficOperationResult> AnalyzeAsync(
        string exchangeId, string request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = HttpPacketAnalyzer.Analyze(HttpPacketCodec.Parse(request));
        var summary = analysis.Findings.Count == 0
            ? "未发现内置规则问题。"
            : string.Join(Environment.NewLine, analysis.Findings.Select(f => $"[{f.Severity}] {f.Code}: {f.Message}"));
        if (analysis.SensitiveFields.Count > 0)
            summary += Environment.NewLine + "敏感字段: " + string.Join(", ", analysis.SensitiveFields);
        return Task.FromResult(new TrafficOperationResult(true, summary));
    }

    public async Task<TrafficOperationResult> ReplayAsync(
        string exchangeId, string request, CancellationToken cancellationToken)
    {
        var source = Required(exchangeId);
        var result = await _traffic.ReplayAsync(exchangeId,
            ToRequestEdit(HttpPacketCodec.Parse(request), source), cancellationToken).ConfigureAwait(false);
        var response = FormatResponse(result);
        return new TrafficOperationResult(true, $"重放完成: HTTP {result.Status}", response);
    }

    public Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken) =>
        _traffic.ContinueAsync(exchangeId,
            ToRequestEdit(HttpPacketCodec.Parse(request), Required(exchangeId)), cancellationToken);

    public Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken) =>
        _traffic.FulfillAsync(exchangeId, ToResponseEdit(HttpPacketCodec.Parse(response)), cancellationToken);

    private void OnStoreChanged(TrafficMessage _) => Changed?.Invoke();
    private void OnSessionOpened(ICdpSession session) => _ = StartForPageAsync(session.PageId);
    private void OnSessionClosed(string pageId) => _ = _traffic.StopCaptureAsync(pageId);

    private async Task StartForPageAsync(string pageId, CancellationToken ct = default)
    {
        try
        {
            await _traffic.StartCaptureAsync(pageId,
                new TrafficCaptureOptions(PauseRequests: _intercept, PauseResponses: false, CaptureResponseBodies: true), ct)
                .ConfigureAwait(false);
        }
        catch { /* 页面可能已在异步启动过程中关闭；不影响浏览器本身。 */ }
    }

    private async Task RestartAllAsync(CancellationToken ct = default)
    {
        await _captureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.All)
            {
                await _traffic.StopCaptureAsync(session.PageId, ct).ConfigureAwait(false);
                await StartForPageAsync(session.PageId, ct).ConfigureAwait(false);
            }
        }
        finally { _captureGate.Release(); }
    }

    private TrafficMessage Required(string id) => _store.Get(id)
        ?? throw new KeyNotFoundException($"数据包不存在: {id}");

    private static TrafficExchange ToExchange(TrafficMessage item) => new(
        item.Id, item.CapturedAt, item.Method, item.Url, item.ResponseStatus,
        FormatRequest(item), FormatResponse(item), item.State == TrafficState.Paused);

    private static string FormatRequest(TrafficMessage item)
    {
        var uri = Uri.TryCreate(item.Url, UriKind.Absolute, out var parsed) ? parsed : null;
        var target = uri?.PathAndQuery ?? item.Url;
        var headers = item.RequestHeaders.Select(h => new HttpHeader(h.Name, h.Value)).ToList();
        if (uri is not null && !headers.Any(h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)))
            headers.Insert(0, new HttpHeader("Host", uri.Authority));
        return HttpPacketCodec.Format(new HttpPacket
        {
            Kind = HttpPacketKind.Request, ProtocolVersion = "HTTP/1.1", Method = item.Method,
            Target = target, Headers = headers, Body = DecodeBody(item.RequestBody)
        });
    }

    private static string FormatResponse(TrafficMessage item)
    {
        if (item.ResponseStatus is null && item.ResponseHeaders.Count == 0 && item.ResponseBody is null) return string.Empty;
        return HttpPacketCodec.Format(new HttpPacket
        {
            Kind = HttpPacketKind.Response, ProtocolVersion = "HTTP/1.1", StatusCode = item.ResponseStatus ?? 200,
            ReasonPhrase = item.ResponseStatusText ?? string.Empty,
            Headers = item.ResponseHeaders.Select(h => new HttpHeader(h.Name, h.Value)).ToArray(),
            Body = DecodeBody(item.ResponseBody)
        });
    }

    private static string FormatResponse(TrafficReplayResult result) => HttpPacketCodec.Format(new HttpPacket
    {
        Kind = HttpPacketKind.Response, ProtocolVersion = "HTTP/1.1", StatusCode = result.Status,
        ReasonPhrase = result.StatusText ?? string.Empty,
        Headers = result.Headers.Select(h => new HttpHeader(h.Name, h.Value)).ToArray(),
        Body = DecodeBody(result.Body)
    });

    private static TrafficRequestEdit ToRequestEdit(HttpPacket packet, TrafficMessage source)
    {
        if (packet.Kind != HttpPacketKind.Request) throw new HttpPacketParseException("需要 HTTP 请求。");
        var url = Uri.TryCreate(packet.Target, UriKind.Absolute, out var absolute)
            ? absolute.ToString() : new Uri(new Uri(source.Url), packet.Target ?? "/").ToString();
        return new TrafficRequestEdit(url, packet.Method,
            packet.Headers.Select(h => new TrafficHeader(h.Name, h.Value)).ToArray(), Encoding.UTF8.GetBytes(packet.Body));
    }

    private static TrafficResponseEdit ToResponseEdit(HttpPacket packet)
    {
        if (packet.Kind != HttpPacketKind.Response) throw new HttpPacketParseException("需要 HTTP 响应。");
        return new TrafficResponseEdit(packet.StatusCode ?? 200, packet.ReasonPhrase,
            packet.Headers.Select(h => new TrafficHeader(h.Name, h.Value)).ToArray(), Encoding.UTF8.GetBytes(packet.Body));
    }

    private static string DecodeBody(byte[]? body) => body is null ? string.Empty : Encoding.UTF8.GetString(body);

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _sessions.SessionOpened -= OnSessionOpened;
        _sessions.SessionClosed -= OnSessionClosed;
        _activeSubscription.Dispose();
        _captureGate.Dispose();
    }
}
