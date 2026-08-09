using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Platform.Registries;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hackermes.Platform.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册应用平台层的核心单例。这一组服务不依赖任何功能模块,
    /// 因此桌面宿主之外的场景(测试宿主、无界面工具)也能直接复用。
    /// </summary>
    public static IServiceCollection AddHackermesCoreServices(this IServiceCollection services)
    {
        // 日志级别可用 HACKERMES_LOG_LEVEL 覆盖(Debug/Info/Warn/Error),
        // 排查 CDP、布局这类只在运行时暴露的问题时很有用。
        services.AddSingleton<IAppLogger>(_ =>
        {
            var configured = Environment.GetEnvironmentVariable("HACKERMES_LOG_LEVEL");
            var level = Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var parsed)
                ? parsed
                : LogLevel.Info;

            return new FileAppLogger(level);
        });
        services.AddSingleton<IEventBus>(sp => new EventBus(sp.GetRequiredService<IAppLogger>()));

        services.AddSingleton<IDockLayoutRegistry, DockLayoutRegistry>();
        services.AddSingleton<IMenuRegistry, MenuRegistry>();
        services.AddSingleton<ISettingsRegistry, SettingsRegistry>();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<WebViewCreationCoordinator>();

        services.AddSingleton<ISecretStore>(sp =>
            SecretStoreFactory.Create(sp.GetRequiredService<IAppLogger>()));

        return services;
    }
}
