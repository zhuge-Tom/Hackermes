using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Base.Mvvm;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Dock.ViewModels;

/// <summary>
/// 五区域布局的大脑:把各模块的 Tab 注册变成实际的面板内容,并负责懒物化与布局持久化。
/// </summary>
public partial class DockLayoutViewModel : ViewModelBase
{
    private readonly IDockLayoutRegistry _registry;
    private readonly IEventBus _eventBus;
    private readonly ISettingsService _settingsService;
    private readonly IAppLogger _logger;

    /// <summary>按需注册的 Tab(IsDefaultVisible=false),等到有人请求切过去时才建壳。</summary>
    private readonly Dictionary<string, DockTabRegistration> _lazyRegistrations = new(StringComparer.Ordinal);

    /// <summary>壳 → 工厂。物化时用它拿到真正的内容。</summary>
    private readonly Dictionary<string, DockTabRegistration> _shellFactories = new(StringComparer.Ordinal);

    private readonly HashSet<string> _materialized = new(StringComparer.Ordinal);

    private CancellationTokenSource? _saveDebounce;
    private bool _restoring;
    private bool _startupComplete;

    public DockLayoutViewModel(
        IDockLayoutRegistry registry,
        IEventBus eventBus,
        ISettingsService settingsService,
        IAppLogger logger)
    {
        _registry = registry;
        _eventBus = eventBus;
        _settingsService = settingsService;
        _logger = logger.ForCategory(nameof(DockLayoutViewModel));

        Left = CreatePanel(DockPosition.Left);
        Right = CreatePanel(DockPosition.Right);
        Bottom = CreatePanel(DockPosition.Bottom);
        Content = CreatePanel(DockPosition.Content);

        SubscribeEvent<AddDockTabEvent>(_eventBus, OnAddDockTab);
        SubscribeEvent<RemoveDockTabEvent>(_eventBus, e => GetPanel(e.Region).Remove(e.TabId));
        SubscribeEvent<SwitchDockTabRequestedEvent>(_eventBus, OnSwitchTabRequested);
        SubscribeEvent<UpdateDockTabTitleEvent>(_eventBus, OnUpdateTitle);
    }

    public DockPanelViewModel Left { get; }
    public DockPanelViewModel Right { get; }
    public DockPanelViewModel Bottom { get; }
    public DockPanelViewModel Content { get; }

    private DockPanelViewModel CreatePanel(DockPosition position)
    {
        var panel = new DockPanelViewModel(position, _eventBus);
        panel.PropertyChanged += OnPanelPropertyChanged;
        return panel;
    }

    public DockPanelViewModel GetPanel(DockPosition position) => position switch
    {
        DockPosition.Left => Left,
        DockPosition.Right => Right,
        DockPosition.Bottom => Bottom,
        _ => Content
    };

    #region 注册 → 壳

    /// <summary>
    /// 把注册表里的声明变成 Tab 壳。
    /// <para>
    /// 这里<strong>只建标题和图标</strong>,不调用 <c>CreateTab</c> ——
    /// 十几个模块的内容如果在启动时全部构造,首屏会明显卡顿。
    /// </para>
    /// </summary>
    public async Task ApplyRegistrationsAsync()
    {
        var created = 0;

        foreach (var registration in _registry.GetRegistrations())
        {
            if (registration.Region == DockPosition.Setting)
                continue;

            if (!registration.IsDefaultVisible)
            {
                _lazyRegistrations[registration.TabId] = registration;
                continue;
            }

            AddShell(registration);

            // 每建几个壳让出一帧,让启动画面保持响应。
            if (++created % 3 == 0)
                await StartupPerformance.YieldUiFramesAsync();
        }

        RestoreLayout();
    }

    private DockTabItemViewModel AddShell(DockTabRegistration registration)
    {
        var panel = GetPanel(registration.Region);

        var existing = panel.Find(registration.TabId);
        if (existing is not null)
            return existing;

        var shell = new DockTabItemViewModel
        {
            Id = registration.TabId,
            Title = registration.Title,
            Icon = IconHelper.GetIcon(registration.IconKey),
            IsClosable = registration.IsClosable,
            Content = null
        };

        _shellFactories[registration.TabId] = registration;
        panel.Add(shell, select: false);

        return shell;
    }

    #endregion

    #region 懒物化

    /// <summary>
    /// 确保 Tab 的内容已经构造。触发时机是"Tab 被选中且所在面板可见"。
    /// </summary>
    private void EnsureTabMaterialized(DockPanelViewModel panel, DockTabItemViewModel? tab)
    {
        if (tab is null || !panel.IsVisible)
            return;

        if (!_materialized.Add(tab.Id))
            return;

        if (!_shellFactories.TryGetValue(tab.Id, out var registration))
            return;

        try
        {
            var real = registration.CreateTab();

            tab.Content = real.Content;

            // 工厂可能给出比注册声明更具体的标题与图标(例如带文件名)。
            if (!string.IsNullOrEmpty(real.Title))
                tab.Title = real.Title;
            if (real.Icon is not null)
                tab.Icon = real.Icon;
        }
        catch (Exception ex)
        {
            _materialized.Remove(tab.Id);
            _logger.Error($"Tab '{tab.Id}' 内容构造失败", ex);

            // 降级成一条错误提示而不是让整个面板开天窗。
            tab.Content = new Avalonia.Controls.TextBlock
            {
                Text = $"加载失败: {ex.Message}",
                Margin = new Avalonia.Thickness(12),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DockPanelViewModel panel)
            return;

        switch (e.PropertyName)
        {
            case nameof(DockPanelViewModel.SelectedTab):
                EnsureTabMaterialized(panel, panel.SelectedTab);

                if (panel.Position == DockPosition.Content)
                {
                    _eventBus.Publish(new ActiveContentTabChangedEvent(
                        panel.SelectedTab?.Id, panel.SelectedTab?.Title));
                }

                ScheduleSaveLayout();
                break;

            case nameof(DockPanelViewModel.IsVisible):
                // 面板从隐藏变可见时,当前选中的 Tab 才真正需要内容。
                EnsureTabMaterialized(panel, panel.SelectedTab);
                _eventBus.Publish(new DockPanelVisibilityChangedEvent(panel.Position, panel.IsVisible));
                ScheduleSaveLayout();
                break;
        }
    }

    #endregion

    #region 事件处理

    private void OnAddDockTab(AddDockTabEvent e)
    {
        var panel = GetPanel(e.Region);

        // 动态添加的 Tab 自带内容,不走物化流程。
        _materialized.Add(e.Tab.Id);
        panel.Add(e.Tab);

        if (!panel.IsVisible)
            panel.IsVisible = true;
    }

    private void OnSwitchTabRequested(SwitchDockTabRequestedEvent e)
    {
        var panel = GetPanel(e.Region);

        if (!panel.IsVisible)
            panel.IsVisible = true;

        if (panel.Select(e.TabId))
            return;

        // 目标是按需注册的隐藏 Tab,现在才建壳。
        if (_lazyRegistrations.TryGetValue(e.TabId, out var registration))
        {
            var shell = AddShell(registration);
            panel.SelectedTab = shell;
        }
    }

    private void OnUpdateTitle(UpdateDockTabTitleEvent e)
    {
        foreach (var position in new[]
                 {
                     DockPosition.Left, DockPosition.Right,
                     DockPosition.Bottom, DockPosition.Content
                 })
        {
            var tab = GetPanel(position).Find(e.TabId);
            if (tab is null)
                continue;

            tab.Title = e.Title;
            return;
        }
    }

    #endregion

    #region 布局持久化

    /// <summary>启动装配完成。在此之前的所有变更都不触发保存,避免把中间状态写进配置。</summary>
    public void CompleteStartup()
    {
        _startupComplete = true;
        _logger.Debug("Dock 布局启动装配完成");
    }

    private void ScheduleSaveLayout()
    {
        if (_restoring || !_startupComplete)
            return;

        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = new CancellationTokenSource();

        var token = _saveDebounce.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            UiThreadBridge.Post(SaveLayout, DispatcherPriority.Background);
        }, token);
    }

    private void SaveLayout()
    {
        try
        {
            _settingsService.Update(settings =>
            {
                var layout = settings.Layout;

                layout.LeftPanelVisible = Left.IsVisible;
                layout.RightPanelVisible = Right.IsVisible;
                layout.BottomPanelVisible = Bottom.IsVisible;

                layout.LeftSelectedTabId = Left.SelectedTab?.Id;
                layout.RightSelectedTabId = Right.SelectedTab?.Id;
                layout.BottomSelectedTabId = Bottom.SelectedTab?.Id;
                layout.ContentSelectedTabId = Content.SelectedTab?.Id;
            });
        }
        catch (Exception ex)
        {
            _logger.Error("保存布局失败", ex);
        }
    }

    private void RestoreLayout()
    {
        _restoring = true;

        try
        {
            var layout = _settingsService.Load().Layout;

            Left.IsVisible = layout.LeftPanelVisible;
            Right.IsVisible = layout.RightPanelVisible;
            Bottom.IsVisible = layout.BottomPanelVisible;

            SelectIfPresent(Left, layout.LeftSelectedTabId);
            SelectIfPresent(Right, layout.RightSelectedTabId);
            SelectIfPresent(Bottom, layout.BottomSelectedTabId);
            SelectIfPresent(Content, layout.ContentSelectedTabId);
        }
        catch (Exception ex)
        {
            _logger.Error("恢复布局失败,使用默认布局", ex);
        }
        finally
        {
            _restoring = false;
        }

        // 恢复期间抑制了物化,这里补上当前选中项。
        foreach (var panel in new[] { Left, Right, Bottom, Content })
            EnsureTabMaterialized(panel, panel.SelectedTab);
    }

    private static void SelectIfPresent(DockPanelViewModel panel, string? tabId)
    {
        if (!string.IsNullOrEmpty(tabId))
            panel.Select(tabId);
    }

    #endregion

    protected override void OnDispose()
    {
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();

        foreach (var panel in new[] { Left, Right, Bottom, Content })
            panel.PropertyChanged -= OnPanelPropertyChanged;
    }
}
