using Hookmes.Automation.Packet;
using Hookmes.Base.Events;
using Hookmes.Cdp.Session;
using Hookmes.Inspector.ViewModels;
using Hookmes.Platform.Events;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Rules;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Services;
using Hookmes.Traffic.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

/// <summary>让人工工作台、CLI 和 AI 共用同一个 Traffic 核心。</summary>
public sealed class TrafficIntegrationService :
    IPacketCommandService, IPacketInterceptionModeService, IPacketArchiveService, IPacketBodyReadService, IPacketBodyEditService,
    ITrafficWorkbenchService, ITrafficRuleWorkbenchService,
    IRepeaterWorkbenchService, IDisposable
{
    private readonly ITrafficService _traffic;
    private readonly ITrafficStore _store;
    private readonly ICdpSessionRegistry _sessions;
    private readonly ITrafficRuleManager _rules;
    private readonly IRepeaterService _repeater;
    private readonly ITrafficAnnotationService _annotations;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _binaryEdited = new(StringComparer.Ordinal);
    private readonly IDisposable _activeSubscription;
    private string? _activePageId;
    private bool _intercept;
    private bool _responseIntercept;

    public TrafficIntegrationService(
        ITrafficService traffic, ITrafficStore store, ICdpSessionRegistry sessions,
        ITrafficRuleManager rules, IRepeaterService repeater,
        ITrafficAnnotationService annotations, IEventBus eventBus)
    {
        _traffic = traffic;
        _store = store;
        _sessions = sessions;
        _rules = rules;
        _repeater = repeater;
        _annotations = annotations;
        _store.Changed += OnStoreChanged;
        _sessions.SessionOpened += OnSessionOpened;
        _sessions.SessionClosed += OnSessionClosed;
        _rules.Changed += OnRulesChanged;
        _repeater.Changed += OnRepeaterChanged;
        _annotations.Changed += OnAnnotationChanged;
        _activeSubscription = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(e =>
            _activePageId = e.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal) ? id : null);
        UpdateModificationGate();
        foreach (var session in sessions.All) _ = StartForPageAsync(session.PageId);
    }

    public event Action? Changed;
    public event Action? RulesChanged;
    public event Action? RepeaterChanged;

    public IReadOnlyList<TrafficExchange> Exchanges => _store.Read(5000)
        .Select(ToExchange).ToArray();

    public TrafficExchangePage Query(TrafficExchangeFilter filter)
    {
        var result = _store.Query(new TrafficQuery(
            null, filter.Text, filter.Method, filter.Status, filter.ResourceType,
            filter.OnlyIntercepted ? TrafficState.Paused : null,
            Offset: filter.Offset, Limit: filter.Limit));
        return new TrafficExchangePage(result.Items.Select(ToExchange).ToArray(), result.Total, result.Offset, result.Limit);
    }

    public IReadOnlyList<TrafficRuleItem> Rules => _rules.GetAll().Select(rule => new TrafficRuleItem(
        rule.Id, rule.UrlPattern, rule.Method ?? "*",
        rule.Stage?.ToString().ToLowerInvariant() ?? "any",
        rule.Fail ? "drop" : rule.Pause ? "pause" : rule.ResponseEdit is not null ? "fulfill" : "edit",
        rule.Enabled)).ToArray();

    public IReadOnlyList<RepeaterDraftItem> Drafts => _repeater.GetAll().Select(ToRepeaterItem).ToArray();

    public Task AddRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(draft.Id)) throw new ArgumentException("Rule id is required.");
        if (!Enum.TryParse<TrafficStage>(draft.Stage, true, out var stage))
            throw new ArgumentException("Stage must be request or response.");
        var behavior = draft.Behavior.Trim().ToLowerInvariant();
        if (behavior is not ("pause" or "drop")) throw new ArgumentException("Behavior must be pause or drop.");
        _rules.Add(new TrafficRule(draft.Id.Trim(), draft.UrlPattern.Trim(),
            string.IsNullOrWhiteSpace(draft.Method) || draft.Method == "*" ? null : draft.Method.Trim(),
            stage, Fail: behavior == "drop", Pause: behavior == "pause"));
        return Task.CompletedTask;
    }

    public Task SetRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rules.SetEnabled(id, enabled);
        return Task.CompletedTask;
    }

    public Task RemoveRuleAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rules.Remove(id);
        return Task.CompletedTask;
    }

    public Task MoveRuleAsync(string id, int targetIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rules.Move(id, targetIndex);
        return Task.CompletedTask;
    }

    public async Task ExportRulesFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllTextAsync(fullPath, _rules.ExportJson(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ImportRulesFileAsync(string path, bool merge, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await System.IO.File.ReadAllTextAsync(System.IO.Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        return _rules.ImportJson(json, merge ? TrafficRuleImportMode.Merge : TrafficRuleImportMode.Replace);
    }

    public Task<string> CreateRepeaterAsync(string exchangeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repeater.CreateFromPacket(exchangeId).Id);
    }

    public async Task<RepeaterDraftItem> SendAsync(
        string id, string name, string request, CancellationToken cancellationToken)
    {
        var draft = _repeater.Get(id) ?? throw new KeyNotFoundException($"Repeater draft '{id}' was not found.");
        var source = Required(draft.SourcePacketId);
        var edit = ToRequestEdit(HttpPacketCodec.Parse(request), source);
        _repeater.Update(id, new RepeaterDraftUpdate(name, edit.Method, edit.Url, edit.Headers, edit.Body, true));
        await _repeater.SendAsync(id, cancellationToken).ConfigureAwait(false);
        return ToRepeaterItem(_repeater.Get(id)!);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _repeater.Delete(id);
        return Task.CompletedTask;
    }

    public Task ClearHistoryAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _repeater.ClearHistory(id);
        return Task.CompletedTask;
    }

    public bool IsInterceptEnabled
    {
        get => _intercept;
        set
        {
            if (_intercept == value) return;
            _intercept = value;
            UpdateModificationGate();
            _ = RestartAllAsync();
            Changed?.Invoke();
        }
    }

    public bool IsResponseInterceptEnabled
    {
        get => _responseIntercept;
        set
        {
            if (_responseIntercept == value) return;
            _responseIntercept = value;
            UpdateModificationGate();
            _ = RestartAllAsync();
            Changed?.Invoke();
        }
    }

    public PacketInterceptionMode InterceptionMode => (_intercept, _responseIntercept) switch
    {
        (true, true) => PacketInterceptionMode.Both,
        (true, false) => PacketInterceptionMode.Request,
        (false, true) => PacketInterceptionMode.Response,
        _ => PacketInterceptionMode.Off
    };

    public async Task SetInterceptionModeAsync(PacketInterceptionMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _intercept = mode is PacketInterceptionMode.Request or PacketInterceptionMode.Both;
        _responseIntercept = mode is PacketInterceptionMode.Response or PacketInterceptionMode.Both;
        UpdateModificationGate();
        await RestartAllAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
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

    public Task<IReadOnlyList<PacketArchiveEntry>> ExportArchiveAsync(
        string? filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<TrafficMessage> items = _store.Read(5000, _activePageId);
        if (!string.IsNullOrWhiteSpace(filter))
            items = items.Where(item => item.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Method.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<PacketArchiveEntry>>(items.Select(item =>
            new PacketArchiveEntry(item.Id, item.CapturedAt, FormatArchiveRequest(item),
                item.ResponseStatus is null ? null : FormatResponse(item),
                ToArchiveBody(item.RequestBody, item.RequestHeaders),
                ToArchiveBody(item.ResponseBody, item.ResponseHeaders))).ToArray());
    }

    public Task<int> ImportArchiveAsync(
        IReadOnlyList<PacketArchiveEntry> entries, CancellationToken cancellationToken)
    {
        var imported = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = HttpPacketCodec.Parse(entry.Request);
            if (request.Kind != HttpPacketKind.Request) continue;
            var response = entry.Response is null ? null : HttpPacketCodec.Parse(entry.Response);
            var url = ResolveArchiveUrl(request);
            var id = $"archive:{Guid.NewGuid():N}";
            _store.Import(new TrafficMessage(id, "archive", response is null ? TrafficStage.Request : TrafficStage.Response,
                TrafficState.Continued, request.Method ?? "GET", url,
                request.Headers.Select(h => new TrafficHeader(h.Name, h.Value)).ToArray(),
                entry.RequestBody?.GetBytes() ?? Encoding.UTF8.GetBytes(request.Body),
                response?.StatusCode, response?.ReasonPhrase,
                response?.Headers.Select(h => new TrafficHeader(h.Name, h.Value)).ToArray() ?? [],
                response is null ? null : entry.ResponseBody?.GetBytes() ?? Encoding.UTF8.GetBytes(response.Body),
                "Archive", entry.CapturedAt));
            imported++;
        }
        return Task.FromResult(imported);
    }

    public async Task<int> ExportArchiveFileAsync(
        string path, string? filter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = await ExportArchiveAsync(filter, cancellationToken).ConfigureAwait(false);
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllTextAsync(fullPath,
            PacketArchiveCodec.Serialize(entries, PacketArchiveCodec.DetectFormat(fullPath)), cancellationToken).ConfigureAwait(false);
        return entries.Count;
    }

    public async Task<int> ImportArchiveFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var entries = PacketArchiveCodec.Deserialize(
            await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
            PacketArchiveCodec.DetectFormat(fullPath));
        return await ImportArchiveAsync(entries, cancellationToken).ConfigureAwait(false);
    }

    public Task<PacketBodyDescriptor> DescribeBodyAsync(
        string id, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (body, contentType, charset) = GetBody(id, side);
        return Task.FromResult(PacketBodyChunker.Describe(body, contentType, charset));
    }

    public Task<PacketBodyChunk> ReadBodyChunkAsync(
        string id, string side, long offset, int count,
        PacketBodyChunkEncoding preferredEncoding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (body, _, _) = GetBody(id, side);
        return Task.FromResult(PacketBodyChunker.Read(body, offset, count, preferredEncoding));
    }

    public Task<PacketBodyDescriptor> EditBodyAsync(
        string id, string side, BinaryBodyEdit edit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = Required(id);
        var response = side.Equals("response", StringComparison.OrdinalIgnoreCase);
        if (!response && !side.Equals("request", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Side must be request or response.");
        var source = (response ? item.ResponseBody : item.RequestBody) ?? [];
        var edited = BinaryBodyEditor.Apply(source, edit);
        var updated = response
            ? item with { ResponseBody = edited, ResponseHeaders = NormalizeContentLength(item.ResponseHeaders, edited.LongLength) }
            : item with { RequestBody = edited, RequestHeaders = NormalizeContentLength(item.RequestHeaders, edited.LongLength) };
        _store.Import(updated);
        _binaryEdited[id + ":" + side.ToLowerInvariant()] = 0;
        var (_, contentType, charset) = GetBody(id, side);
        return Task.FromResult(PacketBodyChunker.Describe(edited, contentType, charset));
    }

    public async Task<string> EditBinaryBodyAsync(
        string exchangeId, string side, string kind, long offset, long count,
        string data, string encoding, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BinaryEditKind>(kind, true, out var editKind))
            throw new ArgumentException("Kind must be replace, insert or delete.");
        if (!Enum.TryParse<BinaryTextEncoding>(encoding, true, out var dataEncoding))
            throw new ArgumentException("Encoding must be hex or base64.");
        var result = await EditBodyAsync(exchangeId, side,
            new BinaryBodyEdit(editKind, offset, count, editKind == BinaryEditKind.Delete ? null : data, dataEncoding),
            cancellationToken).ConfigureAwait(false);
        return $"Binary body updated: {result.Length} bytes · sha256 {result.Sha256}";
    }

    public async Task<string> ReadBinaryBodyAsync(
        string exchangeId, string side, long offset, int count, string encoding,
        CancellationToken cancellationToken)
    {
        var chunk = await ReadBodyChunkAsync(exchangeId, side, offset, count,
            PacketBodyChunkEncoding.Base64, cancellationToken).ConfigureAwait(false);
        var bytes = PacketBodyChunker.Decode(chunk);
        return encoding.Equals("hex", StringComparison.OrdinalIgnoreCase)
            ? BinaryBodyCodec.Format(bytes, BinaryTextEncoding.Hex)
            : BinaryBodyCodec.Format(bytes, BinaryTextEncoding.Base64);
    }

    public IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket) =>
        HttpPacketParameters.Read(HttpPacketCodec.Parse(rawPacket))
            .Select(item => new TrafficParameterItem(item.Location.ToString().ToLowerInvariant(),
                item.Name, item.Value, item.Occurrence)).ToArray();

    public string SetParameter(string rawPacket, string location, string name, int occurrence, string value)
    {
        if (!Enum.TryParse<HttpParameterLocation>(location, true, out var parsed))
            throw new ArgumentException("Location must be query, form or json.");
        var updated = HttpPacketParameters.Set(HttpPacketCodec.Parse(rawPacket), parsed, name, occurrence, value);
        return HttpPacketCodec.Format(updated, false);
    }

    public TrafficAnnotationItem? GetAnnotation(string exchangeId) =>
        _annotations.Get(exchangeId) is { } value ? ToAnnotationItem(value) : null;

    public Task<TrafficAnnotationItem> SaveAnnotationAsync(string exchangeId, bool starred, string tags,
        string note, string status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.TryParse<TrafficReviewStatus>(status, true, out var reviewStatus))
            throw new ArgumentException("Status must be Unreviewed, InReview, Resolved or Ignored.");
        var values = tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var changed = _annotations.Update(exchangeId,
            new TrafficAnnotationUpdate(starred, values, note, ReplaceNote: true, reviewStatus));
        return Task.FromResult(ToAnnotationItem(changed));
    }

    public async Task ReplayAsync(string id, CancellationToken cancellationToken) =>
        _ = await _traffic.ReplayAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken)
    {
        _intercept = enabled;
        UpdateModificationGate();
        await RestartAllAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task ContinueAsync(string id, CancellationToken cancellationToken)
    {
        var source = Required(id);
        var side = source.Stage == TrafficStage.Response ? "response" : "request";
        var key = id + ":" + side;
        if (!_binaryEdited.ContainsKey(key))
        {
            await _traffic.ContinueAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (source.Stage == TrafficStage.Response)
            await _traffic.FulfillAsync(id, new TrafficResponseEdit(source.ResponseStatus ?? 200,
                source.ResponseStatusText, source.ResponseHeaders, source.ResponseBody), cancellationToken).ConfigureAwait(false);
        else
            await _traffic.ContinueAsync(id, new TrafficRequestEdit(source.Url, source.Method,
                source.RequestHeaders, source.RequestBody), cancellationToken).ConfigureAwait(false);
        _binaryEdited.TryRemove(key, out _);
    }

    public Task DropAsync(string id, CancellationToken cancellationToken) =>
        _traffic.FailAsync(id, cancellationToken: cancellationToken);

    public async Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken)
    {
        var source = Required(id);
        var packet = HttpPacketCodec.Parse(rawPacket);
        if (side.Equals("response", StringComparison.OrdinalIgnoreCase))
            await _traffic.FulfillAsync(id, ToResponseEdit(packet), cancellationToken).ConfigureAwait(false);
        else if (side.Equals("request", StringComparison.OrdinalIgnoreCase))
            await _traffic.ContinueAsync(id, ToRequestEdit(packet, source), cancellationToken).ConfigureAwait(false);
        else
            throw new ArgumentException("Side must be request or response.");
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
        var edit = ToRequestEdit(HttpPacketCodec.Parse(request), source);
        if (_binaryEdited.ContainsKey(exchangeId + ":request"))
            edit = edit with
            {
                Headers = NormalizeContentLength(edit.Headers ?? [], source.RequestBody?.LongLength ?? 0),
                Body = source.RequestBody
            };
        var result = await _traffic.ReplayAsync(exchangeId, edit, cancellationToken).ConfigureAwait(false);
        var response = FormatResponse(result);
        return new TrafficOperationResult(true, $"重放完成: HTTP {result.Status}", response);
    }

    public async Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken)
    {
        var source = Required(exchangeId);
        if (source.Stage == TrafficStage.Response)
        {
            await ContinueAsync(exchangeId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var edit = ToRequestEdit(HttpPacketCodec.Parse(request), source);
        var key = exchangeId + ":request";
        if (_binaryEdited.ContainsKey(key)) edit = edit with
        {
            Headers = NormalizeContentLength(edit.Headers ?? [], source.RequestBody?.LongLength ?? 0),
            Body = source.RequestBody
        };
        await _traffic.ContinueAsync(exchangeId, edit, cancellationToken).ConfigureAwait(false);
        _binaryEdited.TryRemove(key, out _);
    }

    public async Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken)
    {
        var edit = ToResponseEdit(HttpPacketCodec.Parse(response));
        var key = exchangeId + ":response";
        if (_binaryEdited.ContainsKey(key))
        {
            var source = Required(exchangeId);
            edit = edit with
            {
                Headers = NormalizeContentLength(edit.Headers ?? [], source.ResponseBody?.LongLength ?? 0),
                Body = source.ResponseBody
            };
        }
        await _traffic.FulfillAsync(exchangeId, edit, cancellationToken).ConfigureAwait(false);
        _binaryEdited.TryRemove(key, out _);
    }

    private void OnStoreChanged(TrafficMessage _) => Changed?.Invoke();

    private static IReadOnlyList<TrafficHeader> NormalizeContentLength(
        IReadOnlyList<TrafficHeader> headers, long bodyLength)
    {
        var normalized = new List<TrafficHeader>(headers.Count + 1);
        var inserted = false;
        foreach (var header in headers)
        {
            if (header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(header);
                continue;
            }

            if (inserted) continue;
            normalized.Add(new TrafficHeader("Content-Length", bodyLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            inserted = true;
        }

        if (!inserted)
            normalized.Add(new TrafficHeader("Content-Length", bodyLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return normalized;
    }

    private void OnRulesChanged(TrafficRuleChange _)
    {
        UpdateModificationGate();
        RulesChanged?.Invoke();
    }
    private void OnRepeaterChanged(RepeaterChangedEvent _) => RepeaterChanged?.Invoke();
    private void OnAnnotationChanged(TrafficAnnotationChanged _) => Changed?.Invoke();
    private void OnSessionOpened(ICdpSession session) => _ = StartForPageAsync(session.PageId);
    private void OnSessionClosed(string pageId) => _ = _traffic.StopCaptureAsync(pageId);

    private async Task StartForPageAsync(string pageId, CancellationToken ct = default)
    {
        try
        {
            await _traffic.StartCaptureAsync(pageId,
                new TrafficCaptureOptions(PauseRequests: _intercept, PauseResponses: _responseIntercept, CaptureResponseBodies: true), ct)
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
                _store.MarkPausedContinued(session.PageId);
                await StartForPageAsync(session.PageId, ct).ConfigureAwait(false);
            }
        }
        finally { _captureGate.Release(); }
    }

    private TrafficMessage Required(string id) => _store.Get(id)
        ?? throw new KeyNotFoundException($"数据包不存在: {id}");

    private static TrafficExchange ToExchange(TrafficMessage item) => new(
        item.Id, item.CapturedAt, item.Method, item.Url, item.ResponseStatus,
        FormatRequest(item), FormatResponse(item), item.State == TrafficState.Paused,
        item.Stage == TrafficStage.Response);

    private static RepeaterDraftItem ToRepeaterItem(RepeaterDraft draft)
    {
        var request = HttpPacketCodec.Format(new HttpPacket
        {
            Kind = HttpPacketKind.Request, ProtocolVersion = "HTTP/1.1", Method = draft.Request.Method,
            Target = draft.Request.Url, Headers = draft.Request.Headers.Select(h => new HttpHeader(h.Name, h.Value)).ToArray(),
            Body = DecodeBody(draft.Request.Body)
        });
        var latest = draft.History.LastOrDefault();
        var response = latest?.ResponseStatus is null ? string.Empty : HttpPacketCodec.Format(new HttpPacket
        {
            Kind = HttpPacketKind.Response, ProtocolVersion = "HTTP/1.1", StatusCode = latest.ResponseStatus,
            ReasonPhrase = latest.ResponseStatusText, Headers = latest.ResponseHeaders.Select(h => new HttpHeader(h.Name, h.Value)).ToArray(),
            Body = DecodeBody(latest.ResponseBody)
        });
        var metrics = latest is null ? "Not sent" :
            $"{latest.DurationMilliseconds} ms · {latest.RequestSize} B → {latest.ResponseSize} B";
        return new RepeaterDraftItem(draft.Id, draft.Name, request, draft.Revision, draft.History.Count,
            latest?.Status.ToString() ?? "Draft", metrics, response);
    }

    private void UpdateModificationGate() =>
        _traffic.SetModificationsEnabled(_intercept || _responseIntercept || _rules.GetAll().Any(rule => rule.Enabled));

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

    private static string FormatArchiveRequest(TrafficMessage item)
    {
        var packet = HttpPacketCodec.Parse(FormatRequest(item));
        return HttpPacketCodec.Format(packet with { Target = item.Url }, false);
    }

    private static string ResolveArchiveUrl(HttpPacket request)
    {
        if (Uri.TryCreate(request.Target, UriKind.Absolute, out var absolute)) return absolute.ToString();
        var host = request.HeaderValues("Host").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(host)) return request.Target ?? "/";
        return new Uri(new Uri("https://" + host), request.Target ?? "/").ToString();
    }

    private static PacketBody? ToArchiveBody(byte[]? body, IReadOnlyList<TrafficHeader> headers)
    {
        if (body is null) return null;
        var contentType = headers.FirstOrDefault(header =>
            header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        return PacketBody.FromBytes(body, contentType);
    }

    private (byte[] Body, string? ContentType, string? Charset) GetBody(string id, string side)
    {
        var item = Required(id);
        var response = side.Equals("response", StringComparison.OrdinalIgnoreCase);
        if (!response && !side.Equals("request", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Side must be request or response.");
        var body = (response ? item.ResponseBody : item.RequestBody) ?? [];
        var headers = response ? item.ResponseHeaders : item.RequestHeaders;
        var contentType = headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        string? charset = null;
        if (contentType is not null)
        {
            var marker = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) charset = contentType[(marker + 8)..].Split(';')[0].Trim().Trim('"');
        }
        return (body, contentType, charset);
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
        var body = Encoding.UTF8.GetBytes(packet.Body);
        return new TrafficRequestEdit(url, packet.Method, NormalizeContentLength(
            packet.Headers.Select(h => new TrafficHeader(h.Name, h.Value)).ToArray(), body.LongLength), body);
    }

    private static TrafficResponseEdit ToResponseEdit(HttpPacket packet)
    {
        if (packet.Kind != HttpPacketKind.Response) throw new HttpPacketParseException("需要 HTTP 响应。");
        var body = Encoding.UTF8.GetBytes(packet.Body);
        return new TrafficResponseEdit(packet.StatusCode ?? 200, packet.ReasonPhrase, NormalizeContentLength(
            packet.Headers.Select(h => new TrafficHeader(h.Name, h.Value)).ToArray(), body.LongLength), body);
    }

    private static string DecodeBody(byte[]? body) => body is null ? string.Empty : Encoding.UTF8.GetString(body);

    private static TrafficAnnotationItem ToAnnotationItem(TrafficAnnotation value) => new(
        value.Starred, string.Join(", ", value.Tags), value.Note ?? string.Empty,
        value.Status.ToString(), value.Revision);

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _sessions.SessionOpened -= OnSessionOpened;
        _sessions.SessionClosed -= OnSessionClosed;
        _rules.Changed -= OnRulesChanged;
        _repeater.Changed -= OnRepeaterChanged;
        _annotations.Changed -= OnAnnotationChanged;
        _activeSubscription.Dispose();
        _captureGate.Dispose();
    }
}
