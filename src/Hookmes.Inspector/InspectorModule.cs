using Hookmes.Base;
using Hookmes.Base.Diagnostics;
using Hookmes.Inspector.Services;
using Hookmes.Inspector.ViewModels;
using Hookmes.Inspector.Views;
using Hookmes.Platform.Registries;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Hookmes.Inspector;

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
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();

        // 立即实例化,让采集在任何页面打开之前就绪。
        var networkStore = serviceProvider.GetRequiredService<NetworkStore>();
        var consoleStore = serviceProvider.GetRequiredService<ConsoleStore>();

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
        if (Environment.GetEnvironmentVariable("HOOKMES_SELFTEST") != "1")
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
