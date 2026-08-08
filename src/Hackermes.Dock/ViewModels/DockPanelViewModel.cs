using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hackermes.Dock.ViewModels;

/// <summary>单个 Dock 区域的 Tab 集合与选中状态。</summary>
public partial class DockPanelViewModel : ObservableObject
{
    private readonly IEventBus _eventBus;

    public DockPanelViewModel(DockPosition position, IEventBus eventBus)
    {
        Position = position;
        _eventBus = eventBus;
    }

    public DockPosition Position { get; }

    public ObservableCollection<DockTabItemViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private DockTabItemViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isVisible = true;

    public bool HasTabs => Tabs.Count > 0;

    public void Add(DockTabItemViewModel tab, bool select = true)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var existing = Find(tab.Id);
        if (existing is not null)
        {
            if (select)
                SelectedTab = existing;
            return;
        }

        tab.CloseCommand ??= new RelayCommand<DockTabItemViewModel>(RequestClose);
        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasTabs));

        if (select || SelectedTab is null)
            SelectedTab = tab;
    }

    public DockTabItemViewModel? Find(string tabId) =>
        Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));

    public bool Select(string tabId)
    {
        var tab = Find(tabId);
        if (tab is null)
            return false;

        SelectedTab = tab;
        return true;
    }

    /// <summary>
    /// 请求关闭。先发可取消事件征询订阅方意见 —— 内容可能有未保存的更改。
    /// </summary>
    private void RequestClose(DockTabItemViewModel? tab)
    {
        if (tab is null || !tab.IsClosable)
            return;

        var request = new TabCloseRequestedEvent(Position, tab.Id);
        _eventBus.Publish(request);

        if (request.Cancel)
            return;

        Remove(tab.Id);
    }

    public void Remove(string tabId)
    {
        var tab = Find(tabId);
        if (tab is null)
            return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        OnPropertyChanged(nameof(HasTabs));

        if (ReferenceEquals(SelectedTab, tab))
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];

        // 内容的释放时机只有这里。切 Tab 不释放 —— 叠层保活的前提就是内容不随切页销毁。
        TabContentLifetime.Release(tab.Content);

        _eventBus.Publish(new TabClosedEvent(Position, tabId, tab.Content));
    }

    public void RemoveAll()
    {
        foreach (var tab in Tabs.ToArray())
            Remove(tab.Id);
    }
}
