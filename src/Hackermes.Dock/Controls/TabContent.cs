using Avalonia;
using Avalonia.Controls;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;

namespace Hackermes.Dock.Controls;

/// <summary>
/// Tab 内容的两种注册形态。
/// <para>
/// 判断标准很简单:<strong>内容里有没有切页就会坏掉的东西</strong>。
/// WebView2、PTY、正在播放的媒体都属于此类,必须用 <see cref="NonReloadable"/>。
/// </para>
/// </summary>
public static class TabContent
{
    /// <summary>可重载(默认)。切 Tab 会卸载可视树,重新选中时重新挂载。</summary>
    public static Control Reloadable(Control view) => view;

    /// <summary>
    /// 不可重载。真实控件被挂到叠层上永久保活,切 Tab 只改显隐。
    /// WebView2 与 PTY 必须走这条路径 —— 它们一旦离开可视树就会被销毁。
    /// </summary>
    public static NonReloadableTabPlaceholder NonReloadable(Control view) =>
        NonReloadableTabPlaceholder.Wrap(view);
}

/// <summary>不可重载 Tab 的标准占位壳:一个空面板,只负责在内容区占住布局。</summary>
public sealed class NonReloadableTabPlaceholder : Panel, INonReloadableTabShell, IDockTabToolPanelProvider
{
    public static readonly StyledProperty<Control?> PersistedContentProperty =
        AvaloniaProperty.Register<NonReloadableTabPlaceholder, Control?>(nameof(PersistedContent));

    public Control? PersistedContent
    {
        get => GetValue(PersistedContentProperty);
        set => SetValue(PersistedContentProperty, value);
    }

    public static NonReloadableTabPlaceholder Wrap(Control content) =>
        new() { PersistedContent = content };

    public void OnTabBecameVisible()
    {
        if (PersistedContent is INonReloadableTabHost host)
            host.OnTabBecameVisible();
    }

    public void OnTabBecameHidden()
    {
        if (PersistedContent is INonReloadableTabHost host)
            host.OnTabBecameHidden();
    }

    public Control? GetDockTabToolPanel() =>
        PersistedContent is IDockTabToolPanelProvider provider ? provider.GetDockTabToolPanel() : null;
}

/// <summary>
/// Tab 内容在注册时只创建<strong>一个</strong>控件实例以保留状态,
/// 而 Avalonia 的控件任意时刻只能有一个可视父级。换宿主前必须先经此处解除挂载,
/// 否则会抛"控件已有父级"异常。
/// </summary>
public static class DockControlHost
{
    public static void DetachFromVisualTree(Control? control)
    {
        switch (control?.Parent)
        {
            case ContentControl contentHost:
                contentHost.Content = null;
                break;
            case Avalonia.Controls.Presenters.ContentPresenter presenterHost:
                presenterHost.Content = null;
                break;
            case Panel panelHost:
                panelHost.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }
}
