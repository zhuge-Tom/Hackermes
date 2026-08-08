using Hackermes.Base.Diagnostics;
using Hackermes.Cdp.ComInterop;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Cdp.Session;

/// <inheritdoc cref="ICdpSession"/>
// 注意:类不能整体标 unsafe —— 那样 async 方法里就不能 await(CS4004)。
// 指针操作一律收进局部 unsafe 块。
public sealed class CdpSession : ICdpSession, IDisposable
{
    /// <summary>
    /// 单次 CDP 调用的兜底超时。页面卡死或渲染进程崩溃时回调可能永不到达,
    /// 没有超时会让调用方永久挂起。
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly IAppLogger _logger;
    private readonly Dictionary<string, EventRegistration> _eventRegistrations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledDomains = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private nint _core;
    private bool _disposed;

    /// <param name="corePtr">ICoreWebView2 裸指针。本类不拥有它,不负责释放。</param>
    public CdpSession(string pageId, nint corePtr, IAppLogger logger)
    {
        PageId = pageId;
        _core = corePtr;
        _logger = logger.ForCategory($"Cdp:{pageId}");
    }

    public string PageId { get; }

    public bool IsAlive => !_disposed && _core != 0;

    #region 请求-响应

    public Task<string> SendAsync(string method, string? parametersJson = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDead();

        // WebView2 是 STA,所有 COM 调用必须回到 UI 线程发起。
        return UiThreadBridge.InvokeAsync(() => SendOnUiThreadAsync(method, parametersJson ?? "{}", cancellationToken));
    }

    private Task<string> SendOnUiThreadAsync(string method, string parametersJson, CancellationToken cancellationToken)
    {
        ThrowIfDead();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint handlerPtr = 0;

        var handler = new CdpMethodCompletedHandler((hr, jsonPtr) =>
        {
            // 回调在 UI 线程。先把结果读出来,再释放我们持有的那一份 CCW 引用;
            // 原生侧在回调返回前仍持有自己的引用,所以这里释放是安全的。
            var json = ComHelper.ReadString(jsonPtr);

            if (handlerPtr != 0)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>.Free((void*)handlerPtr);
                }

                handlerPtr = 0;
            }

            if (hr < 0)
                tcs.TrySetException(new CdpException($"CDP 调用 {method} 返回错误 0x{hr:X8}") { Method = method });
            else
                tcs.TrySetResult(json ?? "{}");
        });

        unsafe
        {
            handlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>
                .ConvertToUnmanaged(handler);
        }

        var callHr = ICoreWebView2VTable.CallDevToolsProtocolMethod(_core, method, parametersJson, handlerPtr);

        if (callHr < 0)
        {
            // 调用本身就失败了,回调不会到达,必须立刻释放。
            if (handlerPtr != 0)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>.Free((void*)handlerPtr);
                }

                handlerPtr = 0;
            }

            throw new CdpException($"发起 CDP 调用 {method} 失败 0x{callHr:X8}") { Method = method };
        }

        return AwaitWithTimeoutAsync(tcs.Task, method, cancellationToken);
    }

    private static async Task<string> AwaitWithTimeoutAsync(Task<string> task, string method, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CallTimeout);

        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);

        if (!ReferenceEquals(completed, task))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CdpException($"CDP 调用 {method} 超时({CallTimeout.TotalSeconds:F0} 秒)") { Method = method };
        }

        return await task.ConfigureAwait(false);
    }

    #endregion

    #region 事件订阅

    public async Task<IDisposable> SubscribeAsync(
        string eventName,
        Action<CdpEventArgs> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDead();

        return await UiThreadBridge.InvokeAsync(() => SubscribeOnUiThread(eventName, handler)).ConfigureAwait(false);
    }

    private IDisposable SubscribeOnUiThread(string eventName, Action<CdpEventArgs> handler)
    {
        lock (_gate)
        {
            ThrowIfDead();

            if (!_eventRegistrations.TryGetValue(eventName, out var registration))
            {
                registration = CreateRegistration(eventName);
                _eventRegistrations[eventName] = registration;
            }

            registration.Handlers.Add(handler);
            return new Subscription(this, eventName, handler);
        }
    }

    /// <summary>为一个事件名建立接收器。调用方需持有 <see cref="_gate"/>。</summary>
    private EventRegistration CreateRegistration(string eventName)
    {
        var hr = ICoreWebView2VTable.GetDevToolsProtocolEventReceiver(_core, eventName, out var receiverPtr);

        if (hr < 0 || receiverPtr == 0)
            throw new CdpException($"取事件接收器 {eventName} 失败 0x{hr:X8}");

        var registration = new EventRegistration(eventName, receiverPtr);

        var comHandler = new CdpEventHandler(argsPtr =>
        {
            if (argsPtr == 0)
                return;

            var readHr = ICoreWebView2DevToolsProtocolEventReceivedEventArgsVTable
                .GetParameterObjectAsJson(argsPtr, out var jsonPtr);

            if (readHr < 0)
                return;

            // 参数 JSON 由被调用方分配,读完要释放。
            var json = ComHelper.ReadAndFreeString(jsonPtr) ?? "{}";
            DispatchEvent(eventName, json);
        });

        registration.ComHandler = comHandler;

        unsafe
        {
            registration.HandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2DevToolsProtocolEventReceivedEventHandler>
                .ConvertToUnmanaged(comHandler);
        }

        var addHr = ICoreWebView2DevToolsProtocolEventReceiverVTable
            .Add(receiverPtr, registration.HandlerPtr, out var token);

        if (addHr < 0)
        {
            registration.ReleaseNative();
            throw new CdpException($"订阅事件 {eventName} 失败 0x{addHr:X8}");
        }

        registration.Token = token;
        _logger.Debug($"已订阅 {eventName}");
        return registration;
    }

    private void DispatchEvent(string eventName, string json)
    {
        Action<CdpEventArgs>[] snapshot;

        lock (_gate)
        {
            if (!_eventRegistrations.TryGetValue(eventName, out var registration) || registration.Handlers.Count == 0)
                return;

            snapshot = registration.Handlers.ToArray();
        }

        var args = new CdpEventArgs(eventName, json);

        foreach (var handler in snapshot)
        {
            try
            {
                handler(args);
            }
            catch (Exception ex)
            {
                // 一个订阅者出错不影响其余订阅者,更不能让异常回到原生栈。
                _logger.Error($"事件 {eventName} 的订阅者抛出异常", ex);
            }
        }
    }

    private void Unsubscribe(string eventName, Action<CdpEventArgs> handler)
    {
        lock (_gate)
        {
            if (!_eventRegistrations.TryGetValue(eventName, out var registration))
                return;

            registration.Handlers.Remove(handler);

            if (registration.Handlers.Count > 0)
                return;

            // 最后一个订阅者走了,拆掉原生侧的接收器。
            _eventRegistrations.Remove(eventName);
            DetachRegistration(registration);
        }
    }

    private void DetachRegistration(EventRegistration registration)
    {
        if (_core != 0 && registration.Token != 0 && registration.ReceiverPtr != 0)
        {
            UiThreadBridge.Post(() =>
            {
                try
                {
                    ICoreWebView2DevToolsProtocolEventReceiverVTable.Remove(registration.ReceiverPtr, registration.Token);
                }
                catch
                {
                    // 页面可能已经没了,忽略。
                }
                finally
                {
                    registration.ReleaseNative();
                }
            });

            return;
        }

        registration.ReleaseNative();
    }

    #endregion

    public async Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_enabledDomains.Add(domain))
                return;
        }

        try
        {
            await SendAsync($"{domain}.enable", "{}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _enabledDomains.Remove(domain);
            }

            throw;
        }
    }

    private void ThrowIfDead()
    {
        if (_disposed || _core == 0)
            throw new CdpException($"CDP 会话 {PageId} 已关闭");
    }

    public void Dispose()
    {
        EventRegistration[] registrations;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            registrations = [.. _eventRegistrations.Values];
            _eventRegistrations.Clear();
            _enabledDomains.Clear();
        }

        foreach (var registration in registrations)
            DetachRegistration(registration);

        _core = 0;
        _logger.Debug("会话已关闭");
    }

    /// <summary>一个事件名对应的原生资源与订阅者列表。</summary>
    private sealed class EventRegistration(string eventName, nint receiverPtr)
    {
        public string EventName { get; } = eventName;
        public nint ReceiverPtr { get; private set; } = receiverPtr;
        public nint HandlerPtr { get; set; }
        public long Token { get; set; }

        /// <summary>持有强引用,防止 CCW 尚在原生侧使用时托管对象被回收。</summary>
        public CdpEventHandler? ComHandler { get; set; }

        public List<Action<CdpEventArgs>> Handlers { get; } = [];

        public void ReleaseNative()
        {
            if (HandlerPtr != 0)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2DevToolsProtocolEventReceivedEventHandler>.Free((void*)HandlerPtr);
                }

                HandlerPtr = 0;
            }

            if (ReceiverPtr != 0)
            {
                ComHelper.Release(ReceiverPtr);
                ReceiverPtr = 0;
            }

            ComHandler = null;
        }
    }

    private sealed class Subscription(CdpSession session, string eventName, Action<CdpEventArgs> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            session.Unsubscribe(eventName, handler);
        }
    }
}
