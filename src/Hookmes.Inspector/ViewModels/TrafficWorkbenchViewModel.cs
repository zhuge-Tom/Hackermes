using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Hookmes.Base.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Inspector.ViewModels;

/// <summary>UI-facing boundary implemented by the traffic/proxy module.</summary>
public interface ITrafficWorkbenchService
{
    IReadOnlyList<TrafficExchange> Exchanges { get; }
    TrafficExchangePage Query(TrafficExchangeFilter filter);
    bool IsInterceptEnabled { get; set; }
    bool IsResponseInterceptEnabled { get; set; }
    event Action? Changed;
    Task<TrafficOperationResult> AnalyzeAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrafficFindingItem>> AnalyzeFindingsAsync(string exchangeId, string side, string rawPacket, CancellationToken cancellationToken);
    Task<TrafficOperationResult> ReplayAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task DropAsync(string exchangeId, CancellationToken cancellationToken);
    Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken);
    Task<string> CreateRepeaterAsync(string exchangeId, CancellationToken cancellationToken);
    Task<string> EditBinaryBodyAsync(string exchangeId, string side, string kind, long offset, long count,
        string data, string encoding, CancellationToken cancellationToken);
    Task<string> ReadBinaryBodyAsync(string exchangeId, string side, long offset, int count,
        string encoding, CancellationToken cancellationToken);
    Task<TrafficBinaryBodyInfo> GetBinaryBodyInfoAsync(string exchangeId, string side, CancellationToken cancellationToken);
    Task<string?> GetBinaryDraftStatusAsync(string exchangeId, string side, CancellationToken cancellationToken);
    Task<bool> DiscardBinaryDraftAsync(string exchangeId, string side, CancellationToken cancellationToken);
    Task<TrafficPacketCommitResult> ResolveContinueAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task<TrafficPacketCommitResult> ResolveDropAsync(string exchangeId, CancellationToken cancellationToken);
    Task<TrafficPacketCommitResult> ResolveFulfillAsync(string exchangeId, string response, CancellationToken cancellationToken);
    Task<TrafficPacketCommitResult> ResolveDiscardAsync(string exchangeId, string side, CancellationToken cancellationToken);
    IReadOnlyList<TrafficAuditItem> GetAudit(string exchangeId, int limit = 100);
    TrafficHistoryOverview GetHistoryOverview();
    string PreviewHistoryCleanup();
    Task<TrafficHistoryOverview> UpdateHistoryPolicyAsync(int maxEntries, long maxBytes, int retentionDays,
        bool autoPrune, CancellationToken cancellationToken);
    Task<TrafficHistoryOverview> SetHistorySiteQuotaAsync(string hostPattern, int maxEntries, long maxBytes, CancellationToken cancellationToken);
    Task<TrafficHistoryOverview> RemoveHistorySiteQuotaAsync(string hostPattern, CancellationToken cancellationToken);
    Task<string> CleanupTrafficHistoryAsync(CancellationToken cancellationToken);
    Task ClearTrafficHistoryAsync(CancellationToken cancellationToken);
    Task<int> ExportArchiveFileAsync(string path, string? filter, CancellationToken cancellationToken);
    Task<int> ImportArchiveFileAsync(string path, CancellationToken cancellationToken);
    IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket);
    string SetParameter(string rawPacket, string location, string name, int occurrence, string value);
    TrafficAnnotationItem? GetAnnotation(string exchangeId);
    Task<TrafficAnnotationItem> SaveAnnotationAsync(string exchangeId, bool starred, string tags,
        string note, string status, CancellationToken cancellationToken);
}

public sealed record TrafficParameterItem(string Location, string Name, string Value, int Occurrence);
public sealed record TrafficFindingItem(
    string Severity, string Code, string Message, string Side, string LocationKind,
    string? Field, string? HeaderName, int? HeaderOccurrence, long? BodyOffset, int? BodyLength)
{
    public string Location => LocationKind == "Header" ? $"{HeaderName}[{HeaderOccurrence ?? 0}]"
        : LocationKind == "Body" ? $"byte {BodyOffset ?? 0} +{BodyLength ?? 0}"
        : Field ?? LocationKind;
}
public sealed record TrafficAnnotationItem(bool Starred, string Tags, string Note, string Status, int Revision);
public sealed record TrafficAuditItem(DateTimeOffset Timestamp, string EntryPoint, string Operation, string Side,
    string Before, string After, string Result, string? ErrorCode, string? RuleId = null, string? RuleAction = null);
public sealed record TrafficHistoryOverview(int EntryCount, long EstimatedBytes, long FileBytes,
    DateTimeOffset? Oldest, DateTimeOffset? Newest, int MaxEntries, long MaxBytes, int RetentionDays, bool AutoPrune,
    IReadOnlyList<TrafficHistorySiteQuotaItem> SiteQuotas);
public sealed record TrafficHistorySiteQuotaItem(string HostPattern, int MaxEntries, long MaxBytes);
public sealed record TrafficBinaryBodyInfo(long Length, string Sha256, string? ContentType, string? Charset);
public sealed record TrafficPacketCommitResult(bool Success, string Operation, string PacketId, string Side,
    string FinalState, string Before, string After, string? AuditId, string? ErrorCode, string Message)
{
    public string Summary => $"{Operation} {(Success ? "completed" : "failed")} · state {FinalState} · audit {AuditId ?? "-"}" +
        $"{Environment.NewLine}{Before} → {After}" + (ErrorCode is null ? string.Empty : $" · error {ErrorCode}");
}
public sealed record HttpTextSelection(int Start, int End)
{
    public int Length => End - Start;
}

public static class HttpFindingTextLocator
{
    public static HttpTextSelection? Locate(string rawHttp, string locationKind, string? headerName = null, int occurrence = 0)
    {
        if (string.IsNullOrEmpty(rawHttp)) return null;
        var firstLineEnd = FindLineEnd(rawHttp, 0);
        if (locationKind.Equals("StartLine", StringComparison.OrdinalIgnoreCase))
            return new HttpTextSelection(0, firstLineEnd.ContentEnd);
        if (!locationKind.Equals("Header", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(headerName) || occurrence < 0) return null;

        var headerEnd = FindHeaderEnd(rawHttp);
        var position = firstLineEnd.NextStart;
        var found = 0;
        while (position < headerEnd)
        {
            var line = FindLineEnd(rawHttp, position);
            if (line.ContentEnd <= position) break;
            var colon = rawHttp.IndexOf(':', position, line.ContentEnd - position);
            if (colon > position && rawHttp[position..colon].Trim().Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                if (found++ == occurrence) return new HttpTextSelection(position, line.ContentEnd);
            }
            position = line.NextStart;
        }
        return null;
    }

    private static int FindHeaderEnd(string text)
    {
        var crlf = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (crlf < 0) return lf < 0 ? text.Length : lf;
        return lf < 0 ? crlf : Math.Min(crlf, lf);
    }

    private static (int ContentEnd, int NextStart) FindLineEnd(string text, int start)
    {
        var newline = text.IndexOf('\n', start);
        if (newline < 0) return (text.Length, text.Length);
        var contentEnd = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
        return (contentEnd, newline + 1);
    }
}

public static class TrafficBinaryRange
{
    public static long Previous(long offset, int count) => Math.Max(0, offset - Math.Max(1, count));
    public static long Next(long totalLength, long offset, int loadedCount) =>
        Math.Clamp(checked(Math.Max(0, offset) + Math.Max(0, loadedCount)), 0, Math.Max(0, totalLength));
    public static int ActualCount(long totalLength, long offset, int requestedCount) =>
        offset < 0 || offset > totalLength ? 0 : (int)Math.Min(Math.Max(0, requestedCount), totalLength - offset);
}

public sealed record TrafficExchange(
    string Id,
    DateTimeOffset Timestamp,
    string Method,
    string Url,
    int? Status,
    string RequestText,
    string ResponseText,
    bool IsIntercepted = false,
    bool IsResponseStage = false)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : Url;
    public string StatusText => Status?.ToString() ?? (IsIntercepted ? "HOLD" : "—");
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
}

public sealed record TrafficOperationResult(bool Success, string Summary, string? ResponseText = null);
public sealed record TrafficExchangeFilter(
    string? Text, string? Method, int? Status, string? ResourceType,
    bool OnlyIntercepted, int Offset, int Limit);
public sealed record TrafficExchangePage(
    IReadOnlyList<TrafficExchange> Items, int Total, int Offset, int Limit);

public partial class TrafficWorkbenchViewModel : ViewModelBase
{
    private readonly ITrafficWorkbenchService _service;
    private readonly IRecentTrafficPathService? _recentPaths;
    private CancellationTokenSource? _operation;

    public TrafficWorkbenchViewModel(ITrafficWorkbenchService service, IRecentTrafficPathService? recentPaths = null)
    {
        _service = service;
        _recentPaths = recentPaths;
        if (!string.IsNullOrWhiteSpace(recentPaths?.LastArchivePath))
            _archivePath = recentPaths.LastArchivePath;
        _service.Changed += OnServiceChanged;
        _isInterceptEnabled = service.IsInterceptEnabled;
        _isResponseInterceptEnabled = service.IsResponseInterceptEnabled;
        Refresh();
    }

    public ObservableCollection<TrafficExchange> Visible { get; } = [];
    public ObservableCollection<TrafficParameterItem> Parameters { get; } = [];
    public ObservableCollection<TrafficAuditItem> AuditEntries { get; } = [];
    public ObservableCollection<TrafficFindingItem> Findings { get; } = [];
    public InspectorFileDialogDelegates? FileDialogs { get; set; }
    private static readonly InspectorFileType[] ArchiveFileTypes =
    [
        new("HTTP Archive (*.har)", ["*.har"]),
        new("Hookmes JSON (*.json)", ["*.json"])
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ReplayCommand), nameof(SendToRepeaterCommand), nameof(LoadBinaryChunkCommand), nameof(PreviousBinaryChunkCommand), nameof(NextBinaryChunkCommand), nameof(RefreshBinaryBodyInfoCommand), nameof(ApplyBinaryEditCommand), nameof(RefreshBinaryDraftCommand), nameof(DiscardBinaryDraftCommand), nameof(ApplyParameterCommand), nameof(SaveAnnotationCommand), nameof(RefreshAuditCommand), nameof(ContinueCommand), nameof(DropCommand), nameof(FulfillCommand))]
    private TrafficExchange? _selected;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _methodFilter = string.Empty;
    [ObservableProperty] private string _statusFilter = string.Empty;
    [ObservableProperty] private string _resourceTypeFilter = string.Empty;
    [ObservableProperty] private bool _onlyIntercepted;
    [ObservableProperty] private bool _isInterceptEnabled;
    [ObservableProperty] private bool _isResponseInterceptEnabled;
    [ObservableProperty] private string _requestEditor = string.Empty;
    [ObservableProperty] private string _responseEditor = string.Empty;
    [ObservableProperty] private int _requestSelectionStart;
    [ObservableProperty] private int _requestSelectionEnd;
    [ObservableProperty] private int _responseSelectionStart;
    [ObservableProperty] private int _responseSelectionEnd;
    [ObservableProperty] private string _formattedRequest = string.Empty;
    [ObservableProperty] private string _formattedResponse = string.Empty;
    [ObservableProperty] private string _analysis = "Select a request, edit it, then Analyze or Replay.";
    [ObservableProperty] private string _analysisSide = "request";
    [ObservableProperty] private string _findingTarget = "No finding selected.";
    [ObservableProperty] private TrafficFindingItem? _selectedFinding;
    [ObservableProperty] private string _summary = "No traffic captured";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(DropCommand), nameof(FulfillCommand))]
    private bool _isBusy;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private string _binarySide = "request";
    [ObservableProperty] private string _binaryKind = "replace";
    [ObservableProperty] private string _binaryOffset = "0";
    [ObservableProperty] private string _binaryCount = (64 * 1024).ToString();
    [ObservableProperty] private string _binaryEncoding = "hex";
    [ObservableProperty] private string _binaryData = string.Empty;
    [ObservableProperty] private string _binaryDraftStatus = "No pending binary edit.";
    [ObservableProperty] private string _binaryBodySummary = "Body info not loaded.";
    [ObservableProperty] private string _binaryRangeStatus = "No chunk loaded.";
    [ObservableProperty] private double _binaryProgress;
    private long _binaryBodyLength;
    private int _binaryLoadedCount;
    private bool _binaryInfoLoaded;
    [ObservableProperty] private string _archivePath = "traffic.har";
    [ObservableProperty] private string _archiveFilter = string.Empty;
    [ObservableProperty] private string _parameterSide = "request";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyParameterCommand))]
    private TrafficParameterItem? _selectedParameter;
    [ObservableProperty] private string _parameterValue = string.Empty;
    [ObservableProperty] private bool _annotationStarred;
    [ObservableProperty] private string _annotationTags = string.Empty;
    [ObservableProperty] private string _annotationNote = string.Empty;
    [ObservableProperty] private string _annotationStatus = "Unreviewed";
    [ObservableProperty] private string _historyMaxEntries = "5000";
    [ObservableProperty] private string _historyMaxBytes = (256L * 1024 * 1024).ToString();
    [ObservableProperty] private string _historyRetentionDays = "30";
    [ObservableProperty] private bool _historyAutoPrune = true;
    [ObservableProperty] private string _historySitePattern = string.Empty;
    [ObservableProperty] private string _historySiteMaxEntries = "1000";
    [ObservableProperty] private string _historySiteMaxBytes = (64L * 1024 * 1024).ToString();
    [ObservableProperty] private bool _confirmHistoryClear;
    [ObservableProperty] private string _historyStatus = "History statistics not loaded.";
    private const int PageSize = 200;

    public bool HasSelection => Selected is not null;
    public bool CanResolveIntercept => Selected?.IsIntercepted == true && !IsBusy;

    partial void OnSelectedChanged(TrafficExchange? value)
    {
        RequestEditor = value?.RequestText ?? string.Empty;
        ResponseEditor = value?.ResponseText ?? string.Empty;
        FormattedRequest = FormatMessage(RequestEditor);
        FormattedResponse = FormatMessage(ResponseEditor);
        Analysis = value is null ? "Select a request, edit it, then Analyze or Replay." : $"{value.Method} {value.Url}";
        LoadParameters();
        LoadAnnotation();
        LoadAudit();
        Findings.Clear();
        SelectedFinding = null;
        RequestSelectionStart = RequestSelectionEnd = 0;
        ResponseSelectionStart = ResponseSelectionEnd = 0;
        ResetBinaryNavigation();
        if (value is not null) _ = LoadBinaryBodyInfoSafelyAsync(value.Id, BinarySide);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanResolveIntercept));
    }

    partial void OnFilterTextChanged(string value) { PageIndex = 0; Refresh(); }
    partial void OnMethodFilterChanged(string value) { PageIndex = 0; Refresh(); }
    partial void OnStatusFilterChanged(string value) { PageIndex = 0; Refresh(); }
    partial void OnResourceTypeFilterChanged(string value) { PageIndex = 0; Refresh(); }
    partial void OnOnlyInterceptedChanged(bool value) { PageIndex = 0; Refresh(); }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanResolveIntercept));
    partial void OnIsInterceptEnabledChanged(bool value) => _service.IsInterceptEnabled = value;
    partial void OnIsResponseInterceptEnabledChanged(bool value) => _service.IsResponseInterceptEnabled = value;
    partial void OnRequestEditorChanged(string value) { FormattedRequest = FormatMessage(value); if (ParameterSide == "request") LoadParameters(); }
    partial void OnResponseEditorChanged(string value) { FormattedResponse = FormatMessage(value); if (ParameterSide == "response") LoadParameters(); }
    partial void OnParameterSideChanged(string value) => LoadParameters();
    partial void OnBinarySideChanged(string value)
    {
        ResetBinaryNavigation();
        if (Selected is not null) _ = LoadBinaryBodyInfoSafelyAsync(Selected.Id, value);
    }
    partial void OnSelectedParameterChanged(TrafficParameterItem? value) => ParameterValue = value?.Value ?? string.Empty;
    partial void OnSelectedFindingChanged(TrafficFindingItem? value)
    {
        if (value is null) { FindingTarget = "No finding selected."; return; }
        var side = value.Side.Equals("Response", StringComparison.OrdinalIgnoreCase) ? "response" : "request";
        if (value.LocationKind == "Body")
        {
            BinarySide = side;
            BinaryOffset = (value.BodyOffset ?? 0).ToString();
            BinaryCount = (value.BodyLength ?? 0).ToString();
            FindingTarget = $"Binary editor target: {side} body, offset {BinaryOffset}, length {BinaryCount}.";
        }
        else if (value.LocationKind == "Header")
        {
            var raw = side == "response" ? ResponseEditor : RequestEditor;
            var selection = HttpFindingTextLocator.Locate(raw, value.LocationKind, value.HeaderName, value.HeaderOccurrence ?? 0);
            ApplyFindingSelection(side, selection);
            FindingTarget = selection is null
                ? $"{side} editor target not found: header '{value.HeaderName}', occurrence {value.HeaderOccurrence ?? 0}."
                : $"{side} editor selection {selection.Start}–{selection.End}: header '{value.HeaderName}', occurrence {value.HeaderOccurrence ?? 0}.";
        }
        else if (value.LocationKind == "StartLine")
        {
            var raw = side == "response" ? ResponseEditor : RequestEditor;
            var selection = HttpFindingTextLocator.Locate(raw, value.LocationKind);
            ApplyFindingSelection(side, selection);
            FindingTarget = selection is null ? $"{side} start line not found."
                : $"{side} editor selection {selection.Start}–{selection.End}: start line.";
        }
        else
        {
            FindingTarget = $"{side} editor target: {value.Field ?? value.LocationKind}.";
        }
        Analysis = $"[{value.Severity}] {value.Code}: {value.Message}{Environment.NewLine}{FindingTarget}";
    }

    private void ApplyFindingSelection(string side, HttpTextSelection? selection)
    {
        if (selection is null) return;
        if (side == "response")
        {
            ResponseSelectionEnd = selection.End;
            ResponseSelectionStart = selection.Start;
        }
        else
        {
            RequestSelectionEnd = selection.End;
            RequestSelectionStart = selection.Start;
        }
    }

    [RelayCommand] private void Reload() => Refresh();
    [RelayCommand] private Task ExportArchiveAsync() => ExecuteAsync(async ct =>
    {
        var path = ArchivePath.Trim();
        if (FileDialogs is not null)
        {
            path = await FileDialogs.SaveAsync(new InspectorFileDialogRequest(
                "Export traffic archive", path, ArchiveFileTypes), ct) ?? string.Empty;
            if (path.Length == 0) return;
            ArchivePath = path;
        }
        path = NormalizeArchivePath(path);
        var count = await _service.ExportArchiveFileAsync(path,
            string.IsNullOrWhiteSpace(ArchiveFilter) ? null : ArchiveFilter.Trim(), ct);
        RememberArchivePath(path);
        Analysis = $"Exported {count} packet(s) to {path}.";
    });
    [RelayCommand] private Task ImportArchiveAsync() => ExecuteAsync(async ct =>
    {
        var path = ArchivePath.Trim();
        if (FileDialogs is not null)
        {
            path = await FileDialogs.OpenAsync(new InspectorFileDialogRequest(
                "Import traffic archive", path, ArchiveFileTypes), ct) ?? string.Empty;
            if (path.Length == 0) return;
            ArchivePath = path;
        }
        path = NormalizeArchivePath(path);
        var count = await _service.ImportArchiveFileAsync(path, ct);
        RememberArchivePath(path);
        Analysis = $"Imported {count} packet(s) from {path}.";
    });
    private string NormalizeArchivePath(string path) => _recentPaths?.NormalizePath(path) ?? path.Trim();
    private void RememberArchivePath(string path)
    {
        ArchivePath = path;
        _recentPaths?.RememberArchivePath(path);
    }
    [RelayCommand] private void PreviousPage() { if (PageIndex > 0) { PageIndex--; Refresh(); } }
    [RelayCommand] private void NextPage()
    {
        if ((PageIndex + 1) * PageSize < _service.Query(CreateFilter()).Total) { PageIndex++; Refresh(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AnalyzeAsync() => ExecuteAsync(async ct =>
    {
        var side = AnalysisSide.Equals("response", StringComparison.OrdinalIgnoreCase) ? "response" : "request";
        var raw = side == "response" ? ResponseEditor : RequestEditor;
        var findings = await _service.AnalyzeFindingsAsync(Selected!.Id, side, raw, ct);
        Findings.Clear();
        foreach (var finding in findings) Findings.Add(finding);
        Analysis = findings.Count == 0 ? "No built-in findings." : $"{findings.Count} finding(s). Select one to locate its edit target.";
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ReplayAsync() => RunAsync(ct => _service.ReplayAsync(Selected!.Id, RequestEditor, ct), applyResponse: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task SendToRepeaterAsync() => ExecuteAsync(async ct =>
    {
        var id = await _service.CreateRepeaterAsync(Selected!.Id, ct);
        Analysis = $"Created Repeater draft: {id}";
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ApplyBinaryEditAsync() => ExecuteAsync(async ct =>
    {
        if (!long.TryParse(BinaryOffset, out var offset) || !long.TryParse(BinaryCount, out var count))
            throw new ArgumentException("Binary offset and count must be integers.");
        Analysis = await _service.EditBinaryBodyAsync(Selected!.Id, BinarySide, BinaryKind,
            offset, count, BinaryData, BinaryEncoding, ct);
        await LoadBinaryDraftStatusAsync(ct);
        await LoadBinaryBodyInfoAsync(ct);
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RefreshBinaryDraftAsync() => ExecuteAsync(LoadBinaryDraftStatusAsync);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DiscardBinaryDraftAsync() => ExecuteAsync(async ct =>
    {
        var result = await _service.ResolveDiscardAsync(Selected!.Id, BinarySide, ct);
        BinaryDraftStatus = result.Success ? "Pending binary edit discarded; original body and headers restored." : result.Message;
        Analysis = result.Summary;
        if (result.Success) Refresh();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RefreshAudit() => LoadAudit();

    [RelayCommand]
    private void RefreshHistory() => ApplyHistoryOverview(_service.GetHistoryOverview());

    [RelayCommand]
    private void PreviewHistoryCleanup() => HistoryStatus = _service.PreviewHistoryCleanup();

    [RelayCommand]
    private Task SaveHistoryPolicyAsync() => ExecuteAsync(async ct =>
    {
        if (!int.TryParse(HistoryMaxEntries, out var maxEntries) ||
            !long.TryParse(HistoryMaxBytes, out var maxBytes) ||
            !int.TryParse(HistoryRetentionDays, out var retentionDays))
            throw new ArgumentException("History limits must be integers.");
        ApplyHistoryOverview(await _service.UpdateHistoryPolicyAsync(
            maxEntries, maxBytes, retentionDays, HistoryAutoPrune, ct));
    });

    [RelayCommand]
    private Task SetHistorySiteQuotaAsync() => ExecuteAsync(async ct =>
    {
        if (!int.TryParse(HistorySiteMaxEntries, out var maxEntries) ||
            !long.TryParse(HistorySiteMaxBytes, out var maxBytes))
            throw new ArgumentException("Site quota limits must be integers.");
        ApplyHistoryOverview(await _service.SetHistorySiteQuotaAsync(
            HistorySitePattern.Trim(), maxEntries, maxBytes, ct));
    });

    [RelayCommand]
    private Task RemoveHistorySiteQuotaAsync() => ExecuteAsync(async ct =>
        ApplyHistoryOverview(await _service.RemoveHistorySiteQuotaAsync(HistorySitePattern.Trim(), ct)));

    [RelayCommand]
    private Task CleanupTrafficHistoryAsync() => ExecuteAsync(async ct =>
    {
        HistoryStatus = await _service.CleanupTrafficHistoryAsync(ct);
        Refresh();
    });

    [RelayCommand]
    private Task ClearTrafficHistoryAsync() => ExecuteAsync(async ct =>
    {
        if (!ConfirmHistoryClear) throw new InvalidOperationException("Enable the clear confirmation checkbox first.");
        await _service.ClearTrafficHistoryAsync(ct);
        ConfirmHistoryClear = false;
        HistoryStatus = "Traffic history cleared.";
        Refresh();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task LoadBinaryChunkAsync() => ExecuteAsync(ct => LoadBinaryChunkCoreAsync(ParseBinaryOffset(), ct));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PreviousBinaryChunkAsync() => ExecuteAsync(ct =>
        LoadBinaryChunkCoreAsync(TrafficBinaryRange.Previous(ParseBinaryOffset(), ParseBinaryCount()), ct));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task NextBinaryChunkAsync() => ExecuteAsync(ct =>
        LoadBinaryChunkCoreAsync(TrafficBinaryRange.Next(_binaryBodyLength, ParseBinaryOffset(), _binaryLoadedCount), ct));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RefreshBinaryBodyInfoAsync() => ExecuteAsync(LoadBinaryBodyInfoAsync);

    private async Task LoadBinaryChunkCoreAsync(long offset, CancellationToken ct)
    {
        if (!_binaryInfoLoaded) await LoadBinaryBodyInfoAsync(ct);
        var count = ParseBinaryCount();
        if (offset < 0 || offset > _binaryBodyLength) throw new ArgumentOutOfRangeException(nameof(offset), $"Offset must be between 0 and {_binaryBodyLength}.");
        BinaryData = await _service.ReadBinaryBodyAsync(Selected!.Id, BinarySide, offset, count, BinaryEncoding, ct);
        BinaryOffset = offset.ToString();
        _binaryLoadedCount = TrafficBinaryRange.ActualCount(_binaryBodyLength, offset, count);
        var end = offset + _binaryLoadedCount;
        BinaryRangeStatus = $"Range {offset:N0}–{end:N0} of {_binaryBodyLength:N0} bytes · {_binaryLoadedCount:N0} loaded";
        BinaryProgress = _binaryBodyLength == 0 ? 100 : end * 100d / _binaryBodyLength;
        Analysis = $"Loaded binary range {offset}–{end}.";
    }

    private bool CanApplyParameter => Selected is not null && SelectedParameter is not null;

    [RelayCommand(CanExecute = nameof(CanApplyParameter))]
    private Task ApplyParameterAsync() => ExecuteAsync(_ =>
    {
        var parameter = SelectedParameter ?? throw new InvalidOperationException("Select a parameter first.");
        var raw = ParameterSide.Equals("response", StringComparison.OrdinalIgnoreCase) ? ResponseEditor : RequestEditor;
        var updated = _service.SetParameter(raw, parameter.Location, parameter.Name,
            parameter.Occurrence, ParameterValue);
        if (ParameterSide.Equals("response", StringComparison.OrdinalIgnoreCase)) ResponseEditor = updated;
        else RequestEditor = updated;
        Analysis = $"Updated {parameter.Location} parameter {parameter.Name}[{parameter.Occurrence}]. Submit with Continue, Fulfill or Replay.";
        return Task.CompletedTask;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task SaveAnnotationAsync() => ExecuteAsync(async ct =>
    {
        var saved = await _service.SaveAnnotationAsync(Selected!.Id, AnnotationStarred, AnnotationTags,
            AnnotationNote, AnnotationStatus, ct);
        ApplyAnnotation(saved);
        Analysis = $"Annotation saved (revision {saved.Revision}).";
    });

    [RelayCommand(CanExecute = nameof(CanResolveIntercept))]
    private Task ContinueAsync() => ResolveAsync(ct => _service.ResolveContinueAsync(Selected!.Id, RequestEditor, ct));

    [RelayCommand(CanExecute = nameof(CanResolveIntercept))]
    private Task DropAsync() => ResolveAsync(ct => _service.ResolveDropAsync(Selected!.Id, ct));

    [RelayCommand(CanExecute = nameof(CanResolveIntercept))]
    private Task FulfillAsync() => ResolveAsync(ct => _service.ResolveFulfillAsync(Selected!.Id, ResponseEditor, ct));

    private async Task RunAsync(Func<CancellationToken, Task<TrafficOperationResult>> action, bool applyResponse)
    {
        await ExecuteAsync(async ct =>
        {
            var result = await action(ct).ConfigureAwait(true);
            Analysis = result.Summary;
            if (applyResponse && result.ResponseText is not null)
                ResponseEditor = result.ResponseText;
        }).ConfigureAwait(true);
    }

    private Task ResolveAsync(Func<CancellationToken, Task<TrafficPacketCommitResult>> action) => ExecuteAsync(async ct =>
    {
        var result = await action(ct).ConfigureAwait(true);
        Analysis = result.Summary;
        if (!result.Success) return;
        LoadAudit();
        Refresh();
    });

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        IsBusy = true;
        try { await action(_operation.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { Analysis = "Operation cancelled"; }
        catch (Exception ex) { Analysis = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void OnServiceChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var selectedId = Selected?.Id;
        var page = _service.Query(CreateFilter());
        var items = page.Items.ToArray();
        Visible.Clear();
        foreach (var item in items) Visible.Add(item);
        if (selectedId is not null) Selected = items.FirstOrDefault(x => x.Id == selectedId);
        Summary = $"{items.Length} shown / {page.Total} total · page {PageIndex + 1}";
    }

    private TrafficExchangeFilter CreateFilter() => new(
        NullIfEmpty(FilterText), NullIfEmpty(MethodFilter), int.TryParse(StatusFilter, out var status) ? status : null,
        NullIfEmpty(ResourceTypeFilter), OnlyIntercepted, PageIndex * PageSize, PageSize);

    private void LoadParameters()
    {
        Parameters.Clear();
        if (Selected is null) return;
        var raw = ParameterSide.Equals("response", StringComparison.OrdinalIgnoreCase) ? ResponseEditor : RequestEditor;
        try { foreach (var item in _service.ReadParameters(raw)) Parameters.Add(item); }
        catch (Exception) { }
    }

    private void LoadAnnotation()
    {
        if (Selected is null) { ApplyAnnotation(null); return; }
        ApplyAnnotation(_service.GetAnnotation(Selected.Id));
    }

    private void LoadAudit()
    {
        AuditEntries.Clear();
        if (Selected is null) return;
        foreach (var item in _service.GetAudit(Selected.Id)) AuditEntries.Add(item);
    }

    private void ApplyHistoryOverview(TrafficHistoryOverview value)
    {
        HistoryMaxEntries = value.MaxEntries.ToString();
        HistoryMaxBytes = value.MaxBytes.ToString();
        HistoryRetentionDays = value.RetentionDays.ToString();
        HistoryAutoPrune = value.AutoPrune;
        HistoryStatus = $"{value.EntryCount} entries · estimated {value.EstimatedBytes} B · file {value.FileBytes} B · " +
                        $"oldest {value.Oldest?.ToLocalTime().ToString("g") ?? "-"} · newest {value.Newest?.ToLocalTime().ToString("g") ?? "-"}" +
                        string.Concat(value.SiteQuotas.Select(quota =>
                            $"{Environment.NewLine}{quota.HostPattern}: {quota.MaxEntries} entries / {quota.MaxBytes} B"));
    }

    private async Task LoadBinaryDraftStatusAsync(CancellationToken cancellationToken)
    {
        BinaryDraftStatus = await _service.GetBinaryDraftStatusAsync(Selected!.Id, BinarySide, cancellationToken)
            ?? "No pending binary edit.";
    }

    private async Task LoadBinaryBodyInfoAsync(CancellationToken cancellationToken)
    {
        var info = await _service.GetBinaryBodyInfoAsync(Selected!.Id, BinarySide, cancellationToken);
        _binaryBodyLength = info.Length;
        _binaryInfoLoaded = true;
        _binaryLoadedCount = 0;
        BinaryOffset = "0";
        BinaryBodySummary = $"{info.Length:N0} bytes · SHA-256 {info.Sha256} · {info.ContentType ?? "unknown content type"}" +
                            (string.IsNullOrWhiteSpace(info.Charset) ? "" : $" · charset {info.Charset}");
        BinaryProgress = 0;
        BinaryRangeStatus = info.Length == 0 ? "Empty body." : "No chunk loaded.";
    }

    private async Task LoadBinaryBodyInfoSafelyAsync(string exchangeId, string side)
    {
        if (!side.Equals("request", StringComparison.OrdinalIgnoreCase) &&
            !side.Equals("response", StringComparison.OrdinalIgnoreCase))
        {
            BinaryBodySummary = "Side must be request or response.";
            return;
        }
        try
        {
            var info = await _service.GetBinaryBodyInfoAsync(exchangeId, side, CancellationToken.None);
            if (Selected?.Id != exchangeId || !BinarySide.Equals(side, StringComparison.OrdinalIgnoreCase)) return;
            _binaryBodyLength = info.Length;
            _binaryInfoLoaded = true;
            _binaryLoadedCount = 0;
            BinaryBodySummary = $"{info.Length:N0} bytes · SHA-256 {info.Sha256} · {info.ContentType ?? "unknown content type"}" +
                                (string.IsNullOrWhiteSpace(info.Charset) ? "" : $" · charset {info.Charset}");
            BinaryRangeStatus = info.Length == 0 ? "Empty body." : "No chunk loaded.";
        }
        catch (Exception exception) { BinaryBodySummary = $"Body info unavailable: {exception.Message}"; }
    }

    private void ResetBinaryNavigation()
    {
        _binaryBodyLength = 0;
        _binaryLoadedCount = 0;
        _binaryInfoLoaded = false;
        BinaryOffset = "0";
        BinaryBodySummary = Selected is null ? "Body info not loaded." : "Loading body info…";
        BinaryRangeStatus = "No chunk loaded.";
        BinaryProgress = 0;
    }

    private long ParseBinaryOffset() => long.TryParse(BinaryOffset, out var value)
        ? value : throw new ArgumentException("Binary offset must be an integer.");
    private int ParseBinaryCount() => int.TryParse(BinaryCount, out var value) && value > 0
        ? value : throw new ArgumentException("Binary count must be a positive integer.");

    private void ApplyAnnotation(TrafficAnnotationItem? value)
    {
        AnnotationStarred = value?.Starred ?? false;
        AnnotationTags = value?.Tags ?? string.Empty;
        AnnotationNote = value?.Note ?? string.Empty;
        AnnotationStatus = value?.Status ?? "Unreviewed";
    }
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var delimiter = 4;
        if (split < 0) { split = raw.IndexOf("\n\n", StringComparison.Ordinal); delimiter = 2; }
        var body = split >= 0 ? raw[(split + delimiter)..].Trim() : raw.Trim();
        try
        {
            using var json = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return raw; }
    }

    protected override void OnDispose()
    {
        _service.Changed -= OnServiceChanged;
        _operation?.Cancel();
        _operation?.Dispose();
    }
}
