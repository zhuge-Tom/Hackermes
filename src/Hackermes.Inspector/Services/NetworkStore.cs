using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.Inspector.Models;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hackermes.Inspector.Services;

/// <summary>
/// 网络记录中心。自动跟随所有已打开的页面会话。
/// </summary>
public sealed class NetworkStore : INetworkQueryService
{
    /// <summary>列表上限。超出后丢弃最旧的 —— 长时间运行的页面能产生上万条请求。</summary>
    private const int MaxEntries = 2000;

    /// <summary>Agent 记录与 CDP 请求的配对时间窗。同一个请求两边上报的时刻会有几十毫秒差。</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(3);

    private readonly IAppLogger _logger;
    private readonly Dictionary<string, NetworkEntry> _byRequestId = new(StringComparer.Ordinal);
    private readonly List<PendingInitiator> _pendingInitiators = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();

    public NetworkStore(ICdpSessionRegistry registry, IEventBus eventBus, IAppLogger logger)
    {
        _logger = logger.ForCategory(nameof(NetworkStore));

        foreach (var session in registry.All)
            _ = AttachAsync(session);

        registry.SessionOpened += session => _ = AttachAsync(session);
        registry.SessionClosed += pageId => _logger.Debug($"会话关闭: {pageId}");

        eventBus.Subscribe<PageAgentMessageEvent>(OnAgentMessage);
    }

    /// <summary>UI 直接绑定这个集合。只在 UI 线程改动。</summary>
    public ObservableCollection<NetworkEntry> Entries { get; } = [];

    public event Action? Changed;

    public IReadOnlyList<NetworkObservation> Read(int last = 100, bool failuresOnly = false) => Entries
        .Where(entry => !failuresOnly || entry.IsFailed)
        .Take(Math.Clamp(last, 1, 2000))
        .Select(entry => new NetworkObservation(
            entry.RequestId, entry.Method, entry.Url, entry.Status, entry.StatusText,
            entry.IsFailed, entry.DurationMs, entry.InitiatorKind, entry.InitiatorStack))
        .ToArray();

    public void Clear()
    {
        UiThreadBridge.Post(() =>
        {
            lock (_gate)
            {
                _byRequestId.Clear();
                _pendingInitiators.Clear();
            }

            Entries.Clear();
            Changed?.Invoke();
        });
    }

    private async System.Threading.Tasks.Task AttachAsync(ICdpSession session)
    {
        try
        {
            await session.EnableDomainAsync("Network").ConfigureAwait(false);

            _subscriptions.Add(await session.SubscribeAsync("Network.requestWillBeSent", OnRequestWillBeSent).ConfigureAwait(false));
            _subscriptions.Add(await session.SubscribeAsync("Network.responseReceived", OnResponseReceived).ConfigureAwait(false));
            _subscriptions.Add(await session.SubscribeAsync("Network.loadingFinished", OnLoadingFinished).ConfigureAwait(false));
            _subscriptions.Add(await session.SubscribeAsync("Network.loadingFailed", OnLoadingFailed).ConfigureAwait(false));

            _logger.Info($"已接入页面网络流: {session.PageId}");
        }
        catch (Exception ex)
        {
            _logger.Error($"接入页面网络流失败: {session.PageId}", ex);
        }
    }

    private void OnRequestWillBeSent(CdpEventArgs e)
    {
        var requestId = CdpJson.TryGetString(e.ParametersJson, "requestId");
        var url = CdpJson.TryGetString(e.ParametersJson, "request", "url");

        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(url))
            return;

        var entry = new NetworkEntry
        {
            RequestId = requestId,
            Method = CdpJson.TryGetString(e.ParametersJson, "request", "method") ?? "GET",
            Url = url,
            ResourceType = CdpJson.TryGetString(e.ParametersJson, "type") ?? string.Empty
        };

        // CDP 自己的 initiator 多数只有 URL,拿不到具体栈帧;Agent 的记录更有用。
        TryApplyPendingInitiator(entry);

        UiThreadBridge.Post(() =>
        {
            lock (_gate)
            {
                _byRequestId[requestId] = entry;
            }

            Entries.Insert(0, entry);

            while (Entries.Count > MaxEntries)
            {
                var oldest = Entries[^1];
                Entries.RemoveAt(Entries.Count - 1);

                lock (_gate)
                {
                    _byRequestId.Remove(oldest.RequestId);
                }
            }

            Changed?.Invoke();
        });
    }

    private void OnResponseReceived(CdpEventArgs e)
    {
        var requestId = CdpJson.TryGetString(e.ParametersJson, "requestId");
        if (string.IsNullOrEmpty(requestId))
            return;

        var status = CdpJson.TryGetInt(e.ParametersJson, "response", "status") ?? 0;
        var mime = CdpJson.TryGetString(e.ParametersJson, "response", "mimeType") ?? string.Empty;
        var statusText = CdpJson.TryGetString(e.ParametersJson, "response", "statusText");

        UpdateEntry(requestId, entry =>
        {
            entry.Status = status;
            entry.StatusText = string.IsNullOrEmpty(statusText) ? status.ToString() : $"{status} {statusText}";
            entry.MimeType = mime;
            entry.IsFailed = status >= 400;
        });
    }

    private void OnLoadingFinished(CdpEventArgs e)
    {
        var requestId = CdpJson.TryGetString(e.ParametersJson, "requestId");
        if (string.IsNullOrEmpty(requestId))
            return;

        var length = CdpJson.TryGetElement(e.ParametersJson, "encodedDataLength");
        var bytes = length is { ValueKind: System.Text.Json.JsonValueKind.Number } el && el.TryGetDouble(out var d)
            ? (long)d
            : 0;

        UpdateEntry(requestId, entry =>
        {
            entry.EncodedBytes = bytes;
            entry.DurationMs = (DateTime.Now - entry.StartedAt).TotalMilliseconds;
        });
    }

    private void OnLoadingFailed(CdpEventArgs e)
    {
        var requestId = CdpJson.TryGetString(e.ParametersJson, "requestId");
        if (string.IsNullOrEmpty(requestId))
            return;

        var reason = CdpJson.TryGetString(e.ParametersJson, "errorText") ?? "失败";

        UpdateEntry(requestId, entry =>
        {
            entry.IsFailed = true;
            entry.StatusText = reason;
            entry.DurationMs = (DateTime.Now - entry.StartedAt).TotalMilliseconds;
        });
    }

    private void UpdateEntry(string requestId, Action<NetworkEntry> mutate)
    {
        UiThreadBridge.Post(() =>
        {
            NetworkEntry? entry;

            lock (_gate)
            {
                _byRequestId.TryGetValue(requestId, out entry);
            }

            if (entry is null)
                return;

            mutate(entry);
            Changed?.Invoke();
        });
    }

    #region Page Agent 合并

    /// <summary>
    /// Agent 上报的发起信息。和 CDP 请求的配对靠 URL 后缀 + 时间窗:
    /// 两边的标识体系不同(Agent 自己生成 id,CDP 用 requestId),没有共同主键。
    /// </summary>
    private sealed record PendingInitiator(string Url, string Kind, string? Stack, DateTime At);

    private void OnAgentMessage(PageAgentMessageEvent e)
    {
        if (!string.Equals(e.Kind, "net", StringComparison.Ordinal))
            return;

        if (!string.Equals(CdpJson.TryGetString(e.PayloadJson, "phase"), "start", StringComparison.Ordinal))
            return;

        var url = CdpJson.TryGetString(e.PayloadJson, "url");
        if (string.IsNullOrEmpty(url))
            return;

        var pending = new PendingInitiator(
            url,
            e.SubKind ?? "unknown",
            CdpJson.TryGetString(e.PayloadJson, "stack"),
            DateTime.Now);

        lock (_gate)
        {
            _pendingInitiators.Add(pending);
            _pendingInitiators.RemoveAll(p => DateTime.Now - p.At > MatchWindow);
        }

        // Agent 消息可能晚于 CDP 请求到达,回头补一次。
        UiThreadBridge.Post(() =>
        {
            var target = Entries.FirstOrDefault(entry =>
                entry.InitiatorStack is null && UrlMatches(entry.Url, pending.Url));

            if (target is null)
                return;

            target.InitiatorStack = pending.Stack;
            target.InitiatorKind = pending.Kind;
            Changed?.Invoke();
        });
    }

    private void TryApplyPendingInitiator(NetworkEntry entry)
    {
        lock (_gate)
        {
            var match = _pendingInitiators
                .Where(p => DateTime.Now - p.At <= MatchWindow && UrlMatches(entry.Url, p.Url))
                .OrderByDescending(p => p.At)
                .FirstOrDefault();

            if (match is null)
                return;

            entry.InitiatorStack = match.Stack;
            entry.InitiatorKind = match.Kind;
            _pendingInitiators.Remove(match);
        }
    }

    /// <summary>Agent 拿到的可能是相对地址(如 <c>data.json</c>),CDP 给的是绝对地址。</summary>
    private static bool UrlMatches(string absoluteUrl, string reportedUrl)
    {
        if (string.Equals(absoluteUrl, reportedUrl, StringComparison.Ordinal))
            return true;

        var trimmed = reportedUrl.TrimStart('.', '/');
        return trimmed.Length > 0 && absoluteUrl.EndsWith(trimmed, StringComparison.Ordinal);
    }

    #endregion
}
