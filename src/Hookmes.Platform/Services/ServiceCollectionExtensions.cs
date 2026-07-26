using Hookmes.Base.Diagnostics;
using Hookmes.Base.Events;
using Hookmes.Platform.Registries;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Runtime.InteropServices;

namespace Hookmes.Platform.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册应用平台层的核心单例。这一组服务不依赖任何功能模块,
    /// 因此桌面宿主之外的场景(测试宿主、无界面工具)也能直接复用。
    /// </summary>
    public static IServiceCollection AddHookmesCoreServices(this IServiceCollection services)
    {
        // 日志级别可用 HOOKMES_LOG_LEVEL 覆盖(Debug/Info/Warn/Error),
        // 排查 CDP、布局这类只在运行时暴露的问题时很有用。
        services.AddSingleton<IAppLogger>(_ =>
        {
            var configured = Environment.GetEnvironmentVariable("HOOKMES_LOG_LEVEL");
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
        {
            var logger = sp.GetRequiredService<IAppLogger>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new DpapiSecretStore(logger);

            throw new PlatformNotSupportedException(
                "当前仅实现了 Windows 的密钥存储(DPAPI)。移植到其他平台需补充对应实现。");
        });

        return services;
    }
}
