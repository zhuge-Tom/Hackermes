using Avalonia;
using System;
using System.Threading.Tasks;

namespace Hackermes.App;

internal static class Program
{
    /// <summary>
    /// 入口只做两件事:挂全局异常网,启动 Avalonia。
    /// <para>DI 容器<strong>不在这里</strong>构建 —— 见 <see cref="App.OnFrameworkInitializationCompleted"/>,
    /// 那里会先把空窗显示出来再在后台线程装配,启动观感差别很明显。</para>
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("StartWithClassicDesktopLifetime", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
