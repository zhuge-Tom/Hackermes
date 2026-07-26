using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Hookmes.Base.Diagnostics;
using Hookmes.Browser.Services;
using Hookmes.Browser.ViewModels;
using Hookmes.Cdp;
using Hookmes.Cdp.Session;
using Hookmes.Platform.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Hookmes.Browser.Views;

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
    private readonly ICdpSessionRegistry _registry;
    private readonly WebViewCreationCoordinator _coordinator;
    private readonly PageAgentInjector _agentInjector;

    private Panel? _host;
    private NativeWebView? _webView;
    private BrowserTabViewModel? _vm;
    private CdpSession? _session;
    private IDisposable? _registration;
    private IDisposable? _creationLease;

    private bool _cdpAttached;
    private bool _released;
    private string? _pendingNavigation;

    public BrowserTabView(
        BrowserTabViewModel viewModel,
        ICdpSessionRegistry registry,
        WebViewCreationCoordinator coordinator,
        PageAgentInjector agentInjector,
        IAppLogger logger)
    {
        InitializeComponent();

        _vm = viewModel;
        _registry = registry;
        _coordinator = coordinator;
        _agentInjector = agentInjector;
        _logger = logger.ForCategory($"Browser:{viewModel.PageId}");

        DataContext = viewModel;
        _host = this.FindControl<Panel>("PART_WebViewHost");

        viewModel.NavigateRequested += OnNavigateRequested;
        viewModel.ReloadRequested += OnReloadRequested;
        viewModel.BackRequested += OnBackRequested;
        viewModel.ForwardRequested += OnForwardRequested;
        viewModel.DevToolsRequested += OnDevToolsRequested;
        viewModel.SelfTestRequested += OnSelfTestRequested;

        _pendingNavigation = viewModel.CurrentUrl;

        Loaded += OnLoaded;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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
    /// 环境参数只在创建时读取一次。用户数据目录固定在临时区,
    /// 避免多个实例共用默认目录时互相锁定。
    /// </summary>
    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || e is not WindowsWebView2EnvironmentRequestedEventArgs win)
            return;

        try
        {
            var profileDir = Path.Combine(Path.GetTempPath(), "Hookmes", "WebView2");
            Directory.CreateDirectory(profileDir);
            win.UserDataFolder = profileDir;
        }
        catch
        {
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
            if (Environment.GetEnvironmentVariable("HOOKMES_SELFTEST") == "1")
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

        UiThreadBridge.Post(() =>
        {
            if (_vm is null)
                return;

            _vm.CurrentUrl = url;
            _vm.AddressText = url;
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
                    _vm.Title = title;
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

    // ── Tab 显隐。切 Tab 不销毁任何东西,只是通知 ──────────────────────────────
    public void OnTabBecameVisible() => _logger.Debug("标签页可见");

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
        }

        _registration?.Dispose();
        _registration = null;

        _session?.Dispose();
        _session = null;

        ReleaseCreationLease();

        if (_webView is not null)
        {
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

        _vm = null;
        _host = null;
    }
}
