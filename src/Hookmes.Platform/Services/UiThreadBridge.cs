using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Hookmes.Platform.Services;

/// <summary>
/// UI 线程边界。
/// <para>
/// <strong>约定</strong>:进入 UI 线程只做界面更新,await 之后的逻辑继续留在后台线程。
/// </para>
/// <para>
/// 一律用 <see cref="Dispatcher.Post"/> + <see cref="TaskCompletionSource"/>,
/// <strong>刻意不用 <c>Dispatcher.InvokeAsync</c></strong> —— 后者在嵌套调用时会死锁,
/// 而本应用有大量"UI 线程等待 CDP、CDP 回调又要回 UI 线程"的路径。
/// </para>
/// </summary>
public static class UiThreadBridge
{
    public static bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    public static Task InvokeAsync(Action action, DispatcherPriority priority = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsOnUiThread)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, priority);

        return tcs.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> func, DispatcherPriority priority = default)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (IsOnUiThread)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, priority);

        return tcs.Task;
    }

    /// <summary>UI 线程上执行一段异步操作并等待其完成。</summary>
    public static Task<T> InvokeAsync<T>(Func<Task<T>> func, DispatcherPriority priority = default)
    {
        ArgumentNullException.ThrowIfNull(func);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Run()
        {
            _ = RunCoreAsync();

            async Task RunCoreAsync()
            {
                try
                {
                    tcs.SetResult(await func().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }
        }

        if (IsOnUiThread)
            Run();
        else
            Dispatcher.UIThread.Post(Run, priority);

        return tcs.Task;
    }

    /// <summary>发射后不管。仅用于确实无需等待结果的界面更新。</summary>
    public static void Post(Action action, DispatcherPriority priority = default)
    {
        if (IsOnUiThread)
        {
            try
            {
                action();
            }
            catch
            {
                // 调用方已表明不关心结果。
            }

            return;
        }

        Dispatcher.UIThread.Post(action, priority);
    }
}
