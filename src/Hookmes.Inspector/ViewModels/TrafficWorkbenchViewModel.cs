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
    Task<TrafficOperationResult> ReplayAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task DropAsync(string exchangeId, CancellationToken cancellationToken);
    Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken);
    Task<string> CreateRepeaterAsync(string exchangeId, CancellationToken cancellationToken);
    Task<string> EditBinaryBodyAsync(string exchangeId, string side, string kind, long offset, long count,
        string data, string encoding, CancellationToken cancellationToken);
    Task<string> ReadBinaryBodyAsync(string exchangeId, string side, long offset, int count,
        string encoding, CancellationToken cancellationToken);
    Task<string?> GetBinaryDraftStatusAsync(string exchangeId, string side, CancellationToken cancellationToken);
    Task<bool> DiscardBinaryDraftAsync(string exchangeId, string side, CancellationToken cancellationToken);
    Task<int> ExportArchiveFileAsync(string path, string? filter, CancellationToken cancellationToken);
    Task<int> ImportArchiveFileAsync(string path, CancellationToken cancellationToken);
    IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket);
    string SetParameter(string rawPacket, string location, string name, int occurrence, string value);
    TrafficAnnotationItem? GetAnnotation(string exchangeId);
    Task<TrafficAnnotationItem> SaveAnnotationAsync(string exchangeId, bool starred, string tags,
        string note, string status, CancellationToken cancellationToken);
}

public sealed record TrafficParameterItem(string Location, string Name, string Value, int Occurrence);
public sealed record TrafficAnnotationItem(bool Starred, string Tags, string Note, string Status, int Revision);

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
    private CancellationTokenSource? _operation;

    public TrafficWorkbenchViewModel(ITrafficWorkbenchService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        _isInterceptEnabled = service.IsInterceptEnabled;
        _isResponseInterceptEnabled = service.IsResponseInterceptEnabled;
        Refresh();
    }

    public ObservableCollection<TrafficExchange> Visible { get; } = [];
    public ObservableCollection<TrafficParameterItem> Parameters { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ReplayCommand), nameof(SendToRepeaterCommand), nameof(LoadBinaryChunkCommand), nameof(ApplyBinaryEditCommand), nameof(RefreshBinaryDraftCommand), nameof(DiscardBinaryDraftCommand), nameof(ApplyParameterCommand), nameof(SaveAnnotationCommand), nameof(ContinueCommand), nameof(DropCommand), nameof(FulfillCommand))]
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
    [ObservableProperty] private string _formattedRequest = string.Empty;
    [ObservableProperty] private string _formattedResponse = string.Empty;
    [ObservableProperty] private string _analysis = "Select a request, edit it, then Analyze or Replay.";
    [ObservableProperty] private string _summary = "No traffic captured";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(DropCommand), nameof(FulfillCommand))]
    private bool _isBusy;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private string _binarySide = "request";
    [ObservableProperty] private string _binaryKind = "replace";
    [ObservableProperty] private string _binaryOffset = "0";
    [ObservableProperty] private string _binaryCount = "0";
    [ObservableProperty] private string _binaryEncoding = "hex";
    [ObservableProperty] private string _binaryData = string.Empty;
    [ObservableProperty] private string _binaryDraftStatus = "No pending binary edit.";
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
    partial void OnSelectedParameterChanged(TrafficParameterItem? value) => ParameterValue = value?.Value ?? string.Empty;

    [RelayCommand] private void Reload() => Refresh();
    [RelayCommand] private Task ExportArchiveAsync() => ExecuteAsync(async ct =>
    {
        var count = await _service.ExportArchiveFileAsync(ArchivePath.Trim(),
            string.IsNullOrWhiteSpace(ArchiveFilter) ? null : ArchiveFilter.Trim(), ct);
        Analysis = $"Exported {count} packet(s) to {ArchivePath.Trim()}.";
    });
    [RelayCommand] private Task ImportArchiveAsync() => ExecuteAsync(async ct =>
    {
        var count = await _service.ImportArchiveFileAsync(ArchivePath.Trim(), ct);
        Analysis = $"Imported {count} packet(s) from {ArchivePath.Trim()}.";
    });
    [RelayCommand] private void PreviousPage() { if (PageIndex > 0) { PageIndex--; Refresh(); } }
    [RelayCommand] private void NextPage()
    {
        if ((PageIndex + 1) * PageSize < _service.Query(CreateFilter()).Total) { PageIndex++; Refresh(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AnalyzeAsync() => RunAsync(ct => _service.AnalyzeAsync(Selected!.Id, RequestEditor, ct), applyResponse: false);

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
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RefreshBinaryDraftAsync() => ExecuteAsync(LoadBinaryDraftStatusAsync);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DiscardBinaryDraftAsync() => ExecuteAsync(async ct =>
    {
        var discarded = await _service.DiscardBinaryDraftAsync(Selected!.Id, BinarySide, ct);
        BinaryDraftStatus = discarded ? "Pending binary edit discarded; original body and headers restored." : "No pending binary edit.";
        Analysis = BinaryDraftStatus;
        if (discarded) Refresh();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task LoadBinaryChunkAsync() => ExecuteAsync(async ct =>
    {
        if (!long.TryParse(BinaryOffset, out var offset) || !int.TryParse(BinaryCount, out var count))
            throw new ArgumentException("Binary offset and count must be integers.");
        if (count <= 0) count = 64 * 1024;
        BinaryData = await _service.ReadBinaryBodyAsync(Selected!.Id, BinarySide, offset, count, BinaryEncoding, ct);
        Analysis = $"Loaded binary chunk at offset {offset}.";
    });

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
    private Task ContinueAsync() => ResolveAsync(ct => _service.ContinueAsync(Selected!.Id, RequestEditor, ct), "Request continued");

    [RelayCommand(CanExecute = nameof(CanResolveIntercept))]
    private Task DropAsync() => ResolveAsync(ct => _service.DropAsync(Selected!.Id, ct), "Request dropped");

    [RelayCommand(CanExecute = nameof(CanResolveIntercept))]
    private Task FulfillAsync() => ResolveAsync(ct => _service.FulfillAsync(Selected!.Id, ResponseEditor, ct), "Custom response fulfilled");

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

    private Task ResolveAsync(Func<CancellationToken, Task> action, string success) => ExecuteAsync(async ct =>
    {
        await action(ct).ConfigureAwait(true);
        Analysis = success;
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

    private async Task LoadBinaryDraftStatusAsync(CancellationToken cancellationToken)
    {
        BinaryDraftStatus = await _service.GetBinaryDraftStatusAsync(Selected!.Id, BinarySide, cancellationToken)
            ?? "No pending binary edit.";
    }

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
