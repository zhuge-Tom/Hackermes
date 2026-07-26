using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Base.Diagnostics;

/// <summary>
/// 进程级 WebView 脚本互斥闸门。
/// <para>
/// 浏览器视图与 AI 面板是两个 WebView 实例,<strong>共用同一条 UI 线程</strong>。
/// 一方在等待脚本/CDP 结果时另一方发起调用会互相阻塞,表现为整个界面挂死。
/// 参考项目 ZeroFall 正是因为这个问题引入了同名闸门 —— 这是踩坑后的产物,不要绕过它。
/// </para>
/// <para>
/// 所有 <c>ExecuteScript</c> 与 CDP 调用都必须包在 <see cref="EnterAsync"/> 之内。
/// </para>
/// </summary>
public static class UiScriptGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>获取闸门,返回的句柄释放时自动放行。</summary>
    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser();
    }

    /// <summary>在闸门内执行一段异步操作。</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        using (await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return await action().ConfigureAwait(false);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Gate.Release();
        }
    }
}
