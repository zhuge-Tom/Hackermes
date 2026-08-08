using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace Hackermes.Dock.Controls;

/// <summary>
/// 双模式 Tab 内容宿主。这是整个 Dock 层的地基。
/// <list type="bullet">
/// <item><b>可重载</b> — 内容挂在 <c>PART_SelectedContentHost</c>,切 Tab 卸载/重挂,与原生 TabControl 一致。</item>
/// <item><b>不可重载</b> — 内容挂在 <c>PART_NonReloadableOverlay</c> 叠层上<b>永不卸载</b>,切 Tab 只改显隐。</item>
/// </list>
/// <para>
/// 为什么需要它:Avalonia 原生 <see cref="TabControl"/> 切页会卸载可视树。
/// WebView2 与 PTY 一旦离开可视树就会被销毁,重新挂回来时页面已经白屏、终端会话已经死掉。
/// 叠层保活是绕开这一点的最小代价方案 —— 比引入完整 Dock 框架再打补丁可控得多。
/// </para>
/// </summary>
public class PersistTabControl : TabControl
{
    public const string PartTabStrip = "PART_TabStrip";
    public const string PartTabStripHost = "PART_TabStripHost";
    public const string PartTabStripLayout = "PART_TabStripLayout";
    public const string PartScrollViewer = "PART_ScrollViewer";
    public const string PartTabStripActions = "PART_TabStripActions";
    public const string PartBorderSeparator = "PART_BorderSeparator";
    public const string PartNonReloadableOverlay = "PART_NonReloadableOverlay";
    public const string PartSelectedContentHost = "PART_SelectedContentHost";

    public static readonly StyledProperty<IDataTemplate?> PersistContentTemplateProperty =
        AvaloniaProperty.Register<PersistTabControl, IDataTemplate?>(nameof(PersistContentTemplate));

    public static readonly StyledProperty<Control?> TabStripRightContentProperty =
        AvaloniaProperty.Register<PersistTabControl, Control?>(nameof(TabStripRightContent));

    public static readonly StyledProperty<bool> IsTabStripVisibleProperty =
        AvaloniaProperty.Register<PersistTabControl, bool>(nameof(IsTabStripVisible), true);

    public IDataTemplate? PersistContentTemplate
    {
        get => GetValue(PersistContentTemplateProperty);
        set => SetValue(PersistContentTemplateProperty, value);
    }

    /// <summary>标签栏右侧的工具区(新建按钮、当前 Tab 上交的工具面板)。</summary>
    public Control? TabStripRightContent
    {
        get => GetValue(TabStripRightContentProperty);
        set => SetValue(TabStripRightContentProperty, value);
    }

    public bool IsTabStripVisible
    {
        get => GetValue(IsTabStripVisibleProperty);
        set => SetValue(IsTabStripVisibleProperty, value);
    }

    private static readonly FuncTemplate<Panel?> HorizontalTabItemsPanel =
        new(() => new StackPanel { Orientation = Orientation.Horizontal });

    private static readonly FuncTemplate<Panel?> VerticalTabItemsPanel =
        new(() => new StackPanel { Orientation = Orientation.Vertical });

    private TabStrip? _tabStrip;
    private Panel? _tabStripHost;
    private Grid? _tabStripLayout;
    private ScrollViewer? _scrollViewer;
    private ContentPresenter? _tabStripActions;
    private Border? _borderSeparator;
    private Panel? _nonReloadableOverlay;
    private ContentPresenter? _selectedContentHost;

    private INotifyCollectionChanged? _itemsNotify;
    private DockTabItemViewModel? _watchedTab;
    private PropertyChangedEventHandler? _watchedTabHandler;
    private bool _syncingSelection;
    private bool _updatingLayer;
    private bool _activationNotifyPosted;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachParts();
        base.OnApplyTemplate(e);

        _tabStrip = e.NameScope.Find<TabStrip>(PartTabStrip);
        if (_tabStrip is not null)
            _tabStrip.SelectionChanged += OnTabStripSelectionChanged;

        _tabStripHost = e.NameScope.Find<Panel>(PartTabStripHost);
        _tabStripLayout = e.NameScope.Find<Grid>(PartTabStripLayout);
        _scrollViewer = e.NameScope.Find<ScrollViewer>(PartScrollViewer);
        _tabStripActions = e.NameScope.Find<ContentPresenter>(PartTabStripActions);
        _borderSeparator = e.NameScope.Find<Border>(PartBorderSeparator);
        _selectedContentHost = e.NameScope.Find<ContentPresenter>(PartSelectedContentHost);
        _nonReloadableOverlay = e.NameScope.Find<Panel>(PartNonReloadableOverlay);

        SyncContentTemplate();
        ApplyTabStripLayout();
        ApplyTabStripVisibility();
        ApplyItemsSourceToTabStrip();
        HookItemsCollectionChanges();
        SyncTabStripFromSelectedItem();
        PostUpdateLayer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachParts();
        base.OnDetachedFromVisualTree(e);
    }

    private void DetachParts()
    {
        if (_tabStrip is not null)
        {
            _tabStrip.SelectionChanged -= OnTabStripSelectionChanged;
            _tabStrip = null;
        }

        HookItemsCollectionChanges(null);
        UnhookTabWatcher();

        _tabStripHost = null;
        _tabStripLayout = null;
        _scrollViewer = null;
        _tabStripActions = null;
        _borderSeparator = null;
        _nonReloadableOverlay = null;
        _selectedContentHost = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PersistContentTemplateProperty)
        {
            SyncContentTemplate();
            PostUpdateLayer();
        }
        else if (change.Property == SelectedItemProperty)
        {
            SyncTabStripFromSelectedItem();
            PostUpdateLayer();
        }
        else if (change.Property == ItemsSourceProperty)
        {
            ApplyItemsSourceToTabStrip();
            HookItemsCollectionChanges();
            SyncTabStripFromSelectedItem();
            PostUpdateLayer();
        }
        else if (change.Property == TabStripPlacementProperty)
        {
            ApplyTabStripLayout();
        }
        else if (change.Property == IsTabStripVisibleProperty)
        {
            ApplyTabStripVisibility();
        }
    }

    #region 标签栏

    private void ApplyTabStripLayout()
    {
        if (_tabStripLayout is null || _scrollViewer is null || _tabStripActions is null)
            return;

        var vertical = TabStripPlacement is Avalonia.Controls.Dock.Left or Avalonia.Controls.Dock.Right;

        _tabStripLayout.ColumnDefinitions.Clear();
        _tabStripLayout.RowDefinitions.Clear();

        if (vertical)
        {
            _tabStripLayout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            _tabStripLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _tabStripLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            Grid.SetRow(_scrollViewer, 0);
            Grid.SetColumn(_scrollViewer, 0);
            Grid.SetRow(_tabStripActions, 1);
            Grid.SetColumn(_tabStripActions, 0);

            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scrollViewer.VerticalContentAlignment = VerticalAlignment.Stretch;

            if (_tabStrip is not null)
            {
                _tabStrip.ItemsPanel = VerticalTabItemsPanel;
                _tabStrip.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            if (_borderSeparator is not null)
            {
                _borderSeparator.Width = 1;
                _borderSeparator.ClearValue(HeightProperty);
                _borderSeparator.HorizontalAlignment = TabStripPlacement == Avalonia.Controls.Dock.Left
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left;
                _borderSeparator.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }
        else
        {
            _tabStripLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _tabStripLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            Grid.SetRow(_scrollViewer, 0);
            Grid.SetColumn(_scrollViewer, 0);
            Grid.SetRow(_tabStripActions, 0);
            Grid.SetColumn(_tabStripActions, 1);

            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.VerticalContentAlignment = VerticalAlignment.Center;

            if (_tabStrip is not null)
            {
                _tabStrip.ItemsPanel = HorizontalTabItemsPanel;
                _tabStrip.HorizontalAlignment = HorizontalAlignment.Left;
            }

            if (_borderSeparator is not null)
            {
                _borderSeparator.ClearValue(WidthProperty);
                _borderSeparator.Height = 1;
                _borderSeparator.HorizontalAlignment = HorizontalAlignment.Stretch;
                _borderSeparator.VerticalAlignment = VerticalAlignment.Bottom;
            }
        }
    }

    private void ApplyTabStripVisibility()
    {
        if (_tabStripHost is not null)
            _tabStripHost.IsVisible = IsTabStripVisible;
    }

    private void OnTabStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _tabStrip is null)
            return;

        try
        {
            _syncingSelection = true;
            SelectedItem = _tabStrip.SelectedItem;
        }
        finally
        {
            _syncingSelection = false;
        }

        PostUpdateLayer();
    }

    private void SyncTabStripFromSelectedItem()
    {
        if (_tabStrip is null)
            return;

        try
        {
            _syncingSelection = true;
            if (!ReferenceEquals(_tabStrip.SelectedItem, SelectedItem))
                _tabStrip.SelectedItem = SelectedItem;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void ApplyItemsSourceToTabStrip()
    {
        var source = ResolveItems();

        if (_tabStrip is not null && !ReferenceEquals(_tabStrip.ItemsSource, source))
            _tabStrip.ItemsSource = source;
    }

    #endregion

    #region 集合监听

    private IEnumerable ResolveItems() => ItemsSource ?? Items;

    private void HookItemsCollectionChanges(INotifyCollectionChanged? notifier = null)
    {
        notifier ??= ResolveItems() as INotifyCollectionChanged;

        if (_itemsNotify is not null)
        {
            _itemsNotify.CollectionChanged -= OnItemsCollectionChanged;
            _itemsNotify = null;
        }

        _itemsNotify = notifier;
        if (_itemsNotify is not null)
            _itemsNotify.CollectionChanged += OnItemsCollectionChanged;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        PostUpdateLayer();

    #endregion

    #region 内容层同步

    private void SyncContentTemplate()
    {
        if (PersistContentTemplate is not null)
            ContentTemplate = PersistContentTemplate;
    }

    // 用 Loaded 优先级推迟一拍:集合变更与选中项变更常常成对到达,
    // 合并成一次更新可以避免中间态引起的闪烁与重复挂载。
    private void PostUpdateLayer() =>
        Dispatcher.UIThread.Post(UpdateLayer, DispatcherPriority.Loaded);

    private void UpdateLayer()
    {
        if (_updatingLayer)
            return;

        try
        {
            _updatingLayer = true;

            ApplyTabStripVisibility();
            SyncNonReloadableOverlay();
            SyncSelectedContentHost();
            PostNotifyTabActivated();
        }
        finally
        {
            _updatingLayer = false;
        }
    }

    /// <summary>
    /// 同步可重载 Tab 的内容。
    /// <para>
    /// 模板里没有 <c>PART_ItemsPresenter</c>,基类那套"用 TabItem 填充选中内容"的机制不生效,
    /// 因此这里手动把内容挂到宿主上。直接挂控件而非套一层 ContentPresenter,
    /// 是为了避免切 Tab 时反复卸挂同一个控件导致的短暂空白。
    /// </para>
    /// </summary>
    private void SyncSelectedContentHost()
    {
        if (_selectedContentHost is null)
            return;

        // 不可重载的内容由叠层负责,内容宿主必须让出位置,否则会盖住叠层。
        if (SelectedItem is not DockTabItemViewModel tab || tab.Content is INonReloadableTabShell)
        {
            UnhookTabWatcher();
            _selectedContentHost.Content = null;
            _selectedContentHost.ContentTemplate = null;
            return;
        }

        WatchTabContentChanges(tab);

        if (tab.Content is Control control)
        {
            if (ReferenceEquals(_selectedContentHost.Content, control))
                return;

            _selectedContentHost.ContentTemplate = null;
            _selectedContentHost.Content = null;
            DockControlHost.DetachFromVisualTree(control);
            _selectedContentHost.Content = control;
        }
        else if (!ReferenceEquals(_selectedContentHost.Content, tab))
        {
            _selectedContentHost.ContentTemplate = PersistContentTemplate ?? ContentTemplate;
            _selectedContentHost.Content = tab;
        }
    }

    // Tab 的内容可能在物化后才被赋值(壳先建、内容后填),需要监听变更补一次挂载。
    private void WatchTabContentChanges(DockTabItemViewModel tab)
    {
        if (ReferenceEquals(_watchedTab, tab))
            return;

        UnhookTabWatcher();

        _watchedTab = tab;
        _watchedTabHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(DockTabItemViewModel.Content))
                PostUpdateLayer();
        };
        _watchedTab.PropertyChanged += _watchedTabHandler;
    }

    private void UnhookTabWatcher()
    {
        if (_watchedTab is not null && _watchedTabHandler is not null)
            _watchedTab.PropertyChanged -= _watchedTabHandler;

        _watchedTab = null;
        _watchedTabHandler = null;
    }

    /// <summary>
    /// 叠层同步:所有不可重载内容都挂在叠层上,只有选中的那个可见。
    /// 已经不在 Tab 列表里的内容从叠层移除并释放资源 —— 这是它们唯一的释放时机。
    /// </summary>
    private void SyncNonReloadableOverlay()
    {
        if (_nonReloadableOverlay is null)
            return;

        var alive = new HashSet<Control>();
        Control? selectedContent = null;
        INonReloadableTabShell? selectedShell = null;

        if (SelectedItem is DockTabItemViewModel selectedTab
            && selectedTab.Content is INonReloadableTabShell shell)
        {
            selectedShell = shell;
            selectedContent = shell.PersistedContent;
        }

        foreach (var (itemShell, content) in EnumerateShells())
        {
            alive.Add(content);
            EnsureChildOfOverlay(content);

            if (ReferenceEquals(content, selectedContent))
                continue;

            content.IsVisible = false;
            content.IsHitTestVisible = false;
            itemShell.OnTabBecameHidden();
        }

        if (selectedContent is not null)
        {
            EnsureChildOfOverlay(selectedContent);
            selectedContent.IsVisible = true;
            selectedContent.IsHitTestVisible = true;
            selectedShell?.OnTabBecameVisible();
        }

        for (var i = _nonReloadableOverlay.Children.Count - 1; i >= 0; i--)
        {
            if (_nonReloadableOverlay.Children[i] is { } child && !alive.Contains(child))
            {
                TabContentLifetime.Release(child);
                _nonReloadableOverlay.Children.RemoveAt(i);
            }
        }
    }

    private IEnumerable<(INonReloadableTabShell Shell, Control Content)> EnumerateShells()
    {
        foreach (var item in ResolveItems())
        {
            if (item is not DockTabItemViewModel tab)
                continue;
            if (tab.Content is not INonReloadableTabShell shell)
                continue;
            if (shell.PersistedContent is not { } content)
                continue;

            yield return (shell, content);
        }
    }

    private void EnsureChildOfOverlay(Control content)
    {
        if (_nonReloadableOverlay is null || ReferenceEquals(content.Parent, _nonReloadableOverlay))
            return;

        DockControlHost.DetachFromVisualTree(content);
        _nonReloadableOverlay.Children.Add(content);
    }

    #endregion

    #region 激活通知

    private void PostNotifyTabActivated()
    {
        if (_activationNotifyPosted)
            return;

        _activationNotifyPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _activationNotifyPosted = false;
            NotifyTabActivatedCore();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 通知内容"你被激活了"。某些内容(表格、编辑器)在隐藏状态下量不出尺寸,
    /// 需要在重新可见时刷一次布局。
    /// </summary>
    private void NotifyTabActivatedCore()
    {
        if (SelectedItem is not DockTabItemViewModel tab)
            return;

        var target = NonReloadableTabContent.Resolve(tab.Content)
                     ?? _selectedContentHost?.Child
                     ?? tab.Content as Control;

        if (target is null)
            return;

        if (target is ITabActivationAware aware)
        {
            aware.OnTabActivated();
            return;
        }

        if (target.DataContext is ITabActivationAware dataAware)
        {
            dataAware.OnTabActivated();
            return;
        }

        // 内容常被包在一层布局容器里,向下找一层。
        foreach (var descendant in target.GetVisualDescendants().OfType<Control>())
        {
            if (descendant is ITabActivationAware nested)
            {
                nested.OnTabActivated();
                return;
            }
        }
    }

    #endregion

    #region 内容查找

    /// <summary>取某个 Tab 当前实际承载内容的控件。</summary>
    public Control? FindContentHostForItem(object? item)
    {
        if (item is not DockTabItemViewModel tab)
            return ReferenceEquals(item, SelectedItem) ? _selectedContentHost?.Child : null;

        if (NonReloadableTabContent.Resolve(tab.Content) is { } persisted)
            return persisted;

        if (ReferenceEquals(item, SelectedItem) && _selectedContentHost?.Child is { } live)
            return live;

        return tab.Content as Control;
    }

    public TControl? FindContentForItem<TControl>(object? item) where TControl : Control
    {
        var host = FindContentHostForItem(item);

        return host switch
        {
            null => null,
            TControl exact => exact,
            _ => host.GetVisualDescendants().OfType<TControl>().FirstOrDefault()
        };
    }

    public TControl? FindSelectedContent<TControl>() where TControl : Control =>
        FindContentForItem<TControl>(SelectedItem);

    #endregion
}
