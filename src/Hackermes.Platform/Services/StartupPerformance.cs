using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Hackermes.Platform.Services;

/// <summary>
/// 启动期调度工具。启动路径的目标是"空窗先出、重活后做":
/// DI 容器在后台线程构建,布局稳定后才关遮罩,WebView2 创建与工作区恢复都往后推。
/// </summary>
public static class StartupPerformance
{
    private static volatile bool _layoutReady;

    /// <summary>
    /// 主布局是否已经稳定。WebView2 必须等到它为 true 才允许创建 ——
    /// 过早创建会因宿主尺寸为 0 而初始化失败。
    /// </summary>
    public static bool IsLayoutReady => _layoutReady;

    public static event Action? LayoutReady;

    public static void MarkLayoutReady()
    {
        if (_layoutReady)
            return;

        _layoutReady = true;
        LayoutReady?.Invoke();
    }

    /// <summary>让出若干个 UI 帧,给界面绘制的机会(遮罩动画、进度条)。</summary>
    public static async Task YieldUiFramesAsync(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    /// <summary>延迟一段时间后在 UI 线程执行,用于把重活推离启动关键路径。</summary>
    // DispatcherPriority 在 Avalonia 12 是 struct 而非 enum,不能作为默认参数值。
    public static void RunAfterDelay(Action action, int delayMs, DispatcherPriority? priority = null)
    {
        var effective = priority ?? DispatcherPriority.Background;

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            UiThreadBridge.Post(action, effective);
        });
    }

    /// <summary>UI 空闲时执行。</summary>
    public static void RunOnUiIdle(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.ApplicationIdle);
}
