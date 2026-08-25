using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.Services;
using Hackermes.Browser.ViewModels;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Browser.Views;

/// <summary>
/// 浏览器标签页。承载 <see cref="NativeWebView"/> 并在适配器就绪后建立 CDP 会话。
/// <para>
/// 实现 <see cref="INonReloadableTabHost"/>:内容由 Dock 叠层保活,切 Tab 不卸载 ——
/// WebView2 一旦离开可视树就会被销毁。
/// </para>
/// </summary>
public partial class BrowserTabView : UserControl, INonReloadableTabHost, ITabContentReleasable
{
    /// <summary>
    /// 适配器就绪的探测节奏。
    /// <para>
    /// 为什么不只依赖 <c>AdapterCreated</c> 事件:该事件在部分时序下不会触发
    /// (例如控件在适配器创建完成之后才挂上处理器),因此采用事件加轮询的双保险。
    /// 宁可多探几次,也不要一个永远白屏的标签页。
    /// </para>
    /// </summary>
    private static readonly int[] ProbeDelaysMs = [150, 400, 800, 1500, 3000, 6000];

    private readonly IAppLogger _logger;
    private readonly IEventBus _eventBus;
    private readonly ICdpSessionRegistry _registry;
    private readonly WebViewCreationCoordinator _coordinator;
    private readonly PageAgentInjector _agentInjector;
    private readonly ISettingsService _settings;

    private Panel? _host;
    private NativeWebView? _webView;
    private Button? _proxyButton;
    private ContextMenu? _proxyMenu;
    private MenuItem? _directProxyItem;
    private MenuItem? _burpProxyItem;
    private MenuItem? _proxyStatusItem;
    private MenuItem? _telemetryFilterItem;
    private BrowserTabViewModel? _vm;
    private CdpSession? _session;
    private IDisposable? _registration;
    private IDisposable? _pickerStateSubscription;
    private IDisposable? _deviceModeRequestSubscription;
    private IDisposable? _proxyModeSubscription;
    private IDisposable? _telemetryFilterSubscription;
    private IDisposable? _creationLease;
    private BrowserDeviceProfile? _appliedDeviceProfile;
    private BrowserProxyMode _activeProxyMode;
    private bool _suppressKnownTelemetry;

    private bool _cdpAttached;
    private bool _released;
    private bool _proxySwitching;
    private string? _pendingNavigation;

    public BrowserTabView(
        BrowserTabViewModel viewModel,
        IEventBus eventBus,
        ICdpSessionRegistry registry,
        WebViewCreationCoordinator coordinator,
        PageAgentInjector agentInjector,
        ISettingsService settings,
        IAppLogger logger)
    {
        InitializeComponent();

        _vm = viewModel;
        _eventBus = eventBus;
        _registry = registry;
        _coordinator = coordinator;
        _agentInjector = agentInjector;
        _settings = settings;
        _logger = logger.ForCategory($"Browser:{viewModel.PageId}");
        _activeProxyMode = BrowserProxyConfiguration.ParseMode(settings.Load().Browser.ProxyMode);
        _suppressKnownTelemetry = settings.Load().Browser.SuppressKnownTelemetry;

        DataContext = viewModel;
        _host = this.FindControl<Panel>("PART_WebViewHost");
        _proxyButton = this.FindControl<Button>("PART_ProxyButton");
        InitializeProxyMenu();

        viewModel.NavigateRequested += OnNavigateRequested;
        viewModel.ReloadRequested += OnReloadRequested;
        viewModel.BackRequested += OnBackRequested;
        viewModel.ForwardRequested += OnForwardRequested;
        viewModel.DevToolsRequested += OnDevToolsRequested;
        viewModel.SelfTestRequested += OnSelfTestRequested;
        viewModel.ElementPickerRequested += OnElementPickerRequested;
        viewModel.DeviceEmulationRequested += OnDeviceEmulationRequested;
        _pickerStateSubscription = eventBus.SubscribeDisposable<ElementPickerStateChangedEvent>(OnPickerStateChanged);
        _deviceModeRequestSubscription = eventBus.SubscribeDisposable<BrowserDeviceModeToggleRequestedEvent>(OnDeviceModeToggleRequested);
        _proxyModeSubscription = eventBus.SubscribeDisposable<BrowserProxyModeChangedEvent>(OnProxyModeChanged);
        _telemetryFilterSubscription = eventBus.SubscribeDisposable<BrowserTelemetryFilterChangedEvent>(OnTelemetryFilterChanged);

        if (_host is not null)
            _host.SizeChanged += OnWebViewHostSizeChanged;

        _pendingNavigation = viewModel.CurrentUrl;

        Loaded += OnLoaded;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    #region 内部浏览器代理插件

    private void InitializeProxyMenu()
    {
        if (_proxyButton is null)
            return;

        _directProxyItem = new MenuItem { Header = "直连（不使用系统代理）" };
        _burpProxyItem = new MenuItem { Header = $"Burp 代理 · {BrowserProxyConfiguration.BurpEndpoint}" };
        _proxyStatusItem = new MenuItem { Header = "检测 Burp 监听器" };
        _telemetryFilterItem = new MenuItem { Header = "过滤已知页面遥测" };
        var caItem = new MenuItem { Header = "打开 Burp CA 页面" };

        _directProxyItem.Click += OnDirectProxySelected;
        _burpProxyItem.Click += OnBurpProxySelected;
        _proxyStatusItem.Click += OnCheckBurpListener;
        _telemetryFilterItem.Click += OnTelemetryFilterClicked;
        caItem.Click += OnOpenBurpCertificatePage;

        _proxyMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                _directProxyItem,
                _burpProxyItem,
                new Separator(),
                _telemetryFilterItem,
                _proxyStatusItem,
                caItem
            }
        };
        _proxyButton.ContextMenu = _proxyMenu;
        UpdateProxyMenuState();
    }

    private void OnProxyButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_proxyButton is not null)
            _proxyMenu?.Open(_proxyButton);
    }

    private void OnDirectProxySelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ChangeProxyMode(BrowserProxyMode.Direct);

    private void OnBurpProxySelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ChangeProxyMode(BrowserProxyMode.Burp);

    private void ChangeProxyMode(BrowserProxyMode mode)
    {
        if (mode == _activeProxyMode || _proxySwitching)
            return;

        var saved = _settings.Update(
            settings => settings.Browser.ProxyMode = BrowserProxyConfiguration.ToSetting(mode),
            SettingsSection.Browser);

        if (!saved)
        {
            SetStatus("代理模式保存失败，浏览器配置未改变。");
            return;
        }

        _eventBus.Publish(new BrowserProxyModeChangedEvent(mode));
    }

    private void OnProxyModeChanged(BrowserProxyModeChangedEvent changed) =>
        UiThreadBridge.Post(() => _ = RecreateWebViewForProxyAsync(changed.Mode));

    private void OnTelemetryFilterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var enabled = !_suppressKnownTelemetry;
        if (!_settings.Update(settings => settings.Browser.SuppressKnownTelemetry = enabled, SettingsSection.Browser))
        {
            SetStatus("页面遥测过滤设置保存失败。");
            return;
        }

        _eventBus.Publish(new BrowserTelemetryFilterChangedEvent(enabled));
    }

    private void OnTelemetryFilterChanged(BrowserTelemetryFilterChangedEvent changed)
    {
        _suppressKnownTelemetry = changed.Enabled;
        UpdateProxyMenuState();
        if (_session is not null)
            _ = ApplyTelemetryFilterAsync(_session, changed.Enabled);
    }

    private async Task ApplyTelemetryFilterAsync(CdpSession session, bool enabled)
    {
        try
        {
            await session.SendAsync(
                "Network.setBlockedURLs",
                BrowserTrafficNoiseFilter.BuildSetBlockedUrlsParameters(enabled)).ConfigureAwait(false);
            UiThreadBridge.Post(() => SetStatus(enabled
                ? "已过滤已知页面遥测；普通网站请求仍会进入代理。"
                : "页面遥测过滤已关闭；所有网站请求都会进入代理。"));
        }
        catch (Exception ex)
        {
            _logger.Warn($"设置页面遥测过滤失败: {ex.Message}");
            UiThreadBridge.Post(() => SetStatus($"页面遥测过滤设置失败: {ex.Message}"));
        }
    }

    private async Task RecreateWebViewForProxyAsync(BrowserProxyMode mode)
    {
        if (_released || _proxySwitching || mode == _activeProxyMode)
            return;

        _proxySwitching = true;

        try
        {
            _pendingNavigation = _vm?.CurrentUrl is { Length: > 0 } current
                ? current
                : _vm?.AddressText;
            _activeProxyMode = mode;
            UpdateProxyMenuState();
            SetStatus($"正在切换到 {BrowserProxyConfiguration.Create(mode).DisplayName}…");

            DisposeCurrentWebView();
            if (_vm is not null)
            {
                _vm.IsCdpReady = false;
                _vm.IsAgentReady = false;
            }

            _cdpAttached = false;
            _appliedDeviceProfile = null;
            await CreateWebViewAsync().ConfigureAwait(true);
        }
        finally
        {
            _proxySwitching = false;
        }
    }

    private async void OnCheckBurpListener(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_proxyStatusItem is null)
            return;

        _proxyStatusItem.IsEnabled = false;
        _proxyStatusItem.Header = $"正在检测 {BrowserProxyConfiguration.BurpEndpoint}…";

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            await client.ConnectAsync(IPAddress.Loopback, BrowserProxyConfiguration.BurpPort, timeout.Token);
            _proxyStatusItem.Header = "● Burp 监听器可连接";
            SetStatus($"Burp 监听器在线：{BrowserProxyConfiguration.BurpEndpoint}");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            _proxyStatusItem.Header = "○ Burp 监听器未启动（点击重试）";
            SetStatus($"无法连接 Burp：请确认监听器已启动在 {BrowserProxyConfiguration.BurpEndpoint}");
        }
        finally
        {
            _proxyStatusItem.IsEnabled = true;
        }
    }

    private void OnOpenBurpCertificatePage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        const string certificatePage = "http://burpsuite";

        if (_activeProxyMode == BrowserProxyMode.Burp)
        {
            if (_vm is not null)
                _vm.AddressText = certificatePage;
            OnNavigateRequested(certificatePage);
            return;
        }

        if (_vm is not null)
        {
            _vm.AddressText = certificatePage;
            _vm.CurrentUrl = certificatePage;
        }
        ChangeProxyMode(BrowserProxyMode.Burp);
    }

    private void UpdateProxyMenuState()
    {
        var configuration = BrowserProxyConfiguration.Create(_activeProxyMode);

        if (_proxyButton is not null)
        {
            _proxyButton.Content = configuration.DisplayName;
            ToolTip.SetTip(_proxyButton,
                _activeProxyMode == BrowserProxyMode.Burp
                    ? $"内部浏览器正在通过 {BrowserProxyConfiguration.BurpEndpoint} 连接 Burp"
                    : "内部浏览器为直连模式，不使用系统代理");
        }

        if (_directProxyItem is not null)
            _directProxyItem.Header = (_activeProxyMode == BrowserProxyMode.Direct ? "✓ " : string.Empty)
                                      + "直连（不使用系统代理）";
        if (_burpProxyItem is not null)
            _burpProxyItem.Header = (_activeProxyMode == BrowserProxyMode.Burp ? "✓ " : string.Empty)
                                    + $"Burp 代理 · {BrowserProxyConfiguration.BurpEndpoint}";
        if (_telemetryFilterItem is not null)
            _telemetryFilterItem.Header = (_suppressKnownTelemetry ? "✓ " : string.Empty)
                                          + "过滤已知页面遥测";
    }

    #endregion

    #region WebView 生命周期

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = CreateWebViewAsync();
    }

    private async Task CreateWebViewAsync()
    {
        if (_released || _webView is not null || _host is null)
            return;

        // 同一时刻只允许一个 WebView2 初始化 —— 并发初始化会争抢用户数据目录并卡死。
        _creationLease = await _coordinator.AcquireAsync(_vm?.PageId ?? "browser").ConfigureAwait(true);

        try
        {
            var webView = new NativeWebView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            webView.EnvironmentRequested += OnEnvironmentRequested;
            webView.AdapterCreated += OnAdapterCreated;

            _webView = webView;
            _host.Children.Add(webView);

            SetStatus("正在创建 WebView…");
            ScheduleAdapterProbe();
        }
        catch (Exception ex)
        {
            _logger.Error("创建 WebView 失败", ex);
            SetStatus($"创建 WebView 失败: {ex.Message}");
            ReleaseCreationLease();
        }
    }

    /// <summary>
    /// 环境参数只在创建时读取一次。直连与 Burp 使用独立的持久配置目录，
    /// 避免切换期间不同 WebView2 环境互相锁定。
    /// </summary>
    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || e is not WindowsWebView2EnvironmentRequestedEventArgs win)
            return;

        try
        {
            var proxy = BrowserProxyConfiguration.Create(_activeProxyMode);
            Directory.CreateDirectory(proxy.UserDataFolder);
            win.UserDataFolder = proxy.UserDataFolder;
            win.AdditionalBrowserArguments = proxy.AdditionalBrowserArguments;
        }
        catch (Exception ex)
        {
            // An explicit profile override is an isolation boundary for automated
            // acceptance. Never fall back to WebView2's default user profile when
            // that boundary is invalid or unavailable.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    BrowserProxyConfiguration.ProfileRootEnvironmentVariable)))
            {
                _logger.Error("The isolated WebView2 profile could not be configured.", ex);
                throw;
            }

            // 拿不到自定义目录就让 WebView2 用默认值,不阻断创建。
        }
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        ReleaseCreationLease();
        Dispatcher.UIThread.Post(TryAttachCdp, DispatcherPriority.Loaded);
    }

    private void ScheduleAdapterProbe()
    {
        foreach (var delay in ProbeDelaysMs)
        {
            StartupPerformance.RunAfterDelay(
                () => Dispatcher.UIThread.Post(TryAttachCdp, DispatcherPriority.Loaded),
                delay);
        }
    }

    #endregion

    #region CDP 会话

    /// <summary>
    /// 取原生句柄并建立 CDP 会话。可能被探测器重复调用,内部幂等。
    /// </summary>
    private void TryAttachCdp()
    {
        if (_released || _cdpAttached || _webView is null || _vm is null)
            return;

        if (_webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle { CoreWebView2: not 0 } handle)
        {
            SetStatus("等待 WebView2 适配器…");
            return;
        }

        _cdpAttached = true;
        ReleaseCreationLease();

        try
        {
            _session = new CdpSession(_vm.PageId, handle.CoreWebView2, _logger);
            _registration = _registry.Register(_session);

            _vm.IsCdpReady = true;
            SetStatus("CDP 通道已建立");
            _logger.Info("CDP 会话已建立");

            _ = InitializeSessionAsync(_session);
        }
        catch (Exception ex)
        {
            _cdpAttached = false;
            _logger.Error("建立 CDP 会话失败", ex);
            SetStatus($"CDP 建立失败: {ex.Message}");

            // CDP 起不来也要能浏览,只是失去观测能力。
            NavigatePending();
        }
    }

    /// <summary>
    /// 启用 CDP 域并接上事件,<strong>完成后才发起首次导航</strong>。
    /// <para>
    /// 顺序不能颠倒:先导航再订阅会把首屏的全部网络活动漏掉,
    /// 而首屏恰恰是调试时最需要看到的部分。
    /// </para>
    /// 域启用失败不影响浏览本身,只是降级为"能浏览但没有观测能力"。
    /// </summary>
    private async Task InitializeSessionAsync(CdpSession session)
    {
        try
        {
            await session.EnableDomainAsync("Page").ConfigureAwait(false);
            await session.EnableDomainAsync("Runtime").ConfigureAwait(false);
            await session.EnableDomainAsync("Network").ConfigureAwait(false);
            await session.EnableDomainAsync("Log").ConfigureAwait(false);

            // Apply before the first navigation so filtered telemetry never reaches
            // an external proxy such as Burp Suite.
            await ApplyTelemetryFilterAsync(session, _suppressKnownTelemetry).ConfigureAwait(false);

            // Page Agent 必须赶在导航之前装好 —— 预注入只对之后加载的文档生效。
            var agentInstalled = await _agentInjector.InstallAsync(session).ConfigureAwait(false);
            UiThreadBridge.Post(() =>
            {
                if (_vm is not null)
                    _vm.IsAgentReady = agentInstalled;
            });

            await session.SubscribeAsync("Page.frameNavigated", OnFrameNavigated).ConfigureAwait(false);
            // 参数不能叫 _:那样它就是 lambda 参数本身,后面的 `_ =` 丢弃赋值会当成给参数赋值。
            await session.SubscribeAsync("Page.loadEventFired", loadEvent =>
            {
                UpdateLoading(false);
                _ = RefreshTitleAsync();
            }).ConfigureAwait(false);
            await session.SubscribeAsync("Page.frameStartedLoading", _ => UpdateLoading(true)).ConfigureAwait(false);
            await session.SubscribeAsync("Network.responseReceived", OnResponseReceived).ConfigureAwait(false);

            _logger.Info("CDP 域已启用并完成事件订阅: Page / Runtime / Network");

            // 订阅就绪,现在才导航 —— 首屏流量得以完整捕获。
            UiThreadBridge.Post(NavigatePending);

            // 诊断模式:等页面加载完跑一次自检,供无人值守验证。
            if (Environment.GetEnvironmentVariable("HACKERMES_SELFTEST") == "1")
            {
                await Task.Delay(6000).ConfigureAwait(false);
                await RunSelfTestAsync().ConfigureAwait(false);
                _logger.Info($"网络事件累计: {_vm?.NetworkEventCount ?? 0}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("启用 CDP 域失败", ex);
            UiThreadBridge.Post(() =>
            {
                SetStatus($"CDP 域启用失败: {ex.Message}");
                NavigatePending();
            });
        }
    }

    private void OnFrameNavigated(CdpEventArgs e)
    {
        // 只关心主框架,子框架导航不该改地址栏。
        var url = CdpJson.ReadMainFrameUrl(e.ParametersJson);

        if (string.IsNullOrEmpty(url))
            return;

        if (_vm is { } viewModel)
            _eventBus.Publish(new BrowserPageNavigatedEvent(viewModel.PageId, url));

        UiThreadBridge.Post(() =>
        {
            if (_vm is null)
                return;

            _vm.CurrentUrl = url;
            _vm.AddressText = url;
            _vm.RecordHistory(url);
            SetStatus(url);
        });
    }

    /// <summary>
    /// 网络响应事件。阶段 1 只做计数与最近一条 URL ——
    /// 完整的请求/响应入库属于阶段 2 的检查面板。
    /// </summary>
    private void OnResponseReceived(CdpEventArgs e)
    {
        var url = CdpJson.TryGetString(e.ParametersJson, "response", "url");
        var status = CdpJson.TryGetInt(e.ParametersJson, "response", "status");

        UiThreadBridge.Post(() =>
        {
            if (_vm is null)
                return;

            _vm.NetworkEventCount++;

            if (!string.IsNullOrEmpty(url))
                SetStatus($"[{status}] {Shorten(url)}  ·  已捕获 {_vm.NetworkEventCount} 个响应");
        });
    }

    private static string Shorten(string url) =>
        url.Length <= 90 ? url : url[..87] + "…";

    /// <summary>
    /// CDP 自检:通过 <c>Runtime.evaluate</c> 取页面标题与资源数,
    /// 一次跑通"请求-响应"通道并把结果显示出来。
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        if (_session is null)
        {
            SetSelfTestResult("CDP 会话未建立");
            return;
        }

        try
        {
            var expression = "JSON.stringify({title: document.title, url: location.href, "
                             + "nodes: document.getElementsByTagName('*').length, "
                             + "resources: performance.getEntriesByType('resource').length})";

            var json = await _session.SendAsync(
                "Runtime.evaluate",
                CdpJson.Params(("expression", expression), ("returnByValue", true))).ConfigureAwait(false);

            var value = CdpJson.TryGetString(json, "result", "value");

            UiThreadBridge.Post(() => SetSelfTestResult(
                value is null ? "自检返回空结果" : $"自检通过 → {value}"));
        }
        catch (Exception ex)
        {
            _logger.Error("CDP 自检失败", ex);
            UiThreadBridge.Post(() => SetSelfTestResult($"自检失败: {ex.Message}"));
        }
    }

    private void SetSelfTestResult(string text)
    {
        if (_vm is not null)
            _vm.SelfTestResult = text;

        _logger.Info(text);
    }

    /// <summary>
    /// 页面加载完成后同步标题。
    /// <para>
    /// 走 <c>Runtime.evaluate</c> 而不是 WebView2 的 DocumentTitleChanged 事件:
    /// 前者与其余页面操作同一条通道,SPA 用 JS 改标题时也能取到最新值。
    /// </para>
    /// </summary>
    private async Task RefreshTitleAsync()
    {
        if (_session is null)
            return;

        try
        {
            var json = await _session.SendAsync(
                "Runtime.evaluate",
                CdpJson.Params(("expression", "document.title"), ("returnByValue", true))).ConfigureAwait(false);

            var title = CdpJson.TryGetString(json, "result", "value");

            if (string.IsNullOrWhiteSpace(title))
                return;

            UiThreadBridge.Post(() =>
            {
                if (_vm is not null)
                {
                    _vm.Title = title;
                    _vm.RecordHistory(_vm.CurrentUrl);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Debug($"取页面标题失败: {ex.Message}");
        }
    }

    private void UpdateLoading(bool isLoading) =>
        UiThreadBridge.Post(() =>
        {
            if (_vm is not null)
                _vm.IsLoading = isLoading;
        });

    #endregion

    #region 导航

    private void NavigatePending()
    {
        if (_pendingNavigation is not { Length: > 0 } url)
            return;

        _pendingNavigation = null;
        NavigateCore(url);
    }

    private void OnNavigateRequested(string url)
    {
        if (!_cdpAttached)
        {
            // 适配器还没就绪,记下来等会儿再走。
            _pendingNavigation = url;
            return;
        }

        NavigateCore(url);
    }

    private void NavigateCore(string url)
    {
        if (_webView is null)
            return;

        try
        {
            _webView.Navigate(new Uri(url));
            SetStatus($"正在打开 {url}");
        }
        catch (Exception ex)
        {
            _logger.Error($"导航失败: {url}", ex);
            SetStatus($"导航失败: {ex.Message}");
        }
    }

    private void OnReloadRequested() => RunCdp("Page.reload", "{}");

    private void OnBackRequested() => TryWebViewAction(w => w.GoBack(), "后退");

    private void OnForwardRequested() => TryWebViewAction(w => w.GoForward(), "前进");

    private void OnDevToolsRequested() => RunCdp("Page.bringToFront", "{}");

    private void OnSelfTestRequested() => _ = RunSelfTestAsync();

    private void OnElementPickerRequested(bool enabled)
    {
        if (_vm is null) return;
        _eventBus.Publish(new ElementPickerToggleRequestedEvent(_vm.PageId, enabled));
        SetStatus(enabled ? "Element picker enabled: hover, then click an element." : "Element picker disabled.");
    }

    private void OnPickerStateChanged(ElementPickerStateChangedEvent state)
    {
        if (_vm is null || !string.Equals(state.PageId, _vm.PageId, StringComparison.Ordinal)) return;
        UiThreadBridge.Post(() =>
        {
            _vm?.SetElementPickerState(state.Enabled);
            if (!string.IsNullOrWhiteSpace(state.Error)) SetStatus($"Element picker unavailable: {state.Error}");
        });
    }

    private void OnDeviceEmulationRequested(BrowserDeviceProfile? profile) => _ = SetDeviceEmulationAsync(profile);

    private void OnDeviceModeToggleRequested(BrowserDeviceModeToggleRequestedEvent request)
    {
        if (_vm is null || !string.Equals(request.PageId, _vm.PageId, StringComparison.Ordinal))
            return;

        UiThreadBridge.Post(() => _vm?.SetDeviceMode(request.Enabled));
    }

    private async Task SetDeviceEmulationAsync(BrowserDeviceProfile? profile)
    {
        var pageId = _vm?.PageId;
        if (string.IsNullOrWhiteSpace(pageId))
            return;

        if (_session is null)
        {
            SetStatus("Wait for the CDP connection before changing device mode.");
            if (_vm is not null) _vm.IsDeviceMode = false;
            _eventBus.Publish(new BrowserDeviceModeStateChangedEvent(pageId, false, Error: "CDP connection is not ready."));
            return;
        }

        try
        {
            if (profile is null)
            {
                await _session.SendAsync("Emulation.clearDeviceMetricsOverride", "{}").ConfigureAwait(false);
                await _session.SendAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}").ConfigureAwait(false);
                UiThreadBridge.Post(() =>
                {
                    _appliedDeviceProfile = null;
                    ApplyBrowserViewport(null);
                    SetStatus("Desktop viewport restored.");
                });
                _eventBus.Publish(new BrowserDeviceModeStateChangedEvent(pageId, false));
                return;
            }

            var viewport = GetBoundedDeviceViewport(profile);

            var metrics = JsonSerializer.Serialize(new
            {
                width = viewport.Width,
                height = viewport.Height,
                deviceScaleFactor = profile.DeviceScaleFactor,
                mobile = profile.Mobile,
                screenWidth = viewport.Width,
                screenHeight = viewport.Height,
                positionX = 0,
                positionY = 0,
                dontSetVisibleSize = false
            });
            await _session.SendAsync("Emulation.setDeviceMetricsOverride", metrics).ConfigureAwait(false);
            await _session.SendAsync("Emulation.setTouchEmulationEnabled", JsonSerializer.Serialize(new { enabled = profile.Mobile })).ConfigureAwait(false);
            UiThreadBridge.Post(() =>
            {
                _appliedDeviceProfile = profile;
                ApplyBrowserViewport(profile, viewport.Width, viewport.Height);
                SetStatus($"Device viewport: {profile.Name} ({profile.Width} × {profile.Height}).");
            });
            _eventBus.Publish(new BrowserDeviceModeStateChangedEvent(pageId, true, profile.Name));
        }
        catch (Exception exception)
        {
            _logger.Warn($"Device emulation failed: {exception.Message}");
            UiThreadBridge.Post(() =>
            {
                if (_vm is not null) _vm.IsDeviceMode = false;
                _appliedDeviceProfile = null;
                ApplyBrowserViewport(null);
                SetStatus($"Device emulation failed: {exception.Message}");
            });
            _eventBus.Publish(new BrowserDeviceModeStateChangedEvent(pageId, false, Error: exception.Message));
        }
    }

    private (int Width, int Height) GetBoundedDeviceViewport(BrowserDeviceProfile profile)
    {
        var availableWidth = _host?.Bounds.Width ?? 0;
        var availableHeight = _host?.Bounds.Height ?? 0;
        var width = availableWidth > 1 ? Math.Min(profile.Width, (int)Math.Floor(availableWidth)) : profile.Width;
        var height = availableHeight > 1 ? Math.Min(profile.Height, (int)Math.Floor(availableHeight)) : profile.Height;
        return (Math.Max(1, width), Math.Max(1, height));
    }

    private void OnWebViewHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_appliedDeviceProfile is null)
            return;

        var viewport = GetBoundedDeviceViewport(_appliedDeviceProfile);
        ApplyBrowserViewport(_appliedDeviceProfile, viewport.Width, viewport.Height);
    }

    private void ApplyBrowserViewport(BrowserDeviceProfile? profile, int? width = null, int? height = null)
    {
        if (_webView is null || _host is null) return;
        if (profile is null)
        {
            _webView.Width = double.NaN;
            _webView.Height = double.NaN;
            _webView.HorizontalAlignment = HorizontalAlignment.Stretch;
            _webView.VerticalAlignment = VerticalAlignment.Stretch;
            _host.Background = Avalonia.Media.Brushes.White;
            return;
        }

        _webView.Width = width ?? profile.Width;
        _webView.Height = height ?? profile.Height;
        _webView.HorizontalAlignment = HorizontalAlignment.Center;
        _webView.VerticalAlignment = VerticalAlignment.Top;
        _host.Background = Avalonia.Media.Brushes.LightGray;
    }

    private void TryWebViewAction(Action<NativeWebView> action, string name)
    {
        if (_webView is null)
            return;

        try
        {
            action(_webView);
        }
        catch (Exception ex)
        {
            _logger.Warn($"{name} 失败: {ex.Message}");
        }
    }

    private void RunCdp(string method, string parameters)
    {
        if (_session is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _session.SendAsync(method, parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"{method} 失败: {ex.Message}");
            }
        });
    }

    #endregion

    private void SetStatus(string text)
    {
        if (_vm is not null)
            _vm.StatusText = text;
    }

    private void ReleaseCreationLease()
    {
        _creationLease?.Dispose();
        _creationLease = null;
    }

    private void DisposeCurrentWebView()
    {
        _registration?.Dispose();
        _registration = null;

        _session?.Dispose();
        _session = null;

        ReleaseCreationLease();

        if (_webView is null)
            return;

        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.AdapterCreated -= OnAdapterCreated;

        try
        {
            _host?.Children.Remove(_webView);
            (_webView as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn($"释放 WebView 失败: {ex.Message}");
        }

        _webView = null;
    }

    // ── Tab 显隐。切 Tab 不销毁任何东西,只是通知 ──────────────────────────────
    public void OnTabBecameVisible()
    {
        _logger.Debug("标签页可见");
        if (_vm is not null)
            _eventBus.Publish(new BrowserDeviceModeStateChangedEvent(_vm.PageId, _vm.IsDeviceMode, _vm.SelectedDeviceProfile.Name));
    }

    public void OnTabBecameHidden() => _logger.Debug("标签页隐藏");

    /// <summary>Tab <strong>关闭</strong>时才走到这里。切 Tab 不会调用。</summary>
    public void ReleaseTabResources()
    {
        if (_released)
            return;

        _released = true;
        _logger.Info("释放标签页资源");

        if (_vm is not null)
        {
            _vm.NavigateRequested -= OnNavigateRequested;
            _vm.ReloadRequested -= OnReloadRequested;
            _vm.BackRequested -= OnBackRequested;
            _vm.ForwardRequested -= OnForwardRequested;
            _vm.DevToolsRequested -= OnDevToolsRequested;
            _vm.SelfTestRequested -= OnSelfTestRequested;
            _vm.ElementPickerRequested -= OnElementPickerRequested;
            _vm.DeviceEmulationRequested -= OnDeviceEmulationRequested;
            _vm.Dispose();
        }

        _pickerStateSubscription?.Dispose();
        _pickerStateSubscription = null;

        _deviceModeRequestSubscription?.Dispose();
        _deviceModeRequestSubscription = null;

        _proxyModeSubscription?.Dispose();
        _proxyModeSubscription = null;

        _telemetryFilterSubscription?.Dispose();
        _telemetryFilterSubscription = null;

        if (_host is not null)
            _host.SizeChanged -= OnWebViewHostSizeChanged;

        DisposeCurrentWebView();

        _vm = null;
        _host = null;
        _proxyButton = null;
        _proxyMenu = null;
    }
}
