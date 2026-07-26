using Hookmes.Base.Diagnostics;
using System;
using System.Collections.Generic;

namespace Hookmes.Base.Events;

/// <inheritdoc cref="IEventBus"/>
public sealed class EventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _gate = new();
    private readonly IAppLogger? _logger;

    public EventBus(IAppLogger? logger = null) => _logger = logger;

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = new List<Delegate>();
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(handler);
        }
    }

    public void Unsubscribe<TEvent>(Action<TEvent> handler)
    {
        if (handler is null)
            return;

        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                return;

            list.Remove(handler);
            if (list.Count == 0)
                _handlers.Remove(typeof(TEvent));
        }
    }

    public void Publish<TEvent>(TEvent eventData)
    {
        Delegate[] snapshot;

        // 先拷贝再遍历:handler 内部退订或再订阅不会破坏本次派发。
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
                return;

            snapshot = list.ToArray();
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<TEvent>)handler)(eventData);
            }
            catch (Exception ex)
            {
                // 一个订阅者失败不应阻断其余订阅者。
                _logger?.Error($"事件 {typeof(TEvent).Name} 的订阅者抛出异常", ex);
            }
        }
    }
}
