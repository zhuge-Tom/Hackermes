using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using Hackermes.Inspector.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hackermes.Inspector.ViewModels;

public partial class ConsolePanelViewModel : ViewModelBase
{
    private readonly ConsoleStore _store;

    public ConsolePanelViewModel(ConsoleStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    public ObservableCollection<ConsoleEntry> Visible { get; } = [];

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _errorsOnly;

    [ObservableProperty]
    private string _summary = "尚无输出";

    partial void OnFilterTextChanged(string value) => Refresh();

    partial void OnErrorsOnlyChanged(bool value) => Refresh();

    [RelayCommand]
    private void Clear() => _store.Clear();

    private void OnStoreChanged() => Refresh();

    private void Refresh()
    {
        var keyword = FilterText?.Trim();
        var query = _store.Entries.AsEnumerable();

        if (ErrorsOnly)
            query = query.Where(e => e.Level is "error" or "warn");

        if (!string.IsNullOrEmpty(keyword))
            query = query.Where(e => e.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var filtered = query.ToArray();

        Visible.Clear();
        foreach (var entry in filtered)
            Visible.Add(entry);

        var errors = _store.Entries.Count(e => e.Level == "error");
        var warns = _store.Entries.Count(e => e.Level == "warn");

        Summary = _store.Entries.Count == 0
            ? "尚无输出"
            : $"共 {_store.Entries.Count} 条 · 错误 {errors} · 警告 {warns}";
    }

    protected override void OnDispose() => _store.Changed -= OnStoreChanged;
}
