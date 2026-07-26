using Avalonia.Controls;
using System;

namespace Hookmes.Platform.Services;

/// <summary>
/// 不可重载 Tab 的占位壳。Tab 内容区只渲染这个空壳(占布局),
/// 真实的 <see cref="PersistedContent"/> 由 PersistTabControl 挂到叠层并管理显隐 —— 切 Tab 不卸载它。
/// <para>WebView2 与 PTY 一旦离开可视树就会被销毁,这是它们能正常工作的前提。</para>
/// </summary>
public interface INonReloadableTabShell
{
    Control? PersistedContent { get; }

    void OnTabBecameVisible();

    void OnTabBecameHidden();
}

/// <summary>被叠层托管的真实内容可实现此接口,接收显隐通知(例如暂停渲染、释放 GPU 资源)。</summary>
public interface INonReloadableTabHost
{
    void OnTabBecameVisible();

    void OnTabBecameHidden();
}

/// <summary>
/// Tab <strong>关闭</strong>时释放大对象(文件缓冲、数据 Provider、CDP 会话等)。
/// <para>
/// 实现方切勿在 <c>DetachedFromVisualTree</c> 中调用它 ——
/// 叠层保活机制会让控件在切 Tab 时短暂离树,那时释放会直接毁掉还在用的资源。
/// </para>
/// </summary>
public interface ITabContentReleasable
{
    void ReleaseTabResources();
}

public static class TabContentLifetime
{
    public static void Release(object? content)
    {
        if (content is null)
            return;

        if (content is ITabContentReleasable releasable)
        {
            SafeRelease(releasable);
            return;
        }

        if (content is INonReloadableTabShell shell)
        {
            Release(shell.PersistedContent);
            if (shell is ITabContentReleasable shellReleasable)
                SafeRelease(shellReleasable);
            return;
        }

        if (content is Control control)
        {
            if (control is ITabContentReleasable controlReleasable)
                SafeRelease(controlReleasable);
            else if (control.DataContext is ITabContentReleasable dataReleasable)
                SafeRelease(dataReleasable);
        }
    }

    private static void SafeRelease(ITabContentReleasable target)
    {
        try
        {
            target.ReleaseTabResources();
        }
        catch
        {
            // 关闭路径上不抛错。
        }
    }
}

public static class NonReloadableTabContent
{
    /// <summary>若内容是不可重载壳则取出其真实控件,否则返回 null。</summary>
    public static Control? Resolve(object? content) =>
        content is INonReloadableTabShell shell ? shell.PersistedContent : null;
}
