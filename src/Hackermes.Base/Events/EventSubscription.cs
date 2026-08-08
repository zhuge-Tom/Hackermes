using System;

namespace Hackermes.Base.Events;

/// <summary>把一次订阅包装成 <see cref="IDisposable"/>,便于集中退订。</summary>
public sealed class EventSubscription<TEvent> : IDisposable
{
    private readonly IEventBus _eventBus;
    private Action<TEvent>? _handler;

    public EventSubscription(IEventBus eventBus, Action<TEvent> handler)
    {
        _eventBus = eventBus;
        _handler = handler;
        _eventBus.Subscribe(handler);
    }

    public void Dispose()
    {
        if (_handler is null)
            return;

        _eventBus.Unsubscribe(_handler);
        _handler = null;
    }
}

public static class EventBusExtensions
{
    /// <summary>订阅并返回可释放句柄。ViewModel 请优先用 <c>ViewModelBase.SubscribeEvent</c>。</summary>
    public static IDisposable SubscribeDisposable<TEvent>(this IEventBus eventBus, Action<TEvent> handler) =>
        new EventSubscription<TEvent>(eventBus, handler);
}
