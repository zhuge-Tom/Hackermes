using System;

namespace Hackermes.Base.Events;

/// <summary>
/// 进程内事件聚合器。功能模块之间零项目引用,横向通信全部经过此处。
/// <para>
/// <strong>派发是同步的</strong> —— handler 在发布者所在线程直接执行。
/// 任何可能从后台线程发布的事件,订阅方必须自行切回 UI 线程再碰 UI。
/// </para>
/// </summary>
public interface IEventBus
{
    void Subscribe<TEvent>(Action<TEvent> handler);

    void Unsubscribe<TEvent>(Action<TEvent> handler);

    void Publish<TEvent>(TEvent eventData);
}
