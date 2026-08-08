using CommunityToolkit.Mvvm.ComponentModel;
using Hackermes.Base.Events;
using System;
using System.Collections.Generic;

namespace Hackermes.Base.Mvvm;

/// <summary>
/// 全应用 ViewModel 基类。
/// <para>
/// <strong>约定</strong>:ViewModel 订阅事件一律走 <see cref="SubscribeEvent{TEvent}"/>,
/// 不要直接调 <c>IEventBus.Subscribe</c>。EventBus 持有 handler 的强引用,
/// 漏退订会让整个 ViewModel 连同它引用的视图永久留在内存里。
/// </para>
/// </summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>订阅事件,并把退订登记到本 ViewModel 的生命周期上。</summary>
    protected void SubscribeEvent<TEvent>(IEventBus eventBus, Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(handler);

        _subscriptions.Add(eventBus.SubscribeDisposable(handler));
    }

    /// <summary>登记任意需要随 ViewModel 一同释放的资源。</summary>
    protected void TrackDisposable(IDisposable disposable) => _subscriptions.Add(disposable);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var subscription in _subscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch
            {
                // 释放阶段不再抛错。
            }
        }

        _subscriptions.Clear();
        OnDispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>派生类释放自有资源。基类已处理事件退订。</summary>
    protected virtual void OnDispose()
    {
    }
}
