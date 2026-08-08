using Hackermes.Base.Diagnostics;
using Hackermes.Cdp.Session;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Traffic.Services;

public interface ITrafficService : IAsyncDisposable, ITrafficRuleExecutionSource
{
    bool ModificationsEnabled { get; }
    void SetModificationsEnabled(bool enabled);
    Task StartCaptureAsync(string pageId, TrafficCaptureOptions? options = null, CancellationToken cancellationToken = default);
    Task StopCaptureAsync(string pageId, CancellationToken cancellationToken = default);
    Task ContinueAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default);
    Task FailAsync(string id, string reason = "BlockedByClient", CancellationToken cancellationToken = default);
    Task FulfillAsync(string id, TrafficResponseEdit response, CancellationToken cancellationToken = default);
    Task<TrafficReplayResult> ReplayAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default);
}

/// <summary>Shared, UI-independent HTTP traffic engine backed by the CDP Fetch domain.</summary>
public sealed class TrafficService : ITrafficService
{
    private readonly ICdpSessionRegistry _registry;
    private readonly TrafficStore _store;
    private readonly ITrafficRuleSet _rules;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, CaptureContext> _contexts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private int _modificationsEnabled;

    public event Action<TrafficRuleExecutionEvent>? RuleExecuted;

    public TrafficService(ICdpSessionRegistry registry, TrafficStore store, ITrafficRuleSet rules, IAppLogger logger)
    {
        _registry = registry;
        _store = store;
        _rules = rules;
        _logger = logger.ForCategory(nameof(TrafficService));
    }

    public bool ModificationsEnabled => Volatile.Read(ref _modificationsEnabled) != 0;
    public void SetModificationsEnabled(bool enabled) => Volatile.Write(ref _modificationsEnabled, enabled ? 1 : 0);

    public async Task StartCaptureAsync(string pageId, TrafficCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_contexts.ContainsKey(pageId)) return;
            var session = GetSession(pageId);
            options = (options ?? new TrafficCaptureOptions()).Normalize();
            var patterns = new List<object> { new { urlPattern = "*", requestStage = "Request" } };
            if (options.CaptureResponseBodies || options.PauseResponses)
                patterns.Add(new { urlPattern = "*", requestStage = "Response" });
            var subscription = await session.SubscribeAsync("Fetch.requestPaused", e => _ = OnPausedAsync(session, options, e), cancellationToken).ConfigureAwait(false);
            try
            {
                await session.SendAsync("Fetch.enable", JsonSerializer.Serialize(new { patterns }), cancellationToken).ConfigureAwait(false);
                _contexts[pageId] = new CaptureContext(session, subscription);
            }
            catch
            {
                subscription.Dispose();
                throw;
            }
        }
        finally { _captureGate.Release(); }
    }

    public async Task StopCaptureAsync(string pageId, CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_contexts.TryRemove(pageId, out var context)) return;
            try { await context.Session.SendAsync("Fetch.disable", null, cancellationToken).ConfigureAwait(false); }
            finally { context.Subscription.Dispose(); }
        }
        finally { _captureGate.Release(); }
    }

    public Task ContinueAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default)
    {
        EnsureModificationAllowed(edit is not null);
        return ResolvePausedAsync(id, "Fetch.continueRequest", BuildContinueRequestParameters(FetchId(id), edit),
            TrafficState.Continued, cancellationToken);
    }

    public Task FailAsync(string id, string reason = "BlockedByClient", CancellationToken cancellationToken = default)
    {
        EnsureModificationAllowed(true);
        return ResolvePausedAsync(id, "Fetch.failRequest", new { requestId = FetchId(id), errorReason = reason }, TrafficState.Failed, cancellationToken);
    }

    public Task FulfillAsync(string id, TrafficResponseEdit response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response); EnsureModificationAllowed(true);
        return ResolvePausedAsync(id, "Fetch.fulfillRequest", BuildFulfillRequestParameters(FetchId(id), response),
            TrafficState.Fulfilled, cancellationToken);
    }

    public async Task<TrafficReplayResult> ReplayAsync(string id, TrafficRequestEdit? edit = null, CancellationToken cancellationToken = default)
    {
        var source = _store.Get(id) ?? throw new KeyNotFoundException($"Traffic item '{id}' was not found.");
        var session = GetSession(source.PageId);
        var method = edit?.Method ?? source.Method;
        var url = edit?.Url ?? source.Url;
        var headers = edit?.Headers ?? source.RequestHeaders;
        var body = edit?.Body ?? source.RequestBody;
        var script = BuildReplayScript(method, url, headers, body);
        var json = await session.SendAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = script, awaitPromise = true, returnByValue = true
        }), cancellationToken).ConfigureAwait(false);
        var value = ReadPath(json, "result", "value") ?? throw new InvalidOperationException("Replay returned no result.");
        var status = value.GetProperty("status").GetInt32();
        var statusText = value.TryGetProperty("statusText", out var st) ? st.GetString() : null;
        var responseHeaders = value.GetProperty("headers").EnumerateArray().Select(ReadHeader).ToArray();
        var responseBody = Convert.FromBase64String(value.GetProperty("body").GetString() ?? string.Empty);
        return new TrafficReplayResult(status, statusText, responseHeaders, responseBody);
    }

    private async Task OnPausedAsync(ICdpSession session, TrafficCaptureOptions options, CdpEventArgs args)
    {
        string? fetchId = null;
        try
        {
            using var doc = JsonDocument.Parse(args.ParametersJson);
            var root = doc.RootElement;
            fetchId = root.GetProperty("requestId").GetString();
            if (fetchId is null) return;
            var id = session.PageId + ":" + fetchId;
            var request = root.GetProperty("request");
            var responseStage = root.TryGetProperty("responseStatusCode", out var statusEl);
            var old = _store.Get(id);
            byte[]? responseBody = old?.ResponseBody;
            var responseHeaders = root.TryGetProperty("responseHeaders", out var responseHeadersElement)
                ? responseHeadersElement.EnumerateArray().Select(ReadHeader).ToArray() : old?.ResponseHeaders ?? [];
            var declaredResponseLength = ReadContentLength(responseHeaders);
            if (responseStage && options.CaptureResponseBodies &&
                (!declaredResponseLength.HasValue || declaredResponseLength <= options.MaxResponseBodyBytes))
            {
                try
                {
                    var bodyJson = await session.SendAsync("Fetch.getResponseBody", JsonSerializer.Serialize(new { requestId = fetchId })).ConfigureAwait(false);
                    using var bodyDoc = JsonDocument.Parse(bodyJson);
                    var result = bodyDoc.RootElement;
                    var raw = result.GetProperty("body").GetString() ?? string.Empty;
                    var candidate = result.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean() ? Convert.FromBase64String(raw) : Encoding.UTF8.GetBytes(raw);
                    responseBody = candidate.Length <= options.MaxResponseBodyBytes ? candidate : null;
                    if (responseBody is null)
                        _logger.Info($"Response body skipped for {id}: {candidate.Length:N0} B exceeds {options.MaxResponseBodyBytes:N0} B capture limit.");
                }
                catch (Exception ex) { _logger.Warn($"Response body unavailable for {id}: {ex.Message}"); }
            }
            else if (responseStage && options.CaptureResponseBodies && declaredResponseLength is { } length)
            {
                _logger.Info($"Response body skipped for {id}: declared {length:N0} B exceeds {options.MaxResponseBodyBytes:N0} B capture limit.");
            }
            var message = new TrafficMessage(id, session.PageId, responseStage ? TrafficStage.Response : TrafficStage.Request,
                TrafficState.Paused, request.GetProperty("method").GetString() ?? "GET", request.GetProperty("url").GetString() ?? string.Empty,
                ReadRequestHeaders(request), ReadPostData(request) ?? old?.RequestBody,
                responseStage ? statusEl.GetInt32() : old?.ResponseStatus,
                root.TryGetProperty("responseStatusText", out var phrase) ? phrase.GetString() : old?.ResponseStatusText,
                responseHeaders,
                responseBody, root.TryGetProperty("resourceType", out var rt) ? rt.GetString() ?? string.Empty : old?.ResourceType ?? string.Empty,
                old?.CapturedAt ?? DateTimeOffset.UtcNow);
            _store.Put(message);

            var rule = ModificationsEnabled ? _rules.Match(message) : null;
            if (rule is not null)
            {
                _store.Put(message with { AppliedRuleId = rule.Id });
                var action = ResolveRuleAction(rule, responseStage);
                var before = Metadata(message);
                var planned = PlannedMetadata(message, rule, action);
                PublishRuleExecution(new TrafficRuleExecutionEvent(rule.Id, id, session.PageId, message.Stage,
                    action, TrafficRuleExecutionResult.Matched, before, planned, DateTimeOffset.UtcNow));
                if (action == TrafficRuleAction.None)
                {
                    PublishRuleExecution(new TrafficRuleExecutionEvent(rule.Id, id, session.PageId, message.Stage,
                        action, TrafficRuleExecutionResult.Skipped, before, before, DateTimeOffset.UtcNow));
                }
                else if (action == TrafficRuleAction.Pause)
                {
                    PublishRuleExecution(new TrafficRuleExecutionEvent(rule.Id, id, session.PageId, message.Stage,
                        action, TrafficRuleExecutionResult.Succeeded, before, before, DateTimeOffset.UtcNow));
                    return;
                }
                else
                {
                    try
                    {
                        if (action == TrafficRuleAction.Fail) await FailAsync(id, rule.FailureReason).ConfigureAwait(false);
                        else if (action == TrafficRuleAction.FulfillResponse) await FulfillAsync(id, rule.ResponseEdit!).ConfigureAwait(false);
                        else await ContinueAsync(id, rule.RequestEdit!).ConfigureAwait(false);
                        var after = _store.Get(id) is { } current ? planned with { State = current.State } : planned;
                        PublishRuleExecution(new TrafficRuleExecutionEvent(rule.Id, id, session.PageId, message.Stage,
                            action, TrafficRuleExecutionResult.Succeeded, before, after, DateTimeOffset.UtcNow));
                        return;
                    }
                    catch (Exception ruleError)
                    {
                        PublishRuleExecution(new TrafficRuleExecutionEvent(rule.Id, id, session.PageId, message.Stage,
                            action, TrafficRuleExecutionResult.Failed, before, planned, DateTimeOffset.UtcNow,
                            ruleError.GetType().Name));
                        throw;
                    }
                }
            }
            if ((responseStage && options.PauseResponses) || (!responseStage && options.PauseRequests)) return;
            await ContinueInternalAsync(session, id, fetchId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to process Fetch.requestPaused.", ex);
            if (fetchId is not null) try { await session.SendAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId = fetchId })).ConfigureAwait(false); } catch { }
        }
    }

    private async Task ContinueInternalAsync(ICdpSession session, string id, string fetchId)
    {
        await session.SendAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId = fetchId })).ConfigureAwait(false);
        UpdateState(id, TrafficState.Continued);
    }

    private async Task ResolvePausedAsync(string id, string method, object parameters, TrafficState state, CancellationToken ct)
    {
        var item = _store.Get(id) ?? throw new KeyNotFoundException($"Traffic item '{id}' was not found.");
        if (item.State != TrafficState.Paused) throw new InvalidOperationException($"Traffic item '{id}' is not paused.");
        await GetSession(item.PageId).SendAsync(method, JsonSerializer.Serialize(parameters), ct).ConfigureAwait(false);
        UpdateState(id, state);
    }

    private void UpdateState(string id, TrafficState state)
    {
        var item = _store.Get(id); if (item is not null) _store.Put(item with { State = state });
    }

    private ICdpSession GetSession(string pageId) => _registry.Get(pageId) ?? throw new InvalidOperationException($"No live CDP session for page '{pageId}'.");

    private static TrafficRuleAction ResolveRuleAction(TrafficRule rule, bool responseStage) =>
        rule.Pause ? TrafficRuleAction.Pause : rule.Fail ? TrafficRuleAction.Fail
        : responseStage && rule.ResponseEdit is not null ? TrafficRuleAction.FulfillResponse
        : !responseStage && rule.RequestEdit is not null ? TrafficRuleAction.EditRequest
        : TrafficRuleAction.None;

    private static TrafficRulePacketMetadata Metadata(TrafficMessage message)
    {
        var uri = Uri.TryCreate(message.Url, UriKind.Absolute, out var parsed) ? parsed : null;
        var headers = message.Stage == TrafficStage.Response ? message.ResponseHeaders : message.RequestHeaders;
        var body = message.Stage == TrafficStage.Response ? message.ResponseBody : message.RequestBody;
        return new TrafficRulePacketMetadata(message.Method, uri?.Scheme, uri?.Host,
            HashText(uri?.AbsolutePath ?? string.Empty), message.ResponseStatus, headers.Count, body?.LongLength ?? 0, message.State);
    }

    private static TrafficRulePacketMetadata PlannedMetadata(TrafficMessage message, TrafficRule rule, TrafficRuleAction action)
    {
        var before = Metadata(message);
        if (action == TrafficRuleAction.EditRequest && rule.RequestEdit is { } request)
        {
            var uri = Uri.TryCreate(request.Url ?? message.Url, UriKind.Absolute, out var parsed) ? parsed : null;
            return before with
            {
                Method = request.Method ?? message.Method, Scheme = uri?.Scheme, Host = uri?.Host,
                PathHash = HashText(uri?.AbsolutePath ?? string.Empty),
                HeaderCount = request.Headers?.Count ?? message.RequestHeaders.Count,
                BodyLength = request.Body?.LongLength ?? message.RequestBody?.LongLength ?? 0,
                State = TrafficState.Continued
            };
        }
        if (action == TrafficRuleAction.FulfillResponse && rule.ResponseEdit is { } response)
            return before with { Status = response.Status, HeaderCount = response.Headers?.Count ?? 0,
                BodyLength = response.Body?.LongLength ?? 0, State = TrafficState.Fulfilled };
        return action == TrafficRuleAction.Fail ? before with { State = TrafficState.Failed } : before;
    }

    private void PublishRuleExecution(TrafficRuleExecutionEvent value)
    {
        foreach (Action<TrafficRuleExecutionEvent> handler in RuleExecuted?.GetInvocationList() ?? [])
            try { handler(value); } catch (Exception ex) { _logger.Warn($"Rule execution observer failed: {ex.GetType().Name}"); }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private void EnsureModificationAllowed(bool modifying) { if (modifying && !ModificationsEnabled) throw new InvalidOperationException("Traffic modifications are disabled. Enable them explicitly first."); }
    private static string FetchId(string id) { var split = id.IndexOf(':'); return split < 0 ? id : id[(split + 1)..]; }
    private static object HeaderObject(TrafficHeader h) => new { name = h.Name, value = h.Value };

    private static Dictionary<string, object> BuildContinueRequestParameters(string requestId, TrafficRequestEdit? edit)
    {
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal) { ["requestId"] = requestId };
        if (edit is null) return parameters;
        if (edit.Url is not null) parameters["url"] = edit.Url;
        if (edit.Method is not null) parameters["method"] = edit.Method;
        if (edit.Body is not null) parameters["postData"] = Convert.ToBase64String(edit.Body);
        if (edit.Headers is not null) parameters["headers"] = edit.Headers.Select(HeaderObject).ToArray();
        return parameters;
    }

    private static Dictionary<string, object> BuildFulfillRequestParameters(string requestId, TrafficResponseEdit response)
    {
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["requestId"] = requestId,
            ["responseCode"] = response.Status
        };
        if (response.StatusText is not null) parameters["responsePhrase"] = response.StatusText;
        if (response.Headers is not null) parameters["responseHeaders"] = response.Headers.Select(HeaderObject).ToArray();
        if (response.Body is not null) parameters["body"] = Convert.ToBase64String(response.Body);
        return parameters;
    }
    private static TrafficHeader ReadHeader(JsonElement h) => new(h.GetProperty("name").GetString() ?? string.Empty, h.GetProperty("value").GetString() ?? string.Empty);
    private static TrafficHeader[] ReadRequestHeaders(JsonElement request) => request.GetProperty("headers").EnumerateObject().Select(x => new TrafficHeader(x.Name, x.Value.GetString() ?? x.Value.ToString())).ToArray();
    private static long? ReadContentLength(IReadOnlyList<TrafficHeader> headers)
    {
        var header = headers.LastOrDefault(item => item.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
        return header is not null && long.TryParse(header.Value, out var length) && length >= 0 ? length : null;
    }
    private static byte[]? ReadPostData(JsonElement request) => request.TryGetProperty("postData", out var p) ? Encoding.UTF8.GetBytes(p.GetString() ?? string.Empty) : null;
    private static JsonElement? ReadPath(string json, params string[] path) { using var d = JsonDocument.Parse(json); var e = d.RootElement; foreach (var p in path) if (!e.TryGetProperty(p, out e)) return null; return e.Clone(); }

    private static string BuildReplayScript(string method, string url, IReadOnlyList<TrafficHeader> headers, byte[]? body)
    {
        var normalizedHeaders = headers.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => string.Join(", ", x.Select(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        var init = JsonSerializer.Serialize(new { method, headers = normalizedHeaders });
        var target = JsonSerializer.Serialize(url);
        var bodyExpression = body is null ? "undefined" : $"Uint8Array.from(atob('{Convert.ToBase64String(body)}'),c=>c.charCodeAt(0))";
        return $"(async()=>{{const i={init};i.body={bodyExpression};const r=await fetch({target},i);const b=new Uint8Array(await r.arrayBuffer());let s='';for(let n=0;n<b.length;n+=32768)s+=String.fromCharCode(...b.subarray(n,n+32768));return {{status:r.status,statusText:r.statusText,headers:[...r.headers].map(x=>({{name:x[0],value:x[1]}})),body:btoa(s)}};}})()";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pageId in _contexts.Keys.ToArray()) try { await StopCaptureAsync(pageId).ConfigureAwait(false); } catch { }
    }

    private sealed record CaptureContext(ICdpSession Session, IDisposable Subscription);
}
