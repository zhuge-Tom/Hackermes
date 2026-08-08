using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.ViewModels;
using Hackermes.Browser.Views;
using Hackermes.Cdp.Session;
using Hackermes.Dock.Controls;
using Hackermes.Platform.Events;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hackermes.Browser.Services;

/// <summary>浏览器标签页的创建与登记。</summary>
public interface IBrowserTabManager
{
    IReadOnlyList<string> OpenPageIds { get; }

    /// <summary>新建标签页并切换过去。</summary>
    string OpenTab(string? url = null);
}

public sealed class BrowserTabManager : IBrowserTabManager
{
    private readonly IEventBus _eventBus;
    private readonly ICdpSessionRegistry _registry;
    private readonly WebViewCreationCoordinator _coordinator;
    private readonly PageAgentInjector _agentInjector;
    private readonly ISettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly List<string> _openPageIds = [];
    private readonly object _gate = new();

    public BrowserTabManager(
        IEventBus eventBus,
        ICdpSessionRegistry registry,
        WebViewCreationCoordinator coordinator,
        PageAgentInjector agentInjector,
        ISettingsService settings,
        IAppLogger logger)
    {
        _eventBus = eventBus;
        _registry = registry;
        _coordinator = coordinator;
        _agentInjector = agentInjector;
        _settings = settings;
        _logger = logger.ForCategory(nameof(BrowserTabManager));

        _eventBus.Subscribe<TabClosedEvent>(OnTabClosed);
    }

    public IReadOnlyList<string> OpenPageIds
    {
        get
        {
            lock (_gate)
            {
                return _openPageIds.ToArray();
            }
        }
    }

    public string OpenTab(string? url = null)
    {
        var target = BrowserTabViewModel.NormalizeUrl(url) is { Length: > 0 } normalized
            ? normalized
            : _settings.Load().Browser.HomePage;

        var pageId = "page-" + Guid.NewGuid().ToString("N")[..8];

        var viewModel = new BrowserTabViewModel(pageId, target);
        var view = new BrowserTabView(viewModel, _eventBus, _registry, _coordinator, _agentInjector, _logger);

        var tab = new DockTabItemViewModel
        {
            Id = pageId,
            Title = "新标签页",
            Icon = IconHelper.GetIcon("SemiIconGlobe", "SemiIconLink", "SemiIconWindowAdaptive"),
            IsClosable = true,
            // 必须不可重载:WebView2 离开可视树就会被销毁。
            Content = TabContent.NonReloadable(view)
        };

        // 标题跟随页面变化,并广播出去 —— 其他模块(如控制台的上下文提示符)
        // 需要知道当前页面叫什么,不能只有 Dock 自己知道。
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BrowserTabViewModel.Title))
                return;

            tab.Title = viewModel.Title;
            _eventBus.Publish(new UpdateDockTabTitleEvent(pageId, viewModel.Title));
        };

        lock (_gate)
        {
            _openPageIds.Add(pageId);
        }

        _eventBus.Publish(new AddDockTabEvent(DockPosition.Content, tab));
        _logger.Info($"新建标签页 {pageId} → {target}");

        return pageId;
    }

    private void OnTabClosed(TabClosedEvent e)
    {
        lock (_gate)
        {
            _openPageIds.Remove(e.TabId);
        }
    }
}
