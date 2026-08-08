using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Hackermes.App.ViewModels;
using Hackermes.App.Views;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Hackermes.App;

public partial class App : Application
{
    /// <summary>
    /// 服务定位入口。构造函数注入仍是首选,这里主要服务于 code-behind ——
    /// View 由 XAML 实例化,拿不到构造参数。
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 同步阶段只把空窗显示出来。装配放到异步阶段,
            // 否则用户会盯着一片空白等上一两秒才看到窗口。
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Show();

            Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;

            _ = InitializeAsync(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeAsync(MainWindow window)
    {
        try
        {
            // 让空窗与加载遮罩先画出来。
            await StartupPerformance.YieldUiFramesAsync(3);

            // 容器构建是 CPU 密集的,挪到后台线程,UI 线程保持响应。
            var services = await Task.Run(AppModuleBootstrap.Build);
            Services = services;

            var logger = services.GetRequiredService<IAppLogger>().ForCategory("Startup");
            var viewModel = services.GetRequiredService<MainWindowViewModel>();

            await UiThreadBridge.InvokeAsync(() =>
            {
                ApplyTheme(viewModel.IsDarkMode);
                window.DataContext = viewModel;
                window.AttachMainContent(new MainContentView { DataContext = viewModel });
            });

            await UiThreadBridge.InvokeAsync(async () =>
            {
                await viewModel.DockLayout.ApplyRegistrationsAsync();
                viewModel.DockLayout.CompleteStartup();
            });

            await StartupPerformance.YieldUiFramesAsync(2);

            await UiThreadBridge.InvokeAsync(() =>
            {
                window.HideLoadingOverlay();

                // 在此之前创建 WebView2 会因宿主尺寸为 0 而失败。
                StartupPerformance.MarkLayoutReady();
            });

            logger.Info("启动完成");

            // 工作区恢复推迟到最后,不占用首屏时间。
            StartupPerformance.RunAfterDelay(viewModel.TryRestoreLastWorkspace, 300);
        }
        catch (Exception ex)
        {
            CrashLog.Write("App.InitializeAsync", ex);

            UiThreadBridge.Post(() => window.ShowStartupFailure(ex));
        }
    }

    private static void ApplyTheme(bool isDark)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private static void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Dispatcher.UnhandledException", e.Exception);

        // UI 线程上的异常不应直接终止进程 —— 单个控件出问题不该拖垮整个会话。
        e.Handled = true;
    }
}
