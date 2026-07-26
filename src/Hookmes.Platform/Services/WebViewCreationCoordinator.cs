using Hookmes.Base.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Platform.Services;

/// <summary>
/// WebView2 初始化互斥。
/// <para>
/// <strong>同一时刻只允许一个 WebView2 实例初始化。</strong>
/// 并发初始化会在 WebView2 运行时内部争抢用户数据目录,表现为其中一个永远卡在创建中。
/// 参考项目在浏览器与 AI 面板两个 WebView 上都撞过这个问题,因此引入了同名协调器。
/// </para>
/// <para>看门狗是必需的:初始化失败时回调可能永不到达,不设超时会让后续所有创建饿死。</para>
/// </summary>
public sealed class WebViewCreationCoordinator
{
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAppLogger _logger;

    public WebViewCreationCoordinator(IAppLogger logger) =>
        _logger = logger.ForCategory(nameof(WebViewCreationCoordinator));

    /// <summary>
    /// 取得创建许可。调用方<strong>必须</strong>在初始化完成或失败后释放返回的句柄;
    /// 即便忘了,看门狗也会在超时后自动放行。
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string owner, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _logger.Debug($"WebView2 创建许可已授予: {owner}");
        return new Lease(this, owner);
    }

    private void Release(string owner, bool byWatchdog)
    {
        if (byWatchdog)
            _logger.Warn($"WebView2 创建许可超时自动释放: {owner}(初始化可能已失败)");
        else
            _logger.Debug($"WebView2 创建许可已释放: {owner}");

        try
        {
            _gate.Release();
        }
        catch (SemaphoreFullException)
        {
            // 看门狗与正常释放竞争时可能重复,忽略。
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly WebViewCreationCoordinator _owner;
        private readonly string _name;
        private readonly CancellationTokenSource _watchdog = new();
        private int _released;

        public Lease(WebViewCreationCoordinator owner, string name)
        {
            _owner = owner;
            _name = name;

            _ = Task.Delay(WatchdogTimeout, _watchdog.Token)
                .ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                        ReleaseOnce(byWatchdog: true);
                }, TaskScheduler.Default);
        }

        public void Dispose()
        {
            _watchdog.Cancel();
            ReleaseOnce(byWatchdog: false);
            _watchdog.Dispose();
        }

        private void ReleaseOnce(bool byWatchdog)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            _owner.Release(_name, byWatchdog);
        }
    }
}
