using Hackermes.Traffic.Models;
using Hackermes.Traffic.Persistence;
using Hackermes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Traffic.Repeater;

public interface IRepeaterService
{
    event Action<RepeaterChangedEvent>? Changed;
    string StorageFilePath { get; }
    IReadOnlyList<RepeaterDraft> GetAll();
    RepeaterDraft? Get(string id);
    RepeaterDraft CreateFromPacket(string packetId, string? name = null);
    RepeaterDraft Create(string name, string sourcePacketId, string pageId, RepeaterRequest request);
    RepeaterDraft Update(string id, RepeaterDraftUpdate update);
    RepeaterDraft Rename(string id, string name);
    bool Delete(string id);
    void ClearHistory(string id);
    void Reload();
    Task<RepeaterSendResult> SendAsync(
        string id,
        RepeaterSendOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>UI-independent, in-memory request repeater with immutable snapshots and per-draft send history.</summary>
public sealed class RepeaterService : IRepeaterService
{
    private const int MaxHistoryPerDraft = 200;
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly ITrafficStore _trafficStore;
    private readonly ITrafficService _trafficService;
    private readonly string _storageFilePath;
    private readonly Dictionary<string, RepeaterDraft> _drafts = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public RepeaterService(ITrafficStore trafficStore, ITrafficService trafficService)
        : this(trafficStore, trafficService, VersionedJsonFile.DefaultPath("repeater.json"))
    {
    }

    public RepeaterService(ITrafficStore trafficStore, ITrafficService trafficService, string storageFilePath)
    {
        _trafficStore = trafficStore ?? throw new ArgumentNullException(nameof(trafficStore));
        _trafficService = trafficService ?? throw new ArgumentNullException(nameof(trafficService));
        if (string.IsNullOrWhiteSpace(storageFilePath))
            throw new ArgumentException("Storage file path is required.", nameof(storageFilePath));
        _storageFilePath = System.IO.Path.GetFullPath(storageFilePath);
        Reload();
    }

    public event Action<RepeaterChangedEvent>? Changed;

    public string StorageFilePath => _storageFilePath;

    public IReadOnlyList<RepeaterDraft> GetAll()
    {
        lock (_gate)
            return _order.Select(id => Clone(_drafts[id])).ToArray();
    }

    public RepeaterDraft? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _drafts.TryGetValue(id, out var draft) ? Clone(draft) : null;
    }

    public RepeaterDraft CreateFromPacket(string packetId, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packetId);
        var packet = _trafficStore.Get(packetId)
            ?? throw new KeyNotFoundException($"Traffic item '{packetId}' was not found.");
        var displayName = string.IsNullOrWhiteSpace(name)
            ? $"{packet.Method} {GetDisplayTarget(packet.Url)}"
            : name.Trim();
        return Create(displayName, packet.Id, packet.PageId,
            new RepeaterRequest(packet.Method, packet.Url, packet.RequestHeaders, packet.RequestBody));
    }

    public RepeaterDraft Create(string name, string sourcePacketId, string pageId, RepeaterRequest request)
    {
        ValidateName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePacketId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ValidateRequest(request);

        var now = DateTimeOffset.UtcNow;
        var draft = new RepeaterDraft(Guid.NewGuid().ToString("N"), name.Trim(), sourcePacketId, pageId,
            Clone(request), now, now, 1, []);
        lock (_gate)
        {
            CommitLocked(() =>
            {
                _drafts.Add(draft.Id, draft);
                _order.Add(draft.Id);
            });
        }
        Publish("create", draft);
        return Clone(draft);
    }

    public RepeaterDraft Update(string id, RepeaterDraftUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(update);
        RepeaterDraft changed;
        lock (_gate)
        {
            var draft = GetRequired(id);
            var name = update.Name is null ? draft.Name : update.Name;
            ValidateName(name);
            var request = new RepeaterRequest(
                update.Method ?? draft.Request.Method,
                update.Url ?? draft.Request.Url,
                update.Headers ?? draft.Request.Headers,
                update.ReplaceBody ? update.Body : draft.Request.Body);
            ValidateRequest(request);
            changed = draft with
            {
                Name = name.Trim(),
                Request = Clone(request),
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = checked(draft.Revision + 1)
            };
            CommitLocked(() => _drafts[id] = changed);
        }
        Publish("update", changed);
        return Clone(changed);
    }

    public RepeaterDraft Rename(string id, string name) => Update(id, new RepeaterDraftUpdate(Name: name));

    public bool Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bool removed;
        lock (_gate)
        {
            removed = _drafts.ContainsKey(id);
            if (removed)
            {
                CommitLocked(() =>
                {
                    _drafts.Remove(id);
                    _order.Remove(id);
                });
            }
        }
        if (removed)
            Changed?.Invoke(new RepeaterChangedEvent("delete", id, null));
        return removed;
    }

    public void ClearHistory(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        RepeaterDraft changed;
        lock (_gate)
        {
            var draft = GetRequired(id);
            changed = draft with { History = [], UpdatedAt = DateTimeOffset.UtcNow };
            CommitLocked(() => _drafts[id] = changed);
        }
        Publish("clear-history", changed);
    }

    public async Task<RepeaterSendResult> SendAsync(
        string id,
        RepeaterSendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var sendOptions = ValidateSendOptions(options);
        RepeaterDraft draft;
        RepeaterDraft startedDraft;
        RepeaterSendResult pending;
        lock (_gate)
        {
            draft = GetRequired(id);
            var sequence = draft.History.Count == 0 ? 1 : checked(draft.History.Max(x => x.Sequence) + 1);
            pending = new RepeaterSendResult(Guid.NewGuid().ToString("N"), sequence, RepeaterSendStatus.Sending,
                Clone(draft.Request), DateTimeOffset.UtcNow, null, 0, MeasureRequest(draft.Request),
                null, null, [], null, 0, null);
            startedDraft = AppendHistory(draft, pending);
            CommitLocked(() => _drafts[id] = startedDraft);
        }
        Publish("send-started", startedDraft);

        using var timeout = new CancellationTokenSource(sendOptions.Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopwatch = Stopwatch.StartNew();
        RepeaterSendResult completed;
        try
        {
            var edit = new TrafficRequestEdit(draft.Request.Url, draft.Request.Method,
                draft.Request.Headers, draft.Request.Body);
            var replay = _trafficService.ReplayAsync(draft.SourcePacketId, edit, operation.Token);
            var response = await replay.WaitAsync(operation.Token).ConfigureAwait(false);
            stopwatch.Stop();
            completed = pending with
            {
                Status = RepeaterSendStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ResponseStatus = response.Status,
                ResponseStatusText = response.StatusText,
                ResponseHeaders = response.Headers.ToArray(),
                ResponseBody = response.Body.ToArray(),
                ResponseSize = MeasureResponse(response)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            completed = Failure(pending, RepeaterSendStatus.Cancelled, stopwatch.ElapsedMilliseconds, "The send was cancelled.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            stopwatch.Stop();
            completed = Failure(pending, RepeaterSendStatus.TimedOut, stopwatch.ElapsedMilliseconds,
                $"The send timed out after {sendOptions.Timeout.TotalSeconds:0.###} seconds.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            completed = Failure(pending, RepeaterSendStatus.Failed, stopwatch.ElapsedMilliseconds, ex.Message);
        }

        RepeaterDraft finalDraft;
        lock (_gate)
        {
            if (!_drafts.TryGetValue(id, out var current))
                return Clone(completed);
            var history = current.History.Select(item => item.Id == pending.Id ? completed : item).ToArray();
            finalDraft = current with { History = history, UpdatedAt = DateTimeOffset.UtcNow };
            CommitLocked(() => _drafts[id] = finalDraft);
        }
        Publish("send-completed", finalDraft);
        return Clone(completed);
    }

    public void Reload()
    {
        RepeaterDraft[] loaded;
        lock (_gate)
        {
            var document = VersionedJsonFile.ReadWithBackup<RepeaterDocument>(_storageFilePath,
                IsValidDocument);
            loaded = document is null ? [] : Normalize(document.Drafts);
            _drafts.Clear();
            _order.Clear();
            foreach (var draft in loaded)
            {
                _drafts.Add(draft.Id, draft);
                _order.Add(draft.Id);
            }
        }
        foreach (var draft in loaded)
            Publish("reload", draft);
    }

    private void PersistLocked() => VersionedJsonFile.Write(_storageFilePath,
        new RepeaterDocument(SchemaVersion, _order.Select(id => _drafts[id]).ToArray()),
        IsValidDocument);

    private void CommitLocked(Action mutation)
    {
        var previousDrafts = new Dictionary<string, RepeaterDraft>(_drafts, StringComparer.Ordinal);
        var previousOrder = _order.ToArray();
        mutation();
        try
        {
            PersistLocked();
        }
        catch
        {
            _drafts.Clear();
            foreach (var pair in previousDrafts)
                _drafts.Add(pair.Key, pair.Value);
            _order.Clear();
            _order.AddRange(previousOrder);
            throw;
        }
    }

    private static RepeaterDraft[] Normalize(IReadOnlyList<RepeaterDraft>? drafts)
    {
        if (drafts is null)
            return [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<RepeaterDraft>();
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Id) || string.IsNullOrWhiteSpace(draft.Name) ||
                string.IsNullOrWhiteSpace(draft.SourcePacketId) || string.IsNullOrWhiteSpace(draft.PageId) ||
                draft.Request is null || !ids.Add(draft.Id))
                continue;
            try { ValidateRequest(draft.Request); }
            catch (ArgumentException) { continue; }
            var now = DateTimeOffset.UtcNow;
            var history = (draft.History ?? []).TakeLast(MaxHistoryPerDraft).Select(item =>
                item.Status == RepeaterSendStatus.Sending
                    ? item with
                    {
                        Status = RepeaterSendStatus.Failed,
                        CompletedAt = now,
                        Error = "Send interrupted by application restart."
                    }
                    : item).ToArray();
            normalized.Add(draft with { History = history });
        }
        return normalized.ToArray();
    }

    private static bool IsValidDocument(RepeaterDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Drafts is null)
            return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var draft in document.Drafts)
        {
            if (draft is null || string.IsNullOrWhiteSpace(draft.Id) || string.IsNullOrWhiteSpace(draft.Name) ||
                string.IsNullOrWhiteSpace(draft.SourcePacketId) || string.IsNullOrWhiteSpace(draft.PageId) ||
                draft.Request is null || draft.History is null || !ids.Add(draft.Id))
                return false;
            try { ValidateRequest(draft.Request); }
            catch (ArgumentException) { return false; }
            foreach (var item in draft.History)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Request is null ||
                    item.ResponseHeaders is null || item.Sequence < 1)
                    return false;
                try { ValidateRequest(item.Request); }
                catch (ArgumentException) { return false; }
            }
        }
        return true;
    }

    private RepeaterDraft AppendHistory(RepeaterDraft draft, RepeaterSendResult item)
    {
        var history = draft.History.Append(item).TakeLast(MaxHistoryPerDraft).ToArray();
        return draft with { History = history, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private RepeaterDraft GetRequired(string id) => _drafts.TryGetValue(id, out var draft)
        ? draft
        : throw new KeyNotFoundException($"Repeater draft '{id}' was not found.");

    private void Publish(string operation, RepeaterDraft draft) =>
        Changed?.Invoke(new RepeaterChangedEvent(operation, draft.Id, Clone(draft)));

    private static RepeaterSendResult Failure(RepeaterSendResult source, RepeaterSendStatus status, long duration, string error) =>
        source with { Status = status, CompletedAt = DateTimeOffset.UtcNow, DurationMilliseconds = duration, Error = error };

    private static int MeasureRequest(RepeaterRequest request) =>
        MeasureStartAndHeaders($"{request.Method} {request.Url}", request.Headers) + (request.Body?.Length ?? 0);

    private static int MeasureResponse(TrafficReplayResult response) =>
        MeasureStartAndHeaders($"HTTP {response.Status} {response.StatusText}", response.Headers) + response.Body.Length;

    private static int MeasureStartAndHeaders(string startLine, IReadOnlyList<TrafficHeader> headers) =>
        Encoding.UTF8.GetByteCount(startLine) + 2 + headers.Sum(header => Encoding.UTF8.GetByteCount(header.Name) + 2 + Encoding.UTF8.GetByteCount(header.Value) + 2) + 2;

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Draft name is required.", nameof(name));
        if (name.Trim().Length > 200)
            throw new ArgumentException("Draft name cannot exceed 200 characters.", nameof(name));
    }

    private static void ValidateRequest(RepeaterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Method))
            throw new ArgumentException("HTTP method is required.", nameof(request));
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("An absolute HTTP or HTTPS URL is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Headers);
        if (request.Headers.Any(header => string.IsNullOrWhiteSpace(header.Name) || header.Name.ContainsAny('\r', '\n') || header.Value.ContainsAny('\r', '\n')))
            throw new ArgumentException("Request headers contain an invalid name or line break.", nameof(request));
    }

    private static RepeaterSendOptions ValidateSendOptions(RepeaterSendOptions? options)
    {
        var value = options ?? RepeaterSendOptions.Default;
        if (value.Timeout < RepeaterSendOptions.MinimumTimeout || value.Timeout > RepeaterSendOptions.MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), value.Timeout,
                $"Repeater timeout must be between {RepeaterSendOptions.MinimumTimeout.TotalSeconds:0.###} and {RepeaterSendOptions.MaximumTimeout.TotalSeconds:0.###} seconds.");
        return value;
    }

    private static string GetDisplayTarget(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? uri.PathAndQuery
        : url;

    private static RepeaterRequest Clone(RepeaterRequest request) =>
        request with { Headers = request.Headers.ToArray(), Body = request.Body?.ToArray() };

    private static RepeaterSendResult Clone(RepeaterSendResult result) => result with
    {
        Request = Clone(result.Request),
        ResponseHeaders = result.ResponseHeaders.ToArray(),
        ResponseBody = result.ResponseBody?.ToArray()
    };

    private static RepeaterDraft Clone(RepeaterDraft draft) => draft with
    {
        Request = Clone(draft.Request),
        History = draft.History.Select(Clone).ToArray()
    };

    private sealed record RepeaterDocument(int SchemaVersion, IReadOnlyList<RepeaterDraft>? Drafts);
}
