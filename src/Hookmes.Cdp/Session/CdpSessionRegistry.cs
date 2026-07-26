using Hookmes.Base.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hookmes.Cdp.Session;

/// <summary>
/// 页面 CDP 会话的登记处。
/// <para>
/// 这是模块解耦的关键:浏览器模块在创建标签页时登记会话,
/// 检查面板与自动化模块按 <c>pageId</c> 取用 —— 三者之间没有项目引用。
/// </para>
/// </summary>
public interface ICdpSessionRegistry
{
    ICdpSession? Get(string pageId);

    IReadOnlyList<ICdpSession> All { get; }

    /// <summary>登记会话,返回的句柄释放即注销。</summary>
    IDisposable Register(ICdpSession session);

    event Action<ICdpSession>? SessionOpened;
    event Action<string>? SessionClosed;
}

public sealed class CdpSessionRegistry(IAppLogger logger) : ICdpSessionRegistry
{
    private readonly Dictionary<string, ICdpSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IAppLogger _logger = logger.ForCategory(nameof(CdpSessionRegistry));

    public event Action<ICdpSession>? SessionOpened;
    public event Action<string>? SessionClosed;

    public ICdpSession? Get(string pageId)
    {
        if (string.IsNullOrEmpty(pageId))
            return null;

        lock (_gate)
        {
            return _sessions.TryGetValue(pageId, out var session) && session.IsAlive ? session : null;
        }
    }

    public IReadOnlyList<ICdpSession> All
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.Where(s => s.IsAlive).ToArray();
            }
        }
    }

    public IDisposable Register(ICdpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            _sessions[session.PageId] = session;
        }

        _logger.Debug($"会话已登记: {session.PageId}");
        SessionOpened?.Invoke(session);

        return new Registration(this, session.PageId);
    }

    private void Unregister(string pageId)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(pageId))
                return;
        }

        _logger.Debug($"会话已注销: {pageId}");
        SessionClosed?.Invoke(pageId);
    }

    private sealed class Registration(CdpSessionRegistry registry, string pageId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Unregister(pageId);
        }
    }
}
