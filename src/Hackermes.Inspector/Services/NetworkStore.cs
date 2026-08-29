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
public sealed class NetworkStore : INetworkQueryService, INetworkSecurityMetadataQueryService
{
    /// <summary>列表上限。超出后丢弃最旧的 —— 长时间运行的页面能产生上万条请求。</summary>
    private const int MaxEntries = 1000;

    /// <summary>Agent 记录与 CDP 请求的配对时间窗。同一个请求两边上报的时刻会有几十毫秒差。</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(3);

    private readonly IAppLogger _logger;
    private readonly Dictionary<string, NetworkEntry> _byRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ICdpSession> _sessionsByPage = new(StringComparer.Ordinal);
    private readonly List<PendingInitiator> _pendingInitiators = [];
    private readonly Dictionary<string, List<IDisposable>> _subscriptionsByPage = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public NetworkStore(ICdpSessionRegistry registry, IEventBus eventBus, IAppLogger logger)
    {
        _logger = logger.ForCategory(nameof(NetworkStore));

        foreach (var session in registry.All)
            _ = AttachAsync(session);

        registry.SessionOpened += session => _ = AttachAsync(session);
        registry.SessionClosed += OnSessionClosed;

        eventBus.Subscribe<PageAgentMessageEvent>(OnAgentMessage);
    }

    /// <summary>UI 直接绑定这个集合。只在 UI 线程改动。</summary>
    public ObservableCollection<NetworkEntry> Entries { get; } = [];

    public event Action? Changed;

    public IReadOnlyList<NetworkObservation> Read(int last = 100, bool failuresOnly = false, string? pageId = null) => Entries
        .Where(entry => string.IsNullOrWhiteSpace(pageId) || string.Equals(entry.PageId, pageId, StringComparison.Ordinal))
        .Where(entry => !failuresOnly || entry.IsFailed)
        .Take(Math.Clamp(last, 1, MaxEntries))
        .Select(entry => new NetworkObservation(
            entry.RequestId, entry.Method, entry.Url, entry.Status, entry.StatusText,
            entry.IsFailed, entry.DurationMs, entry.InitiatorKind, entry.InitiatorStack))
        .ToArray();

    public NetworkSecurityMetadata ReadSecurityMetadata(string pageId, string documentUrl)
    {
        if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(documentUrl))
            return NetworkSecurityMetadata.Empty;
        // 查询走同步数据源(_byRequestId),不依赖 UI 线程泉的时机——Agent 调用是确定性的。
        List<NetworkEntry> snapshot;
        lock (_gate)
        {
            snapshot = _byRequestId.Values.ToList();
        }
        var entry = snapshot.FirstOrDefault(candidate =>
            string.Equals(candidate.PageId, pageId, StringComparison.Ordinal) &&
            string.Equals(candidate.ResourceType, "Document", StringComparison.OrdinalIgnoreCase) &&
            DocumentUrlsMatch(candidate.Url, documentUrl));
        return entry?.SecurityMetadata ?? NetworkSecurityMetadata.Empty;
    }

    /// <summary>
    /// 懒加载一条请求的响应体(Burp 风格详情用)。只在用户点开该条时调用,
    /// 避免 getResponseBody 对全量记录产生放大流量。
    /// </summary>
    public async System.Threading.Tasks.Task<string> LoadResponseBodyAsync(NetworkEntry entry)
    {
        ICdpSession? session;
        lock (_gate) _sessionsByPage.TryGetValue(entry.PageId, out session);
        if (session is null || !session.IsAlive)
            throw new InvalidOperationException("页面会话已关闭，无法读取响应体。");

        var responseJson = await session.SendAsync("Network.getResponseBody",
            System.Text.Json.JsonSerializer.Serialize(new { requestId = entry.RequestId }))
            .ConfigureAwait(true);
        var body = CdpJson.TryGetString(responseJson, "body") ?? string.Empty;
        var base64 = string.Equals(CdpJson.TryGetString(responseJson, "base64Encoded"), "true", StringComparison.OrdinalIgnoreCase);
        if (!base64) return body;

        try
        {
            var bytes = Convert.FromBase64String(body);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return "(二进制响应体无法以文本显示)";
        }
    }

    public void Clear()
    {        UiThreadBridge.Post(() =>
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

            var subscriptions = new List<IDisposable>
            {
                await session.SubscribeAsync("Network.requestWillBeSent", e => OnRequestWillBeSent(session.PageId, e)).ConfigureAwait(false),
                await session.SubscribeAsync("Network.responseReceived", OnResponseReceived).ConfigureAwait(false),
                await session.SubscribeAsync("Network.loadingFinished", OnLoadingFinished).ConfigureAwait(false),
                await session.SubscribeAsync("Network.loadingFailed", OnLoadingFailed).ConfigureAwait(false)
            };
            lock (_gate)
            {
                if (!session.IsAlive)
                {
                    foreach (var subscription in subscriptions) subscription.Dispose();
                    return;
                }
                if (_subscriptionsByPage.Remove(session.PageId, out var previous))
                    foreach (var subscription in previous) subscription.Dispose();
                _subscriptionsByPage[session.PageId] = subscriptions;
                _sessionsByPage[session.PageId] = session;
            }

            _logger.Info($"已接入页面网络流: {session.PageId}");
        }
        catch (Exception ex)
        {
            _logger.Error($"接入页面网络流失败: {session.PageId}", ex);
        }
    }

    private void OnRequestWillBeSent(string pageId, CdpEventArgs e)
    {
        var requestId = CdpJson.TryGetString(e.ParametersJson, "requestId");
        var url = CdpJson.TryGetString(e.ParametersJson, "request", "url");

        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(url))
            return;

        var entry = new NetworkEntry
        {
            PageId = pageId,
            RequestId = requestId,
            Method = CdpJson.TryGetString(e.ParametersJson, "request", "method") ?? "GET",
            Url = url,
            ResourceType = CdpJson.TryGetString(e.ParametersJson, "type") ?? string.Empty
        };

        // Burp 风格详情的数据源:请求行之外还要有头部块与可选的请求体。
        if (CdpJson.TryGetElement(e.ParametersJson, "request", "headers") is { } requestHeaders)
            entry.RequestHeadersJson = requestHeaders.ToString();
        entry.RequestBody = CdpJson.TryGetString(e.ParametersJson, "request", "postData");

        // CDP 自己的 initiator 多数只有 URL,拿不到具体栈帧;Agent 的记录更有用。
        TryApplyPendingInitiator(entry);

        // 查询数据源同步登记:ReadSecurityMetadata 等查询(含 Agent 调用)必须立即可见,
        // 不能等 UI 泵的时机;Entries(可观察集合)镜像仍走 UI 线程。
        lock (_gate)
        {
            _byRequestId[requestId] = entry;
        }

        UiThreadBridge.Post(() =>
        {
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

    private void OnSessionClosed(string pageId)
    {
        lock (_gate)
        {
            if (_subscriptionsByPage.Remove(pageId, out var subscriptions))
                foreach (var subscription in subscriptions) subscription.Dispose();
            _sessionsByPage.Remove(pageId);
            foreach (var id in _byRequestId.Where(pair => pair.Value.PageId == pageId).Select(pair => pair.Key).ToArray())
                _byRequestId.Remove(id);
            _pendingInitiators.Clear();
        }

        UiThreadBridge.Post(() =>
        {
            foreach (var entry in Entries.Where(entry => entry.PageId == pageId).ToArray())
                Entries.Remove(entry);
            Changed?.Invoke();
        });
        _logger.Debug($"会话关闭并释放网络记录: {pageId}");
    }

    private void OnResponseReceived(CdpEventArgs e)
    {
        var requestId = CdpJson.TryGetString(e.ParametersJson, "requestId");
        if (string.IsNullOrEmpty(requestId))
            return;

        var status = CdpJson.TryGetInt(e.ParametersJson, "response", "status") ?? 0;
        var mime = CdpJson.TryGetString(e.ParametersJson, "response", "mimeType") ?? string.Empty;
        var statusText = CdpJson.TryGetString(e.ParametersJson, "response", "statusText");
        var headers = CdpJson.TryGetElement(e.ParametersJson, "response", "headers");
        var securityMetadata = ReadSecurityMetadata(headers, status);

        UpdateEntry(requestId, entry =>
        {
            entry.Status = status;
            entry.StatusText = string.IsNullOrEmpty(statusText) ? status.ToString() : $"{status} {statusText}";
            entry.MimeType = mime;
            entry.IsFailed = status >= 400;
            entry.SecurityMetadata = securityMetadata;
            if (headers is { ValueKind: System.Text.Json.JsonValueKind.Object } responseHeaders)
                entry.ResponseHeadersJson = responseHeaders.ToString();
        });
    }

    private static NetworkSecurityMetadata ReadSecurityMetadata(System.Text.Json.JsonElement? headersElement, int status)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headersElement is { ValueKind: System.Text.Json.JsonValueKind.Object } element)
        {
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
                if (!string.IsNullOrEmpty(value)) headers[property.Name] = value;
            }
        }

        headers.TryGetValue("content-security-policy", out var csp);
        headers.TryGetValue("content-security-policy-report-only", out var cspReportOnly);
        var policy = string.Join(";", new[] { csp, cspReportOnly }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var directives = policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.ToLowerInvariant())
            .Where(value => value.Length <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        var policyTokens = policy.Split(new[] { ' ', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var cookies = headers.TryGetValue("set-cookie", out var setCookie)
            ? ReadCookieSummary(setCookie)
            : new PageSecurityCookieSummary(0, 0, 0, 0, 0, 0, 0);

        return new NetworkSecurityMetadata(
            true,
            status,
            headers.ContainsKey("strict-transport-security"),
            !string.IsNullOrWhiteSpace(csp),
            !string.IsNullOrWhiteSpace(cspReportOnly),
            directives,
            policyTokens.Contains("'unsafe-inline'", StringComparer.OrdinalIgnoreCase),
            policyTokens.Contains("'unsafe-eval'", StringComparer.OrdinalIgnoreCase),
            policyTokens.Contains("*", StringComparer.Ordinal),
            headers.TryGetValue("x-content-type-options", out var contentType) &&
                contentType.Contains("nosniff", StringComparison.OrdinalIgnoreCase),
            headers.ContainsKey("x-frame-options") || directives.Contains("frame-ancestors", StringComparer.Ordinal),
            headers.ContainsKey("referrer-policy"),
            headers.ContainsKey("permissions-policy"),
            headers.ContainsKey("cross-origin-opener-policy"),
            headers.ContainsKey("cross-origin-embedder-policy"),
            headers.ContainsKey("cross-origin-resource-policy"),
            cookies);
    }

    private static PageSecurityCookieSummary ReadCookieSummary(string value)
    {
        var rows = value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows.Length == 0 && !string.IsNullOrWhiteSpace(value)) rows = [value];
        var secure = 0;
        var httpOnly = 0;
        var strict = 0;
        var lax = 0;
        var none = 0;
        var partitioned = 0;
        foreach (var row in rows.Take(256))
        {
            var attributes = row.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToArray();
            if (attributes.Any(attribute => string.Equals(attribute, "secure", StringComparison.OrdinalIgnoreCase))) secure++;
            if (attributes.Any(attribute => string.Equals(attribute, "httponly", StringComparison.OrdinalIgnoreCase))) httpOnly++;
            if (attributes.Any(attribute => string.Equals(attribute, "partitioned", StringComparison.OrdinalIgnoreCase))) partitioned++;
            var sameSite = attributes.FirstOrDefault(attribute => attribute.StartsWith("samesite=", StringComparison.OrdinalIgnoreCase));
            if (sameSite?.EndsWith("strict", StringComparison.OrdinalIgnoreCase) == true) strict++;
            else if (sameSite?.EndsWith("lax", StringComparison.OrdinalIgnoreCase) == true) lax++;
            else if (sameSite?.EndsWith("none", StringComparison.OrdinalIgnoreCase) == true) none++;
        }
        return new PageSecurityCookieSummary(Math.Min(rows.Length, 256), secure, httpOnly, strict, lax, none, partitioned);
    }

    private static bool DocumentUrlsMatch(string first, string second)
    {
        if (!Uri.TryCreate(first, UriKind.Absolute, out var left) ||
            !Uri.TryCreate(second, UriKind.Absolute, out var right))
            return string.Equals(first, second, StringComparison.Ordinal);
        return Uri.Compare(left, right,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;
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
        // 状态(状态码/安全元数据等)同步落到查询数据源,保证 ReadSecurityMetadata
        // 与 Agent 查询的确定性;可观察集合的刷新仍走 UI 线程。
        lock (_gate)
        {
            if (_byRequestId.TryGetValue(requestId, out var tracked))
                mutate(tracked);
        }

        UiThreadBridge.Post(() =>
        {
            NetworkEntry? entry;

            lock (_gate)
            {
                _byRequestId.TryGetValue(requestId, out entry);
            }

            if (entry is null)
                return;

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
