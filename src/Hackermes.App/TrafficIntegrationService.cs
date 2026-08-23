using Hackermes.Automation.Packet;
using Hackermes.Base.Cryptography;
using Hackermes.Base.Events;
using Hackermes.Cdp.Session;
using Hackermes.Inspector.ViewModels;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using Hackermes.Traffic.Repeater;
using Hackermes.Traffic.Services;
using Hackermes.Traffic.Annotations;
using Hackermes.Traffic.Comparison;
using Hackermes.Traffic.History;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>让人工工作台、CLI 和 AI 共用同一个 Traffic 核心。</summary>
public sealed class TrafficIntegrationService :
    IPacketCommandService, IPacketQueryService, IPacketInterceptionModeService, IPacketArchiveService, IPacketBodyReadService, IPacketBodyEditService, IPacketEditDraftService, IPacketAuditQueryService, IPacketAuditExportService, IPacketCommitService,
    ITrafficWorkbenchService, ITrafficRuleWorkbenchService,
    IRepeaterWorkbenchService, IDisposable
{
    private readonly ITrafficService _traffic;
    private readonly ITrafficStore _store;
    private readonly ICdpSessionRegistry _sessions;
    private readonly ITrafficRuleManager _rules;
    private readonly IRepeaterService _repeater;
    private readonly ITrafficComparisonService _comparisons;
    private readonly ITrafficAnnotationService _annotations;
    private readonly IPacketAuditTrail _audit;
    private readonly IPacketAuditExportService _auditExports;
    private readonly ITrafficHistoryManagementService _history;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EditDraftState> _editDrafts = new(StringComparer.Ordinal);
    private readonly IDisposable _activeSubscription;
    private string? _activePageId;
    private bool _intercept;
    private bool _responseIntercept;

    public TrafficIntegrationService(
        ITrafficService traffic, ITrafficStore store, ICdpSessionRegistry sessions,
        ITrafficRuleManager rules, IRepeaterService repeater, ITrafficComparisonService comparisons,
        ITrafficAnnotationService annotations, IPacketAuditTrail audit, IPacketAuditExportService auditExports,
        ITrafficHistoryManagementService history, ISettingsService settings, IEventBus eventBus)
    {
        _traffic = traffic;
        _store = store;
        _sessions = sessions;
        _rules = rules;
        _repeater = repeater;
        _comparisons = comparisons;
        _annotations = annotations;
        _audit = audit;
        _auditExports = auditExports;
        _history = history;
        _settings = settings;
        _store.Changed += OnStoreChanged;
        _sessions.SessionOpened += OnSessionOpened;
        _sessions.SessionClosed += OnSessionClosed;
        _rules.Changed += OnRulesChanged;
        _repeater.Changed += OnRepeaterChanged;
        _annotations.Changed += OnAnnotationChanged;
        _history.Changed += OnHistoryChanged;
        _activeSubscription = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(e =>
            _activePageId = e.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal) ? id : null);
        UpdateModificationGate();
        foreach (var session in sessions.All) _ = StartForPageAsync(session.PageId);
    }

    public event Action? Changed;
    public event Action? RulesChanged;
    public event Action? RepeaterChanged;
    public event Action? HistoryPolicyChanged;

    /// <summary>
    /// Raised by the workspace policy isolation module after it switched the policy
    /// file and applied it, so workbench views can refresh history statistics without
    /// a manual refresh. Must be called on the UI thread (workspace events are).
    /// </summary>
    public void NotifyHistoryPolicyChanged() => HistoryPolicyChanged?.Invoke();

    public IReadOnlyList<PacketAuditEntry> QueryAudit(PacketAuditQuery query) => _audit.Query(query);
    public string Export(PacketAuditQuery query) => _auditExports.Export(query);
    public PacketAuditVerification Verify(string content, string? expectedKeyId = null) =>
        _auditExports.Verify(content, expectedKeyId);

    public Task<PacketCommitResult> CommitContinueAsync(string id, CancellationToken cancellationToken) =>
        CaptureCommitAsync(id, null, "Continue", () => ContinueAsync(id, cancellationToken), cancellationToken);

    public Task<PacketCommitResult> CommitDropAsync(string id, CancellationToken cancellationToken) =>
        CaptureCommitAsync(id, null, "Drop", () => DropAsync(id, cancellationToken), cancellationToken);

    public Task<PacketCommitResult> CommitEditAsync(
        string id, string side, string rawPacket, CancellationToken cancellationToken) =>
        CaptureCommitAsync(id, side, "Edit", () => EditAsync(id, side, rawPacket, cancellationToken), cancellationToken);

    public Task<PacketCommitResult> CommitDiscardAsync(
        string id, string side, CancellationToken cancellationToken) =>
        CaptureCommitAsync(id, side, "Discard", async () =>
        {
            if (!await DiscardPendingEditAsync(id, side, cancellationToken).ConfigureAwait(false))
            {
                var missing = new InvalidOperationException("Pending packet edit was not found.");
                var current = AuditVersion(Required(id), side);
                RecordAudit(PacketAuditOperation.Discard, "shared-api", id, side.ToLowerInvariant(), current, current, missing);
                throw missing;
            }
        }, cancellationToken);

    public async Task<TrafficPacketCommitResult> ResolveContinueAsync(
        string exchangeId, string request, CancellationToken cancellationToken) =>
        ToTrafficCommitResult(await CaptureCommitAsync(exchangeId, "request", "Continue",
            () => ContinueAsync(exchangeId, request, cancellationToken), cancellationToken).ConfigureAwait(false));

    public async Task<TrafficPacketCommitResult> ResolveDropAsync(
        string exchangeId, CancellationToken cancellationToken) =>
        ToTrafficCommitResult(await CommitDropAsync(exchangeId, cancellationToken).ConfigureAwait(false));

    public async Task<TrafficPacketCommitResult> ResolveFulfillAsync(
        string exchangeId, string response, CancellationToken cancellationToken) =>
        ToTrafficCommitResult(await CaptureCommitAsync(exchangeId, "response", "Fulfill",
            () => FulfillAsync(exchangeId, response, cancellationToken), cancellationToken).ConfigureAwait(false));

    public async Task<TrafficPacketCommitResult> ResolveDiscardAsync(
        string exchangeId, string side, CancellationToken cancellationToken) =>
        ToTrafficCommitResult(await CommitDiscardAsync(exchangeId, side, cancellationToken).ConfigureAwait(false));

    public IReadOnlyList<TrafficAuditItem> GetAudit(string exchangeId, int limit = 100) =>
        _audit.Query(new PacketAuditQuery(exchangeId, Limit: limit)).Select(entry => new TrafficAuditItem(
            entry.Timestamp, entry.EntryPoint, entry.Operation.ToString(), entry.Side,
            FormatAuditVersion(entry.Before), FormatAuditVersion(entry.After),
            entry.Result.ToString(), entry.ErrorCode, entry.RuleId, entry.RuleAction, entry.Operator)).ToArray();

    public TrafficHistoryOverview GetHistoryOverview() => ToHistoryOverview(_history.GetStatistics());

    public string PreviewHistoryCleanup()
    {
        var value = _history.PreviewCleanup();
        return $"Cleanup preview: remove {value.RemovedEntries} entries / {value.RemovedEstimatedBytes} B; " +
               $"keep {value.RemainingEntries} entries / {value.RemainingEstimatedBytes} B.";
    }

    public Task<TrafficHistoryOverview> UpdateHistoryPolicyAsync(
        int maxEntries, long maxBytes, int retentionDays, bool autoPrune, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _history.UpdatePolicy(new TrafficHistoryPolicy(maxEntries, maxBytes, retentionDays, autoPrune, _history.Policy.SiteQuotas));
        return Task.FromResult(ToHistoryOverview(_history.GetStatistics()));
    }

    public Task<TrafficHistoryOverview> SetHistorySiteQuotaAsync(
        string hostPattern, int maxEntries, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPattern);
        var quotas = (_history.Policy.SiteQuotas ?? []).Where(value =>
            !value.HostPattern.Equals(hostPattern, StringComparison.OrdinalIgnoreCase)).ToList();
        quotas.Add(new TrafficSiteQuota(hostPattern, maxEntries, maxBytes));
        _history.UpdatePolicy(_history.Policy with { SiteQuotas = quotas });
        return Task.FromResult(ToHistoryOverview(_history.GetStatistics()));
    }

    public Task<TrafficHistoryOverview> RemoveHistorySiteQuotaAsync(
        string hostPattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPattern);
        var quotas = (_history.Policy.SiteQuotas ?? []).Where(value =>
            !value.HostPattern.Equals(hostPattern, StringComparison.OrdinalIgnoreCase)).ToArray();
        _history.UpdatePolicy(_history.Policy with { SiteQuotas = quotas });
        return Task.FromResult(ToHistoryOverview(_history.GetStatistics()));
    }

    public Task<string> CleanupTrafficHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = _history.Cleanup();
        return Task.FromResult($"Removed {value.RemovedEntries} entries / {value.RemovedEstimatedBytes} B; " +
                               $"remaining {value.RemainingEntries} / {value.RemainingEstimatedBytes} B.");
    }

    public Task ClearTrafficHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _history.Clear();
        return Task.CompletedTask;
    }

    public IReadOnlyList<TrafficExchange> Exchanges => _store.Read(5000)
        .Select(ToExchange).ToArray();

    public TrafficExchangePage Query(TrafficExchangeFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.AnnotationTag) ||
            !string.IsNullOrWhiteSpace(filter.AnnotationStatus))
            return QueryAnnotatedTraffic(filter);

        var result = _store.Query(new TrafficQuery(
            null, filter.Text, filter.Method, filter.Status, filter.ResourceType,
            filter.OnlyIntercepted ? TrafficState.Paused : null,
            Offset: filter.Offset, Limit: filter.Limit));
        return new TrafficExchangePage(result.Items.Select(ToExchange).ToArray(), result.Total, result.Offset, result.Limit);
    }

    private TrafficExchangePage QueryAnnotatedTraffic(TrafficExchangeFilter filter)
    {
        TrafficReviewStatus? reviewStatus = null;
        if (!string.IsNullOrWhiteSpace(filter.AnnotationStatus))
        {
            if (!Enum.TryParse<TrafficReviewStatus>(filter.AnnotationStatus, true, out var parsed))
                throw new ArgumentException("Annotation status must be Unreviewed, InReview, Resolved or Ignored.");
            reviewStatus = parsed;
        }

        var packetIds = _annotations.Query(new TrafficAnnotationQuery(
                string.IsNullOrWhiteSpace(filter.AnnotationTag) ? null : filter.AnnotationTag.Trim(),
                reviewStatus))
            .Select(annotation => annotation.PacketId)
            .ToHashSet(StringComparer.Ordinal);
        var offset = Math.Max(0, filter.Offset);
        var limit = Math.Clamp(filter.Limit, 1, 1000);
        if (packetIds.Count == 0)
            return new TrafficExchangePage([], 0, offset, limit);

        const int batchSize = 1000;
        var resultItems = new List<TrafficExchange>(limit);
        var total = 0;
        var sourceOffset = 0;
        while (true)
        {
            var batch = _store.Query(new TrafficQuery(
                null, filter.Text, filter.Method, filter.Status, filter.ResourceType,
                filter.OnlyIntercepted ? TrafficState.Paused : null,
                Offset: sourceOffset, Limit: batchSize));
            foreach (var item in batch.Items)
            {
                if (!packetIds.Contains(item.Id)) continue;
                if (total >= offset && resultItems.Count < limit)
                    resultItems.Add(ToExchange(item));
                total++;
            }

            sourceOffset += batch.Items.Count;
            if (batch.Items.Count == 0 || sourceOffset >= batch.Total) break;
        }

        return new TrafficExchangePage(resultItems, total, offset, limit);
    }

    public Task<PacketQueryPage> QueryPacketsAsync(PacketQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = PacketQueryLimits.Validate(query);
        var result = _store.Query(new TrafficQuery(
            null, query.Text, query.Method, query.StatusCode, query.ResourceType,
            query.OnlyIntercepted ? TrafficState.Paused : null,
            Offset: query.Offset, Limit: query.Limit));
        IReadOnlyList<PacketSummary> items = result.Items.Select(item => new PacketSummary(
            item.Id, item.Method, item.Url, item.ResponseStatus, item.State == TrafficState.Paused)).ToArray();
        return Task.FromResult(new PacketQueryPage(items, result.Total, result.Offset, result.Limit));
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
        _rules.Add(TrafficRuleDraftMapper.BuildRule(draft));
        return Task.CompletedTask;
    }

    public Task UpdateRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rules.Update(TrafficRuleDraftMapper.BuildRule(draft));
        return Task.CompletedTask;
    }

    public Task<TrafficRuleDraft?> GetRuleAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var rule = _rules.Get(id);
        return Task.FromResult(rule is null ? null : TrafficRuleDraftMapper.ToDraft(rule));
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
        string id, string name, string request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var draft = _repeater.Get(id) ?? throw new KeyNotFoundException($"Repeater draft '{id}' was not found.");
        var source = Required(draft.SourcePacketId);
        var edit = ToRequestEdit(HttpPacketCodec.Parse(request), source);
        _repeater.Update(id, new RepeaterDraftUpdate(name, edit.Method, edit.Url, edit.Headers, edit.Body, true));
        await _repeater.SendAsync(id, new RepeaterSendOptions(timeout), cancellationToken).ConfigureAwait(false);
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

    public Task<string> CompareRoundsAsync(string leftDraftId, string leftResultId,
        string rightDraftId, string rightResultId, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = side.Equals("response", StringComparison.OrdinalIgnoreCase);
        if (!response && !side.Equals("request", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Comparison side must be request or response.");
        var kind = response ? ComparisonSourceKind.RepeaterResponse : ComparisonSourceKind.RepeaterRequest;
        var result = _comparisons.Compare(
            new ComparisonSource(kind, DraftId: leftDraftId, SendResultId: leftResultId),
            new ComparisonSource(kind, DraftId: rightDraftId, SendResultId: rightResultId));
        return Task.FromResult(TrafficComparisonAdapter.Format(result));
    }

    public Task<string> SaveRoundComparisonAsync(string name, string leftDraftId, string leftResultId,
        string rightDraftId, string rightResultId, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var response = side.Equals("response", StringComparison.OrdinalIgnoreCase);
        if (!response && !side.Equals("request", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Comparison side must be request or response.");
        var kind = response ? ComparisonSourceKind.RepeaterResponse : ComparisonSourceKind.RepeaterRequest;
        var session = _comparisons.Create(name.Trim(),
            new ComparisonSource(kind, DraftId: leftDraftId, SendResultId: leftResultId),
            new ComparisonSource(kind, DraftId: rightDraftId, SendResultId: rightResultId));
        return Task.FromResult($"Saved comparison session '{session.Name}' ({session.Id}).{Environment.NewLine}" +
            TrafficComparisonAdapter.Format(session.Result));
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
        return Task.FromResult<IReadOnlyList<PacketArchiveEntry>>(ResolveArchiveEntries(filter));
    }

    public Task<PacketArchivePage> ExportArchivePageAsync(
        PacketArchiveExchangeQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PacketArchiveContent.Page(ResolveArchiveEntries(query.Filter), query));
    }

    private IReadOnlyList<PacketArchiveEntry> ResolveArchiveEntries(string? filter)
    {
        IEnumerable<TrafficMessage> items = _store.Read(5000, _activePageId);
        if (!string.IsNullOrWhiteSpace(filter))
            items = items.Where(item => item.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Method.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return items.Select(item =>
            new PacketArchiveEntry(item.Id, item.CapturedAt, FormatArchiveRequest(item),
                item.ResponseStatus is null ? null : FormatResponse(item),
                ToArchiveBody(item.RequestBody, item.RequestHeaders),
                ToArchiveBody(item.ResponseBody, item.ResponseHeaders))).ToArray();
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

    public async Task<int> ExportSignedAuditFileAsync(
        string path, string? packetId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var query = new PacketAuditQuery(packetId, Limit: Math.Clamp(limit, 1, PacketAuditExportService.MaximumEntries));
        var content = _auditExports.Export(query);
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
        return _audit.Query(query).Count;
    }

    public async Task<TrafficAuditVerificationItem> VerifySignedAuditFileAsync(
        string path, string? expectedKeyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (new System.IO.FileInfo(fullPath).Length > PacketAuditExportService.MaximumContentBytes)
            return new(false, null, 0, null, "content_too_large");
        var result = _auditExports.Verify(
            await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false), expectedKeyId);
        return new(result.Valid, result.KeyId, result.EntryCount, result.ExportedAt, result.ErrorCode);
    }

    public Task<PacketBodyDescriptor> DescribeBodyAsync(
        string id, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (body, contentType, charset) = GetBody(id, side);
        return Task.FromResult(new PacketBodyDescriptor(body.LongLength, BodySha256.Of(body), contentType, charset));
    }

    public async Task<TrafficBinaryBodyInfo> GetBinaryBodyInfoAsync(
        string exchangeId, string side, CancellationToken cancellationToken)
    {
        var info = await DescribeBodyAsync(exchangeId, side, cancellationToken).ConfigureAwait(false);
        return new TrafficBinaryBodyInfo(info.Length, info.Sha256, info.ContentType, info.Charset);
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
        var beforeAudit = AuditVersion(item, response ? "response" : "request");
        try
        {
        var source = (response ? item.ResponseBody : item.RequestBody) ?? [];
        var edited = BinaryBodyEditor.Apply(source, edit);
        var key = id + ":" + side.ToLowerInvariant();
        var originalBody = response ? item.ResponseBody : item.RequestBody;
        var createdDraft = new EditDraftState(id, response ? "response" : "request",
            originalBody?.ToArray(), (response ? item.ResponseHeaders : item.RequestHeaders).ToArray());
        var draft = _editDrafts.GetOrAdd(key, createdDraft);
        var addedDraft = ReferenceEquals(draft, createdDraft);
        var updated = response
            ? item with { ResponseBody = edited, ResponseHeaders = NormalizeContentLength(item.ResponseHeaders, edited.LongLength) }
            : item with { RequestBody = edited, RequestHeaders = NormalizeContentLength(item.RequestHeaders, edited.LongLength) };
        try { _store.Import(updated); }
        catch
        {
            if (addedDraft) _editDrafts.TryRemove(key, out _);
            throw;
        }
        Interlocked.Exchange(ref draft.FailedAttempts, 0);
        draft.LastFailure = null;
        var (_, contentType, charset) = GetBody(id, side);
        RecordAudit(PacketAuditOperation.BodyEdit, "shared-api", id, response ? "response" : "request",
            beforeAudit, AuditVersion(updated, response ? "response" : "request"));
        return Task.FromResult(PacketBodyChunker.Describe(edited, contentType, charset));
        }
        catch (Exception exception)
        {
            RecordAudit(PacketAuditOperation.BodyEdit, "shared-api", id, response ? "response" : "request",
                beforeAudit, beforeAudit, exception);
            throw;
        }
    }

    public Task<IReadOnlyList<PacketEditDraftStatus>> ListPendingEditsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PacketEditDraftStatus>>(_editDrafts.Values
            .Select(ToDraftStatus).OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Side, StringComparer.Ordinal).ToArray());
    }

    public Task<PacketEditDraftStatus?> GetPendingEditAsync(string id, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSide(side);
        return Task.FromResult(_editDrafts.TryGetValue(DraftKey(id, side), out var state) ? ToDraftStatus(state) : null);
    }

    public Task<bool> DiscardPendingEditAsync(string id, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSide(side);
        var key = DraftKey(id, side);
        if (!_editDrafts.TryGetValue(key, out var draft)) return Task.FromResult(false);
        var item = Required(id);
        var before = AuditVersion(item, draft.Side);
        var after = ToEditVersion(draft.OriginalBody ?? [], draft.OriginalHeaders);
        try
        {
            _store.Import(draft.Side == "response"
                ? item with { ResponseBody = draft.OriginalBody?.ToArray(), ResponseHeaders = draft.OriginalHeaders.ToArray() }
                : item with { RequestBody = draft.OriginalBody?.ToArray(), RequestHeaders = draft.OriginalHeaders.ToArray() });
            _editDrafts.TryRemove(key, out _);
            RecordAudit(PacketAuditOperation.Discard, "shared-api", id, draft.Side, before, after);
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            RecordAudit(PacketAuditOperation.Discard, "shared-api", id, draft.Side, before, after, exception);
            throw;
        }
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

    public async Task<string?> GetBinaryDraftStatusAsync(
        string exchangeId, string side, CancellationToken cancellationToken)
    {
        var draft = await GetPendingEditAsync(exchangeId, side, cancellationToken).ConfigureAwait(false);
        if (draft is null) return null;
        var failure = draft.LastCommitFailure is null ? "none" :
            $"{draft.LastCommitFailure.Attempts} attempt(s), {draft.LastCommitFailure.Message}";
        return $"Pending {draft.Side}: {draft.Before.Length} B / {draft.Before.Sha256} / CL {draft.Before.ContentLength ?? "-"} → " +
               $"{draft.After.Length} B / {draft.After.Sha256} / CL {draft.After.ContentLength ?? "-"}; last failure: {failure}";
    }

    public Task<bool> DiscardBinaryDraftAsync(
        string exchangeId, string side, CancellationToken cancellationToken) =>
        DiscardPendingEditAsync(exchangeId, side, cancellationToken);

    public IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket) =>
        HttpPacketParameters.Read(HttpPacketCodec.Parse(rawPacket))
            .Select(item => new TrafficParameterItem(item.Location.ToString().ToLowerInvariant(),
                item.Name, item.Value, item.Occurrence)).ToArray();

    public string SetParameter(string rawPacket, string location, string name, int occurrence, string value)
    {
        if (!Enum.TryParse<HttpParameterLocation>(location, true, out var parsed))
            throw new ArgumentException("Location must be query, form, json, header, cookie or multipart.");
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

    public Task<bool> DeleteAnnotationAsync(string exchangeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_annotations.Delete(exchangeId));
    }

    public async Task ReplayAsync(string id, CancellationToken cancellationToken)
    {
        var item = Required(id); var version = AuditVersion(item, "request");
        try { _ = await _traffic.ReplayAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false); RecordAudit(PacketAuditOperation.Replay, "cli-agent", id, "request", version, version); }
        catch (Exception exception) { RecordAudit(PacketAuditOperation.Replay, "cli-agent", id, "request", version, version, exception); throw; }
    }

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
        var afterVersion = AuditVersion(source, side);
        var beforeVersion = AuditBeforeDraft(key, afterVersion);
        if (!_editDrafts.ContainsKey(key))
        {
            try { await _traffic.ContinueAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false); RecordAudit(PacketAuditOperation.Continue, "cli-agent", id, side, beforeVersion, afterVersion); }
            catch (Exception exception) { RecordAudit(PacketAuditOperation.Continue, "cli-agent", id, side, beforeVersion, afterVersion, exception); throw; }
            return;
        }

        try
        {
            if (source.Stage == TrafficStage.Response)
                await _traffic.FulfillAsync(id, new TrafficResponseEdit(source.ResponseStatus ?? 200,
                    source.ResponseStatusText, source.ResponseHeaders, source.ResponseBody), cancellationToken).ConfigureAwait(false);
            else
                await _traffic.ContinueAsync(id, BuildDraftRequestEdit(source, _editDrafts[key]), cancellationToken).ConfigureAwait(false);
            ClearDraft(key);
            RecordAudit(source.Stage == TrafficStage.Response ? PacketAuditOperation.Fulfill : PacketAuditOperation.Continue,
                "cli-agent", id, side, beforeVersion, afterVersion);
        }
        catch (Exception exception) { RecordCommitFailure(key, exception); RecordAudit(source.Stage == TrafficStage.Response ? PacketAuditOperation.Fulfill : PacketAuditOperation.Continue, "cli-agent", id, side, beforeVersion, afterVersion, exception); throw; }
    }

    public async Task DropAsync(string id, CancellationToken cancellationToken)
    {
        var item = Required(id); var side = item.Stage == TrafficStage.Response ? "response" : "request"; var version = AuditVersion(item, side);
        try { await _traffic.FailAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { RecordAudit(PacketAuditOperation.Drop, "cli-agent", id, side, version, version, exception); throw; }
        ClearDraft(DraftKey(id, "request"));
        ClearDraft(DraftKey(id, "response"));
        RecordAudit(PacketAuditOperation.Drop, "cli-agent", id, side, version, version);
    }

    public async Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken)
    {
        var source = Required(id);
        var packet = HttpPacketCodec.Parse(rawPacket);
        ValidateSide(side);
        var key = DraftKey(id, side);
        var before = AuditBeforeDraft(key, AuditVersion(source, side));
        var submittedBody = Encoding.UTF8.GetBytes(packet.Body);
        var after = ToEditVersion(submittedBody, packet.Headers.Select(header => new TrafficHeader(header.Name, header.Value)).ToArray());
        try
        {
            if (side.Equals("response", StringComparison.OrdinalIgnoreCase))
                await _traffic.FulfillAsync(id, ToResponseEdit(packet), cancellationToken).ConfigureAwait(false);
            else
                await _traffic.ContinueAsync(id, ToRequestEdit(packet, source), cancellationToken).ConfigureAwait(false);
            ClearDraft(key);
            RecordAudit(side.Equals("response", StringComparison.OrdinalIgnoreCase) ? PacketAuditOperation.Fulfill : PacketAuditOperation.Edit,
                "cli-agent", id, side.ToLowerInvariant(), before, after);
        }
        catch (Exception exception) { RecordCommitFailure(key, exception); RecordAudit(side.Equals("response", StringComparison.OrdinalIgnoreCase) ? PacketAuditOperation.Fulfill : PacketAuditOperation.Edit, "cli-agent", id, side.ToLowerInvariant(), before, after, exception); throw; }
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

    public Task<IReadOnlyList<TrafficFindingItem>> AnalyzeFindingsAsync(
        string exchangeId, string side, string rawPacket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Required(exchangeId);
        ValidateSide(side);
        var analysis = HttpPacketAnalyzer.Analyze(HttpPacketCodec.Parse(rawPacket));
        return Task.FromResult<IReadOnlyList<TrafficFindingItem>>(analysis.Findings.Select(finding => new TrafficFindingItem(
            finding.Severity.ToString(), finding.Code, finding.Message,
            finding.Side.ToString(), finding.LocationKind.ToString(), finding.Field,
            finding.HeaderName, finding.HeaderOccurrence, finding.BodyOffset, finding.BodyLength)).ToArray());
    }

    public async Task<TrafficOperationResult> ReplayAsync(
        string exchangeId, string request, CancellationToken cancellationToken)
    {
        var source = Required(exchangeId);
        var before = AuditVersion(source, "request");
        var edit = ToRequestEdit(HttpPacketCodec.Parse(request), source);
        if (_editDrafts.ContainsKey(exchangeId + ":request"))
            edit = edit with
            {
                Headers = NormalizeContentLength(edit.Headers ?? [], source.RequestBody?.LongLength ?? 0),
                Body = source.RequestBody
            };
        TrafficReplayResult result;
        try { result = await _traffic.ReplayAsync(exchangeId, edit, cancellationToken).ConfigureAwait(false); RecordAudit(PacketAuditOperation.Replay, "workbench", exchangeId, "request", before, ToEditVersion(edit.Body ?? [], edit.Headers ?? [])); }
        catch (Exception exception) { RecordAudit(PacketAuditOperation.Replay, "workbench", exchangeId, "request", before, ToEditVersion(edit.Body ?? [], edit.Headers ?? []), exception); throw; }
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
        var before = AuditBeforeDraft(key, AuditVersion(source, "request"));
        if (_editDrafts.ContainsKey(key)) edit = edit with
        {
            Headers = NormalizeContentLength(edit.Headers ?? [], source.RequestBody?.LongLength ?? 0),
            Body = source.RequestBody
        };
        try
        {
            await _traffic.ContinueAsync(exchangeId, edit, cancellationToken).ConfigureAwait(false);
            ClearDraft(key);
            RecordAudit(PacketAuditOperation.Continue, "workbench", exchangeId, "request", before, ToEditVersion(edit.Body ?? [], edit.Headers ?? []));
        }
        catch (Exception exception) { RecordCommitFailure(key, exception); RecordAudit(PacketAuditOperation.Continue, "workbench", exchangeId, "request", before, ToEditVersion(edit.Body ?? [], edit.Headers ?? []), exception); throw; }
    }

    public async Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken)
    {
        var edit = ToResponseEdit(HttpPacketCodec.Parse(response));
        var key = exchangeId + ":response";
        var before = AuditBeforeDraft(key, AuditVersion(Required(exchangeId), "response"));
        if (_editDrafts.ContainsKey(key))
        {
            var source = Required(exchangeId);
            edit = edit with
            {
                Headers = NormalizeContentLength(edit.Headers ?? [], source.ResponseBody?.LongLength ?? 0),
                Body = source.ResponseBody
            };
        }
        try
        {
            await _traffic.FulfillAsync(exchangeId, edit, cancellationToken).ConfigureAwait(false);
            ClearDraft(key);
            RecordAudit(PacketAuditOperation.Fulfill, "workbench", exchangeId, "response", before, ToEditVersion(edit.Body ?? [], edit.Headers ?? []));
        }
        catch (Exception exception) { RecordCommitFailure(key, exception); RecordAudit(PacketAuditOperation.Fulfill, "workbench", exchangeId, "response", before, ToEditVersion(edit.Body ?? [], edit.Headers ?? []), exception); throw; }
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

    private static TrafficRequestEdit BuildDraftRequestEdit(TrafficMessage source, EditDraftState draft)
    {
        // Fetch.continueRequest computes Content-Length from postData. Re-sending an
        // otherwise untouched captured header set can make WebView2 reject the request.
        // Preserve explicit user header changes, but leave a body-only draft minimal.
        var headersChanged = !HeadersEqualExceptContentLength(draft.OriginalHeaders, source.RequestHeaders);
        return new TrafficRequestEdit(
            Headers: headersChanged ? source.RequestHeaders : null,
            Body: source.RequestBody);
    }

    private static bool HeadersEqualExceptContentLength(
        IReadOnlyList<TrafficHeader> first, IReadOnlyList<TrafficHeader> second) =>
        first.Where(header => !header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .SequenceEqual(second.Where(header => !header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)));

    private PacketEditDraftStatus ToDraftStatus(EditDraftState draft)
    {
        var current = Required(draft.Id);
        var body = draft.Side == "response" ? current.ResponseBody ?? [] : current.RequestBody ?? [];
        var headers = draft.Side == "response" ? current.ResponseHeaders : current.RequestHeaders;
        return new PacketEditDraftStatus(draft.Id, draft.Side, true,
            ToEditVersion(draft.OriginalBody ?? [], draft.OriginalHeaders), ToEditVersion(body, headers), draft.LastFailure);
    }

    private static PacketEditVersion ToEditVersion(byte[] body, IReadOnlyList<TrafficHeader> headers) =>
        new(body.LongLength, BodySha256.Of(body),
            headers.FirstOrDefault(header => header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))?.Value);

    private static PacketEditVersion AuditVersion(TrafficMessage item, string side) =>
        side.Equals("response", StringComparison.OrdinalIgnoreCase)
            ? ToEditVersion(item.ResponseBody ?? [], item.ResponseHeaders)
            : ToEditVersion(item.RequestBody ?? [], item.RequestHeaders);

    private PacketEditVersion AuditBeforeDraft(string key, PacketEditVersion fallback) =>
        _editDrafts.TryGetValue(key, out var draft)
            ? ToEditVersion(draft.OriginalBody ?? [], draft.OriginalHeaders)
            : fallback;

    private void RecordAudit(PacketAuditOperation operation, string entryPoint, string packetId, string side,
        PacketEditVersion before, PacketEditVersion after, Exception? error = null)
    {
        try
        {
            _audit.Record(new PacketAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                entryPoint, operation, packetId, side, before, after,
                error is null ? PacketAuditResult.Succeeded : PacketAuditResult.Failed,
                error?.GetType().FullName));
        }
        catch { /* Audit persistence must never change the packet operation outcome. */ }
    }

    private void RecordCommitFailure(string key, Exception exception)
    {
        if (!_editDrafts.TryGetValue(key, out var draft)) return;
        var attempts = Interlocked.Increment(ref draft.FailedAttempts);
        draft.LastFailure = new PacketEditCommitFailure(exception.Message, DateTimeOffset.UtcNow, attempts);
    }

    private void ClearDraft(string key)
    {
        _editDrafts.TryRemove(key, out _);
    }

    private static string DraftKey(string id, string side) => id + ":" + side.ToLowerInvariant();
    private static void ValidateSide(string side)
    {
        if (!side.Equals("request", StringComparison.OrdinalIgnoreCase) && !side.Equals("response", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Side must be request or response.");
    }

    private void OnRulesChanged(TrafficRuleChange _)
    {
        UpdateModificationGate();
        RulesChanged?.Invoke();
    }
    private void OnRepeaterChanged(RepeaterChangedEvent _) => RepeaterChanged?.Invoke();
    private void OnAnnotationChanged(TrafficAnnotationChanged _) => Changed?.Invoke();
    private void OnHistoryChanged(TrafficHistoryChanged _) => Changed?.Invoke();
    private void OnSessionOpened(ICdpSession session) => _ = StartForPageAsync(session.PageId);
    private void OnSessionClosed(string pageId) => _ = _traffic.StopCaptureAsync(pageId);

    private async Task StartForPageAsync(string pageId, CancellationToken ct = default)
    {
        try
        {
            await _traffic.StartCaptureAsync(pageId,
                new TrafficCaptureOptions(PauseRequests: _intercept, PauseResponses: _responseIntercept,
                    CaptureResponseBodies: true, MaxResponseBodyBytes: _settings.Load().Browser.MaxCapturedBodyBytes), ct)
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
        var history = draft.History.OrderByDescending(item => item.Sequence).Select(item =>
        {
            var roundRequest = HttpPacketCodec.Format(new HttpPacket
            {
                Kind = HttpPacketKind.Request, ProtocolVersion = "HTTP/1.1", Method = item.Request.Method,
                Target = item.Request.Url, Headers = item.Request.Headers.Select(h => new HttpHeader(h.Name, h.Value)).ToArray(),
                Body = DecodeBody(item.Request.Body)
            });
            var roundResponse = item.ResponseStatus is null ? string.Empty : HttpPacketCodec.Format(new HttpPacket
            {
                Kind = HttpPacketKind.Response, ProtocolVersion = "HTTP/1.1", StatusCode = item.ResponseStatus,
                ReasonPhrase = item.ResponseStatusText, Headers = item.ResponseHeaders.Select(h => new HttpHeader(h.Name, h.Value)).ToArray(),
                Body = DecodeBody(item.ResponseBody)
            });
            var roundMetrics = $"{item.DurationMilliseconds} ms · {item.RequestSize} B → {item.ResponseSize} B";
            return new RepeaterRoundItem(draft.Id, draft.Name, item.Id, item.Sequence, item.Status.ToString(),
                roundMetrics, roundRequest, roundResponse, item.ResponseStatus is not null);
        }).ToArray();
        return new RepeaterDraftItem(draft.Id, draft.Name, request, draft.Revision, draft.History.Count,
            latest?.Status.ToString() ?? "Draft", metrics, response, history);
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

    private static string FormatAuditVersion(PacketEditVersion value) =>
        $"{value.Length} B · {value.Sha256} · CL {value.ContentLength ?? "-"}";

    private static TrafficPacketCommitResult ToTrafficCommitResult(PacketCommitResult value) => new(
        value.Success, value.Operation, value.PacketId, value.Side, value.FinalState,
        FormatAuditVersion(value.Before), FormatAuditVersion(value.After), value.AuditId,
        value.ErrorCode, value.Message);

    private async Task<PacketCommitResult> CaptureCommitAsync(
        string id, string? requestedSide, string fallbackOperation, Func<Task> action, CancellationToken cancellationToken)
    {
        var previousAuditId = _audit.Query(new PacketAuditQuery(id, Limit: 1)).FirstOrDefault()?.AuditId;
        try
        {
            await action().ConfigureAwait(false);
            return CreateCommitResult(id, requestedSide, fallbackOperation, true, null, previousAuditId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return CreateCommitResult(id, requestedSide, fallbackOperation, false, exception.GetType().Name, previousAuditId);
        }
    }

    private PacketCommitResult CreateCommitResult(
        string id, string? requestedSide, string fallbackOperation, bool success, string? fallbackError, string? previousAuditId)
    {
        var audit = _audit.Query(new PacketAuditQuery(id, Limit: 10)).FirstOrDefault(entry =>
            entry.AuditId != previousAuditId &&
            (requestedSide is null || entry.Side.Equals(requestedSide, StringComparison.OrdinalIgnoreCase)));
        var item = _store.Get(id);
        var side = audit?.Side ?? requestedSide?.ToLowerInvariant() ??
            (item?.Stage == TrafficStage.Response ? "response" : "request");
        var version = item is null ? new PacketEditVersion(0, new string('0', 64), null) : AuditVersion(item, side);
        return new PacketCommitResult(success, audit?.Operation.ToString() ?? fallbackOperation, id, side,
            item?.State.ToString() ?? "Unknown", audit?.Before ?? version, audit?.After ?? version,
            audit?.AuditId, audit?.ErrorCode ?? fallbackError,
            success ? $"{audit?.Operation.ToString() ?? fallbackOperation} completed." :
            $"{audit?.Operation.ToString() ?? fallbackOperation} failed ({audit?.ErrorCode ?? fallbackError ?? "Unknown"}).");
    }

    private static TrafficHistoryOverview ToHistoryOverview(TrafficHistoryStatistics value) => new(
        value.EntryCount, value.EstimatedContentBytes, value.PersistedFileBytes,
        value.OldestCapture, value.NewestCapture, value.Policy.MaxEntries,
        value.Policy.MaxStorageBytes, value.Policy.RetentionDays, value.Policy.AutoPrune,
        (value.Policy.SiteQuotas ?? []).Select(quota =>
            new TrafficHistorySiteQuotaItem(quota.HostPattern, quota.MaxEntries, quota.MaxStorageBytes)).ToArray(),
        value.PolicySource);

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _sessions.SessionOpened -= OnSessionOpened;
        _sessions.SessionClosed -= OnSessionClosed;
        _rules.Changed -= OnRulesChanged;
        _repeater.Changed -= OnRepeaterChanged;
        _annotations.Changed -= OnAnnotationChanged;
        _history.Changed -= OnHistoryChanged;
        _activeSubscription.Dispose();
        _captureGate.Dispose();
    }

    private sealed class EditDraftState(string id, string side, byte[]? originalBody, IReadOnlyList<TrafficHeader> originalHeaders)
    {
        public string Id { get; } = id;
        public string Side { get; } = side;
        public byte[]? OriginalBody { get; } = originalBody;
        public IReadOnlyList<TrafficHeader> OriginalHeaders { get; } = originalHeaders;
        public int FailedAttempts;
        public PacketEditCommitFailure? LastFailure;
    }
}
