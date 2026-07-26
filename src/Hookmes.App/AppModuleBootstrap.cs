using Hookmes.Base;
using Hookmes.Base.Diagnostics;
using Hookmes.Dock;
using Hookmes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hookmes.App;

/// <summary>
/// 容器装配。
/// <para>
/// 模块清单是<strong>硬编码数组</strong>,不做程序集扫描 —— 反射发现省下的几行代码,
/// 换来的是启动变慢、AOT 不友好、以及"模块为什么没加载"这类难查的问题。
/// </para>
/// <para>
/// 顺序有语义:<c>CoreModule</c> 提供核心服务,<c>DockModule</c> 提供布局承载,
/// 功能模块的 <c>Initialize</c> 依赖两者已经就位。
/// </para>
/// </summary>
internal static class AppModuleBootstrap
{
    public static IServiceProvider Build()
    {
        var sw = Stopwatch.StartNew();
        var services = new ServiceCollection();
        var modules = CreateModules();

        // 第一趟:只登记服务,不解析。此时容器还不存在。
        foreach (var module in modules)
            module.RegisterServices(services);

        services.AddSingleton<ViewModels.MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<IAppLogger>().ForCategory("Bootstrap");

        // 第二趟:解析服务、向注册表登记 Tab / 菜单 / 设置页。
        foreach (var module in modules)
        {
            try
            {
                module.Initialize(provider);
            }
            catch (Exception ex)
            {
                // 一个模块初始化失败不应让整个应用起不来。
                logger.Error($"模块 '{module.Name}' 初始化失败", ex);
            }
        }

        logger.Info($"容器装配完成,{modules.Count} 个模块,耗时 {sw.ElapsedMilliseconds} ms");
        return provider;
    }

    private static List<IModule> CreateModules() =>
    [
        new CoreModule(),
        new DockModule(),
        new Browser.BrowserModule(),
        new Inspector.InspectorModule()
        // 后续阶段依次接入:AutomationModule、TerminalModule、
        // AiPanelModule、SidebarModule、SettingsModule
    ];
}
