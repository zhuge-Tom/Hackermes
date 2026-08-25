using Hackermes.Base;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.Services;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Hackermes.Browser;

public sealed class BrowserModule : IModule
{
    public string Name => "Browser";

    public void RegisterServices(IServiceCollection services)
    {
        // 会话注册表放在这里注册,但类型来自 Hackermes.Cdp ——
        // 检查面板与自动化模块只依赖 Cdp,不依赖 Browser。
        services.AddSingleton<ICdpSessionRegistry>(sp =>
            new CdpSessionRegistry(sp.GetRequiredService<IAppLogger>()));

        services.AddSingleton<PageAgentInjector>();
        services.AddSingleton<IPageAgentRuntime>(sp =>
            sp.GetRequiredService<PageAgentInjector>());
        services.AddSingleton<BrowserPageContextService>();
        services.AddSingleton<IPageContextQueryService>(sp =>
            sp.GetRequiredService<BrowserPageContextService>());
        services.AddSingleton<IBrowserHistoryStore, BrowserHistoryStore>();
        services.AddSingleton<IBrowserTabManager, BrowserTabManager>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var eventBus = serviceProvider.GetRequiredService<IEventBus>();
        var tabManager = serviceProvider.GetRequiredService<IBrowserTabManager>();

        eventBus.Subscribe<OpenBrowserTabRequestedEvent>(e =>
            UiThreadBridge.Post(() => tabManager.OpenTab(e.Url)));

        TryTracePageAgent(eventBus, serviceProvider.GetRequiredService<IAppLogger>());
        TryAutoOpen(tabManager);
    }

    /// <summary>
    /// 诊断模式下把 Page Agent 的回传打进日志。
    /// 每种消息类型只详细记前两条,其余仅累计 —— 一个正常页面每秒可能产生几十条。
    /// </summary>
    private static void TryTracePageAgent(IEventBus eventBus, IAppLogger logger)
    {
        if (Environment.GetEnvironmentVariable("HACKERMES_SELFTEST") != "1")
            return;

        var log = logger.ForCategory("PageAgent");
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        eventBus.Subscribe<PageAgentMessageEvent>(e =>
        {
            var key = $"{e.Kind}/{e.SubKind ?? "-"}";
            var n = counts.AddOrUpdate(key, 1, (_, v) => v + 1);

            if (n <= 2)
            {
                var preview = e.PayloadJson.Length <= 260 ? e.PayloadJson : e.PayloadJson[..260] + "…";
                log.Info($"{key} #{n} → {preview}");
            }
        });

        // 页面稳定后汇总一次,便于一眼看出各通道是否都通。
        StartupPerformance.RunAfterDelay(
            () => log.Info("回传汇总: " + (counts.IsEmpty
                ? "(没有收到任何消息)"
                : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")))),
            14000);
    }

    /// <summary>
    /// 诊断入口:设置 <c>HACKERMES_AUTOOPEN_URL</c> 后启动即打开该地址。
    /// <para>
    /// 存在的理由是无人值守验证 —— CDP 通道能否建立只有真正跑起来才知道,
    /// 而手工点击无法纳入自动化检查。配合 <c>HACKERMES_SELFTEST=1</c> 可让标签页
    /// 在 CDP 就绪后自动跑一次自检并把结果写进日志。
    /// </para>
    /// </summary>
    private static void TryAutoOpen(IBrowserTabManager tabManager)
    {
        var url = Environment.GetEnvironmentVariable("HACKERMES_AUTOOPEN_URL");

        if (string.IsNullOrWhiteSpace(url))
            return;

        // 等布局稳定,否则 WebView2 会因宿主尺寸为 0 而初始化失败。
        // A fixed delay can fire before DockLayoutViewModel subscribes to the
        // add-tab event. Wait for the explicit layout lifecycle boundary so the
        // browser tab is never published into an empty event bus.
        StartupPerformance.RunWhenLayoutReady(() => tabManager.OpenTab(url));
    }
}
