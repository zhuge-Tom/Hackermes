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
    bool IsInterceptEnabled { get; set; }
    event Action? Changed;
    Task<TrafficOperationResult> AnalyzeAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task<TrafficOperationResult> ReplayAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken);
    Task DropAsync(string exchangeId, CancellationToken cancellationToken);
    Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken);
}

public sealed record TrafficExchange(
    string Id,
    DateTimeOffset Timestamp,
    string Method,
    string Url,
    int? Status,
    string RequestText,
    string ResponseText,
    bool IsIntercepted = false)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : Url;
    public string StatusText => Status?.ToString() ?? (IsIntercepted ? "HOLD" : "—");
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
}

public sealed record TrafficOperationResult(bool Success, string Summary, string? ResponseText = null);

public partial class TrafficWorkbenchViewModel : ViewModelBase
{
    private readonly ITrafficWorkbenchService _service;
    private CancellationTokenSource? _operation;

    public TrafficWorkbenchViewModel(ITrafficWorkbenchService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        _isInterceptEnabled = service.IsInterceptEnabled;
        Refresh();
    }

    public ObservableCollection<TrafficExchange> Visible { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ReplayCommand), nameof(ContinueCommand), nameof(DropCommand), nameof(FulfillCommand))]
    private TrafficExchange? _selected;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _onlyIntercepted;
    [ObservableProperty] private bool _isInterceptEnabled;
    [ObservableProperty] private string _requestEditor = string.Empty;
    [ObservableProperty] private string _responseEditor = string.Empty;
    [ObservableProperty] private string _formattedRequest = string.Empty;
    [ObservableProperty] private string _formattedResponse = string.Empty;
    [ObservableProperty] private string _analysis = "Select a request, edit it, then Analyze or Replay.";
    [ObservableProperty] private string _summary = "No traffic captured";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(DropCommand), nameof(FulfillCommand))]
    private bool _isBusy;

    public bool HasSelection => Selected is not null;
    public bool CanResolveIntercept => Selected?.IsIntercepted == true && !IsBusy;

    partial void OnSelectedChanged(TrafficExchange? value)
    {
        RequestEditor = value?.RequestText ?? string.Empty;
        ResponseEditor = value?.ResponseText ?? string.Empty;
        FormattedRequest = FormatMessage(RequestEditor);
        FormattedResponse = FormatMessage(ResponseEditor);
        Analysis = value is null ? "Select a request, edit it, then Analyze or Replay." : $"{value.Method} {value.Url}";
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanResolveIntercept));
    }

    partial void OnFilterTextChanged(string value) => Refresh();
    partial void OnOnlyInterceptedChanged(bool value) => Refresh();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanResolveIntercept));
    partial void OnIsInterceptEnabledChanged(bool value) => _service.IsInterceptEnabled = value;
    partial void OnRequestEditorChanged(string value) => FormattedRequest = FormatMessage(value);
    partial void OnResponseEditorChanged(string value) => FormattedResponse = FormatMessage(value);

    [RelayCommand] private void Reload() => Refresh();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AnalyzeAsync() => RunAsync(ct => _service.AnalyzeAsync(Selected!.Id, RequestEditor, ct), applyResponse: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ReplayAsync() => RunAsync(ct => _service.ReplayAsync(Selected!.Id, RequestEditor, ct), applyResponse: true);

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
        var filter = FilterText.Trim();
        IEnumerable<TrafficExchange> query = _service.Exchanges;
        if (OnlyIntercepted) query = query.Where(x => x.IsIntercepted);
        if (filter.Length > 0)
            query = query.Where(x => x.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Method.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var items = query.OrderByDescending(x => x.Timestamp).ToArray();
        Visible.Clear();
        foreach (var item in items) Visible.Add(item);
        if (selectedId is not null) Selected = items.FirstOrDefault(x => x.Id == selectedId);
        Summary = $"{items.Length} shown / {_service.Exchanges.Count} total";
    }

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
