using Hackermes.Base;
using Hackermes.Base.Diagnostics;
using Hackermes.Inspector.Services;
using Hackermes.Inspector.ViewModels;
using Hackermes.Inspector.Views;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Hackermes.Inspector;

/// <summary>
/// 检查面板。占据底部区域,是"人工调试"的主阵地。
/// <para>
/// 两个 store 都是单例并在构造时就挂上会话监听 —— 面板本身是懒物化的,
/// 但数据采集不能等到用户点开面板才开始,否则首屏流量全都错过了。
/// </para>
/// </summary>
public sealed class InspectorModule : IModule
{
    public string Name => "Inspector";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<NetworkStore>();
        services.AddSingleton<ConsoleStore>();
        services.AddSingleton<INetworkQueryService>(sp => sp.GetRequiredService<NetworkStore>());
        services.AddSingleton<IConsoleQueryService>(sp => sp.GetRequiredService<ConsoleStore>());
        services.AddSingleton<PageInspectionService>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();

        // 立即实例化,让采集在任何页面打开之前就绪。
        var networkStore = serviceProvider.GetRequiredService<NetworkStore>();
        var consoleStore = serviceProvider.GetRequiredService<ConsoleStore>();
        var pageInspection = serviceProvider.GetRequiredService<PageInspectionService>();

        TraceStoresWhenDiagnosing(serviceProvider, networkStore, consoleStore);

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "network",
            Title = "网络",
            IconKey = "SemiIconGlobe",
            IsClosable = false,
            Order = 0,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "network",
                Title = "网络",
                Content = new NetworkPanelView { DataContext = new NetworkPanelViewModel(networkStore) }
            }
        });

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "console",
            Title = "控制台",
            IconKey = "SemiIconComment",
            IsClosable = false,
            Order = 1,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "console",
                Title = "控制台",
                Content = new ConsolePanelView { DataContext = new ConsolePanelViewModel(consoleStore) }
            }
        });

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom, TabId = "dom-inspector", Title = "DOM",
            IconKey = "SemiIconCode", IsClosable = false, Order = 2,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "dom-inspector", Title = "DOM",
                Content = new DomInspectorView { DataContext = new DomInspectorViewModel(pageInspection) }
            }
        });

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom, TabId = "storage-inspector", Title = "Storage",
            IconKey = "SemiIconDatabase", IsClosable = false, Order = 3,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "storage-inspector", Title = "Storage",
                Content = new StorageInspectorView { DataContext = new StorageInspectorViewModel(pageInspection) }
            }
        });

        // Security tools stay in the left region; DOM-linked resources are shown
        // in the DOM inspector, and protocol traffic remains in Network/数据包.
    }

    /// <summary>
    /// 诊断模式下汇总两个 store 的采集结果。
    /// 面板是懒物化的,不点开就看不到内容,而无人值守验证需要一个可断言的信号。
    /// </summary>
    private static void TraceStoresWhenDiagnosing(
        IServiceProvider serviceProvider,
        NetworkStore networkStore,
        ConsoleStore consoleStore)
    {
        if (Environment.GetEnvironmentVariable("HACKERMES_SELFTEST") != "1")
            return;

        var log = serviceProvider.GetRequiredService<IAppLogger>().ForCategory("Inspector");

        Platform.Services.StartupPerformance.RunAfterDelay(() =>
        {
            var net = networkStore.Entries;
            var withStack = net.Count(e => !string.IsNullOrEmpty(e.InitiatorStack));
            var failed = net.Count(e => e.IsFailed);

            log.Info($"网络采集: {net.Count} 条,失败 {failed},含调用栈 {withStack}");

            var console = consoleStore.Entries;
            var levels = console.GroupBy(e => e.Level).Select(g => $"{g.Key}={g.Count()}");
            log.Info($"控制台采集: {console.Count} 条 [{string.Join(", ", levels)}]");
        }, 13000);
    }
}
