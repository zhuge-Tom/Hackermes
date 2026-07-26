using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hookmes.Base.Mvvm;
using Hookmes.Inspector.Models;
using Hookmes.Inspector.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hookmes.Inspector.ViewModels;

public partial class NetworkPanelViewModel : ViewModelBase
{
    private readonly NetworkStore _store;

    public NetworkPanelViewModel(NetworkStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    /// <summary>过滤后的视图。直接绑 store 的集合会让过滤无处安放。</summary>
    public ObservableCollection<NetworkEntry> Visible { get; } = [];

    [ObservableProperty]
    private NetworkEntry? _selected;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _onlyFailed;

    [ObservableProperty]
    private string _summary = "尚无请求";

    partial void OnFilterTextChanged(string value) => Refresh();

    partial void OnOnlyFailedChanged(bool value) => Refresh();

    [RelayCommand]
    private void Clear() => _store.Clear();

    private void OnStoreChanged() => Refresh();

    private void Refresh()
    {
        var keyword = FilterText?.Trim();

        var query = _store.Entries.AsEnumerable();

        if (OnlyFailed)
            query = query.Where(e => e.IsFailed);

        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(e =>
                e.Url.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || e.Method.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || e.ResourceType.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToArray();

        // 整体重建而不是增量 diff:2000 条上限下重建足够快,而增量同步的
        // 边界条件(过滤条件变化 + 并发插入)很容易出错。
        Visible.Clear();
        foreach (var entry in filtered)
            Visible.Add(entry);

        var total = _store.Entries.Count;
        var failed = _store.Entries.Count(e => e.IsFailed);
        var withStack = _store.Entries.Count(e => !string.IsNullOrEmpty(e.InitiatorStack));

        Summary = total == 0
            ? "尚无请求"
            : $"共 {total} 条 · 失败 {failed} · 含调用栈 {withStack}" +
              (filtered.Length == total ? string.Empty : $" · 显示 {filtered.Length}");
    }

    protected override void OnDispose() => _store.Changed -= OnStoreChanged;
}
