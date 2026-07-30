using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hookmes.Base.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Inspector.ViewModels;

public sealed record TrafficRuleItem(
    string Id, string UrlPattern, string Method, string Stage, string Behavior, bool Enabled);

public sealed record TrafficRuleDraft(
    string Id, string UrlPattern, string? Method, string Stage, string Behavior);

public interface ITrafficRuleWorkbenchService
{
    IReadOnlyList<TrafficRuleItem> Rules { get; }
    event Action? RulesChanged;
    Task AddRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken);
    Task SetRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);
    Task RemoveRuleAsync(string id, CancellationToken cancellationToken);
    Task MoveRuleAsync(string id, int targetIndex, CancellationToken cancellationToken);
    Task ExportRulesFileAsync(string path, CancellationToken cancellationToken);
    Task<int> ImportRulesFileAsync(string path, bool merge, CancellationToken cancellationToken);
}

public partial class TrafficRulesViewModel : ViewModelBase
{
    private readonly ITrafficRuleWorkbenchService _service;

    public TrafficRulesViewModel(ITrafficRuleWorkbenchService service)
    {
        _service = service;
        _service.RulesChanged += Refresh;
        Refresh();
    }

    public ObservableCollection<TrafficRuleItem> Rules { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand), nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private TrafficRuleItem? _selected;
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _urlPattern = "*";
    [ObservableProperty] private string _method = string.Empty;
    [ObservableProperty] private string _stage = "request";
    [ObservableProperty] private string _behavior = "pause";
    [ObservableProperty] private string _rulesFilePath = "traffic-rules.json";
    [ObservableProperty] private bool _mergeImport;
    [ObservableProperty] private string _status = "Persistent rules are evaluated in list order.";
    [ObservableProperty] private bool _isBusy;

    public bool HasSelection => Selected is not null && !IsBusy;

    partial void OnSelectedChanged(TrafficRuleItem? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasSelection));

    [RelayCommand]
    private async Task AddAsync() => await ExecuteAsync(async ct =>
    {
        await _service.AddRuleAsync(new TrafficRuleDraft(
            Id.Trim(), string.IsNullOrWhiteSpace(UrlPattern) ? "*" : UrlPattern.Trim(),
            string.IsNullOrWhiteSpace(Method) ? null : Method.Trim(), Stage, Behavior), ct);
        Status = $"Rule '{Id.Trim()}' added.";
        Id = string.Empty;
    });

    [RelayCommand]
    private Task ExportRulesAsync() => ExecuteAsync(async ct =>
    {
        await _service.ExportRulesFileAsync(RulesFilePath.Trim(), ct);
        Status = $"Rules exported to {RulesFilePath.Trim()}.";
    });

    [RelayCommand]
    private Task ImportRulesAsync() => ExecuteAsync(async ct =>
    {
        var count = await _service.ImportRulesFileAsync(RulesFilePath.Trim(), MergeImport, ct);
        Status = $"Imported {count} rule(s) from {RulesFilePath.Trim()} ({(MergeImport ? "merge" : "replace")}).";
    });

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
