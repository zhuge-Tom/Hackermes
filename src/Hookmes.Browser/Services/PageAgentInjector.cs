using Hookmes.Base.Diagnostics;
using Hookmes.Base.Events;
using Hookmes.Cdp;
using Hookmes.Cdp.Session;
using Hookmes.PageAgent;
using Hookmes.Platform.Events;
using System;
using System.Threading.Tasks;

namespace Hookmes.Browser.Services;

/// <summary>
/// 把 Page Agent 装进页面,并接收它的回传。
/// <para>
/// 装配顺序不能变:
/// <list type="number">
/// <item><c>Runtime.addBinding</c> 注册回传函数</item>
/// <item>订阅 <c>Runtime.bindingCalled</c></item>
/// <item><c>Page.addScriptToEvaluateOnNewDocument</c> 预注入脚本</item>
/// <item>最后才导航</item>
/// </list>
/// 预注入只对<strong>之后加载的文档</strong>生效,所以必须赶在导航之前完成;
/// 而 binding 要先于脚本存在,否则脚本启动时拿不到回传通道。
/// </para>
/// </summary>
public sealed class PageAgentInjector(IEventBus eventBus, IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForCategory(nameof(PageAgentInjector));

    /// <summary>
    /// 为一个会话装配 Agent。返回是否成功 —— 失败只意味着失去页面内观测能力,
    /// 浏览本身不受影响。
    /// </summary>
    public async Task<bool> InstallAsync(ICdpSession session)
    {
        // 绑定名每会话随机,减少被页面特征识别的可能。
        var bindingName = "__hookmes_" + Guid.NewGuid().ToString("N")[..8] + "__";

        try
        {
            await session.SendAsync("Runtime.addBinding", CdpJson.Params(("name", bindingName)))
                .ConfigureAwait(false);

            await session.SubscribeAsync("Runtime.bindingCalled", e => OnBindingCalled(session.PageId, bindingName, e))
                .ConfigureAwait(false);

            var script = PageAgentScript.PrepareMainWorld(bindingName);

            await session.SendAsync(
                    "Page.addScriptToEvaluateOnNewDocument",
                    CdpJson.Params(("source", script)))
                .ConfigureAwait(false);

            _logger.Info($"Page Agent 已装配到 {session.PageId}(绑定 {bindingName},脚本 {script.Length} 字符)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Page Agent 装配失败: {session.PageId}", ex);
            return false;
        }
    }

    private void OnBindingCalled(string pageId, string expectedBinding, CdpEventArgs e)
    {
        // 同一页面可能有多个 binding(将来的隔离世界通道),按名字过滤。
        var name = CdpJson.TryGetString(e.ParametersJson, "name");
        if (!string.Equals(name, expectedBinding, StringComparison.Ordinal))
            return;

        var payload = CdpJson.TryGetString(e.ParametersJson, "payload");
        if (string.IsNullOrEmpty(payload))
            return;

        var kind = CdpJson.TryGetString(payload, "t") ?? "unknown";
        var subKind = CdpJson.TryGetString(payload, "k");

        eventBus.Publish(new PageAgentMessageEvent(pageId, kind, subKind, payload));
    }
}
