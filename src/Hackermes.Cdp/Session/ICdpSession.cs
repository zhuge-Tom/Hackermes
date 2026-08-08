using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Cdp.Session;

/// <summary>
/// 一个页面的 CDP 通道。
/// <para>
/// 请求-响应与事件订阅都在这里。所有调用最终落到 WebView2 的 COM 接口上,
/// 而 WebView2 是 STA 的,因此内部会自行切到 UI 线程 —— 调用方不需要关心线程。
/// </para>
/// </summary>
public interface ICdpSession
{
    /// <summary>所属浏览器标签页的标识。</summary>
    string PageId { get; }

    bool IsAlive { get; }

    /// <summary>
    /// 调用 CDP 方法,返回结果 JSON。
    /// </summary>
    /// <param name="method">形如 <c>Page.navigate</c> 的域方法名。</param>
    /// <param name="parametersJson">参数 JSON,null 表示无参。</param>
    Task<string> SendAsync(string method, string? parametersJson = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅 CDP 事件。
    /// <para>
    /// WebView2 的事件接收器是<strong>按事件名</strong>创建的,不是一条总线;
    /// 同一事件的多个订阅者会共享一个接收器,由实现做引用计数。
    /// </para>
    /// <param name="eventName">形如 <c>Network.responseReceived</c>。</param>
    /// <returns>释放即退订。</returns>
    Task<IDisposable> SubscribeAsync(string eventName, Action<CdpEventArgs> handler, CancellationToken cancellationToken = default);

    /// <summary>启用某个 CDP 域(等价于调用 <c>{domain}.enable</c>),幂等。</summary>
    Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default);
}

/// <summary>CDP 事件负载。</summary>
public sealed record CdpEventArgs(string EventName, string ParametersJson);

/// <summary>CDP 调用失败。</summary>
public sealed class CdpException(string message, Exception? inner = null) : Exception(message, inner)
{
    public string? Method { get; init; }
}
