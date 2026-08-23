using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Inspector.ViewModels;

public sealed record TrafficRuleItem(
    string Id, string UrlPattern, string Method, string Stage, string Behavior, bool Enabled);

public sealed record TrafficRuleHeaderEdit(string Name, string Value);

/// <summary>
/// Form-level rule draft. The optional request/response payloads back the "edit" and
/// "fulfill" behaviors; they are absent for simple pause/drop rules.
/// </summary>
public sealed record TrafficRuleDraft(
    string Id, string UrlPattern, string? Method, string Stage, string Behavior,
    string? RequestUrl = null, string? RequestMethod = null,
    IReadOnlyList<TrafficRuleHeaderEdit>? RequestHeaders = null, string? RequestBody = null,
    int? ResponseStatus = null, string? ResponseStatusText = null,
    IReadOnlyList<TrafficRuleHeaderEdit>? ResponseHeaders = null, string? ResponseBody = null);

public interface ITrafficRuleWorkbenchService
{
    IReadOnlyList<TrafficRuleItem> Rules { get; }
    event Action? RulesChanged;
    Task AddRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken);
    Task UpdateRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken);
    Task<TrafficRuleDraft?> GetRuleAsync(string id, CancellationToken cancellationToken);
    Task SetRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);
    Task RemoveRuleAsync(string id, CancellationToken cancellationToken);
    Task MoveRuleAsync(string id, int targetIndex, CancellationToken cancellationToken);
    Task ExportRulesFileAsync(string path, CancellationToken cancellationToken);
    Task<int> ImportRulesFileAsync(string path, bool merge, CancellationToken cancellationToken);
}

public partial class TrafficRulesViewModel : ViewModelBase
{
    private readonly ITrafficRuleWorkbenchService _service;
    private readonly IRecentTrafficPathService? _recentPaths;
    public InspectorFileDialogDelegates? FileDialogs { get; set; }
    private static readonly InspectorFileType[] RuleFileTypes = [new("Traffic rules JSON (*.json)", ["*.json"])];
    private string? _loadedRuleId;

    public TrafficRulesViewModel(ITrafficRuleWorkbenchService service, IRecentTrafficPathService? recentPaths = null)
    {
        _service = service;
        _recentPaths = recentPaths;
        if (!string.IsNullOrWhiteSpace(recentPaths?.LastRulesPath))
            _rulesFilePath = recentPaths.LastRulesPath;
        _service.RulesChanged += Refresh;
        Refresh();
    }

    public ObservableCollection<TrafficRuleItem> Rules { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand), nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(LoadSelectedCommand))]
    private TrafficRuleItem? _selected;
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _urlPattern = "*";
    [ObservableProperty] private string _method = string.Empty;
    [ObservableProperty] private string _stage = "request";
    [ObservableProperty] private string _behavior = "pause";
    [ObservableProperty] private string _requestUrl = string.Empty;
    [ObservableProperty] private string _requestMethod = string.Empty;
    [ObservableProperty] private string _requestHeaderText = string.Empty;
    [ObservableProperty] private string _requestBodyText = string.Empty;
    [ObservableProperty] private string _responseStatus = string.Empty;
    [ObservableProperty] private string _responseStatusText = string.Empty;
    [ObservableProperty] private string _responseHeaderText = string.Empty;
    [ObservableProperty] private string _responseBodyText = string.Empty;
    [ObservableProperty] private string _rulesFilePath = "traffic-rules.json";
    [ObservableProperty] private bool _mergeImport;
    [ObservableProperty] private string _status = "Persistent rules are evaluated in list order.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedCommand), nameof(SaveChangesCommand))]
    private bool _isBusy;

    public bool HasSelection => Selected is not null && !IsBusy;
    public bool HasLoadedRule => LoadedRuleId is not null && !IsBusy;
    public string? LoadedRuleId => _loadedRuleId;

    partial void OnSelectedChanged(TrafficRuleItem? value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasLoadedRule));
    }

    /// <summary>Parses one header per line ("Name: value"); empty lines are skipped.</summary>
    internal static IReadOnlyList<TrafficRuleHeaderEdit> ParseHeaderLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var headers = new List<TrafficRuleHeaderEdit>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon < 1 || colon == line.Length - 1)
                throw new FormatException($"Header line {index + 1} must look like 'Name: value'.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length is 0 or > 256 || value.Length > 8192)
                throw new FormatException($"Header line {index + 1} exceeds the allowed name/value length.");
            headers.Add(new TrafficRuleHeaderEdit(name, value));
        }
        return headers;
    }

    internal static string FormatHeaderLines(IReadOnlyList<TrafficRuleHeaderEdit>? headers) =>
        headers is null || headers.Count == 0 ? string.Empty :
        string.Join(Environment.NewLine, headers.Select(header => $"{header.Name}: {header.Value}"));

    private TrafficRuleDraft BuildDraft()
    {
        var behavior = Behavior.Trim().ToLowerInvariant();
        var isFulfill = behavior == "fulfill";
        int? status = null;
        if (isFulfill && !string.IsNullOrWhiteSpace(ResponseStatus))
        {
            if (!int.TryParse(ResponseStatus.Trim(), out var parsed) || parsed is < 100 or > 999)
                throw new FormatException("Response status must be an integer between 100 and 999.");
            status = parsed;
        }
        return new TrafficRuleDraft(
            Id.Trim(), string.IsNullOrWhiteSpace(UrlPattern) ? "*" : UrlPattern.Trim(),
            string.IsNullOrWhiteSpace(Method) || Method.Trim() == "*" ? null : Method.Trim(),
            string.IsNullOrWhiteSpace(Stage) ? "request" : Stage.Trim(), behavior,
            NonEmpty(RequestUrl), NonEmpty(RequestMethod),
            behavior == "edit" ? ParseHeaderLines(RequestHeaderText) : null, NonEmpty(RequestBodyText),
            status, isFulfill ? NonEmpty(ResponseStatusText) : null,
            isFulfill ? ParseHeaderLines(ResponseHeaderText) : null, NonEmpty(ResponseBodyText));
    }

    private static string? NonEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private async Task AddAsync() => await ExecuteAsync(async ct =>
    {
        var draft = BuildDraft();
        await _service.AddRuleAsync(draft, ct);
        Status = $"Rule '{draft.Id}' added.";
        Id = string.Empty;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task LoadSelectedAsync() => ExecuteAsync(async ct =>
    {
        var draft = await _service.GetRuleAsync(Selected!.Id, ct);
        if (draft is null)
        {
            Status = $"Rule '{Selected.Id}' was not found.";
            return;
        }
        _loadedRuleId = draft.Id;
        OnPropertyChanged(nameof(LoadedRuleId));
        SaveChangesCommand.NotifyCanExecuteChanged();
        Id = draft.Id;
        UrlPattern = draft.UrlPattern;
        Method = draft.Method ?? "*";
        Stage = draft.Stage;
        Behavior = draft.Behavior;
        RequestUrl = draft.RequestUrl ?? string.Empty;
        RequestMethod = draft.RequestMethod ?? string.Empty;
        RequestHeaderText = FormatHeaderLines(draft.RequestHeaders);
        RequestBodyText = draft.RequestBody ?? string.Empty;
        ResponseStatus = draft.ResponseStatus?.ToString() ?? string.Empty;
        ResponseStatusText = draft.ResponseStatusText ?? string.Empty;
        ResponseHeaderText = FormatHeaderLines(draft.ResponseHeaders);
        ResponseBodyText = draft.ResponseBody ?? string.Empty;
        Status = $"Loaded rule '{draft.Id}' into the editor.";
    });

    [RelayCommand(CanExecute = nameof(HasLoadedRule))]
    private Task SaveChangesAsync() => ExecuteAsync(async ct =>
    {
        if (LoadedRuleId is null)
        {
            Status = "Load a rule into the editor before saving changes.";
            return;
        }
        var draft = BuildDraft() with { Id = LoadedRuleId };
        await _service.UpdateRuleAsync(draft, ct);
        Status = $"Rule '{draft.Id}' updated.";
    });

    [RelayCommand]
    private Task ExportRulesAsync() => ExecuteAsync(async ct =>
    {
        var path = RulesFilePath.Trim();
        if (FileDialogs is not null)
        {
            path = await FileDialogs.SaveAsync(new InspectorFileDialogRequest(
                "Export traffic rules", path, RuleFileTypes), ct) ?? string.Empty;
            if (path.Length == 0) return;
            RulesFilePath = path;
        }
        path = NormalizeRulesPath(path);
        await _service.ExportRulesFileAsync(path, ct);
        RememberRulesPath(path);
        Status = $"Rules exported to {path}.";
    });

    [RelayCommand]
    private Task ImportRulesAsync() => ExecuteAsync(async ct =>
    {
        var path = RulesFilePath.Trim();
        if (FileDialogs is not null)
        {
            path = await FileDialogs.OpenAsync(new InspectorFileDialogRequest(
                "Import traffic rules", path, RuleFileTypes), ct) ?? string.Empty;
            if (path.Length == 0) return;
            RulesFilePath = path;
            if (!MergeImport && !await FileDialogs.ConfirmAsync(
                    $"Replace all current traffic rules with rules from this file?{Environment.NewLine}{path}", ct))
                return;
        }
        path = NormalizeRulesPath(path);
        var count = await _service.ImportRulesFileAsync(path, MergeImport, ct);
        RememberRulesPath(path);
        Status = $"Imported {count} rule(s) from {path} ({(MergeImport ? "merge" : "replace")}).";
    });

    private string NormalizeRulesPath(string path) => _recentPaths?.NormalizePath(path) ?? path.Trim();
    private void RememberRulesPath(string path)
    {
        RulesFilePath = path;
        _recentPaths?.RememberRulesPath(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleAsync() => ExecuteAsync(async ct =>
    {
        await _service.SetRuleEnabledAsync(Selected!.Id, !Selected.Enabled, ct);
        Status = $"Rule '{Selected.Id}' {(!Selected.Enabled ? "enabled" : "disabled")}.";
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RemoveAsync() => ExecuteAsync(async ct =>
    {
        var id = Selected!.Id;
        await _service.RemoveRuleAsync(id, ct);
        if (_loadedRuleId == id)
        {
            _loadedRuleId = null;
            OnPropertyChanged(nameof(LoadedRuleId));
            SaveChangesCommand.NotifyCanExecuteChanged();
        }
        Status = $"Rule '{id}' removed.";
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveUpAsync() => MoveAsync(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveDownAsync() => MoveAsync(1);

    private Task MoveAsync(int delta) => ExecuteAsync(async ct =>
    {
        var index = Rules.IndexOf(Selected!);
        var target = Math.Clamp(index + delta, 0, Rules.Count - 1);
        await _service.MoveRuleAsync(Selected!.Id, target, ct);
        Status = $"Rule '{Selected.Id}' moved to {target}.";
    });

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        try { await action(CancellationToken.None); }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void Refresh()
    {
        var selectedId = Selected?.Id;
        Rules.Clear();
        foreach (var rule in _service.Rules) Rules.Add(rule);
        if (selectedId is not null)
            foreach (var rule in Rules)
                if (rule.Id == selectedId) { Selected = rule; break; }
    }

    protected override void OnDispose() => _service.RulesChanged -= Refresh;
}
