using Hookmes.Automation.Model;
using Hookmes.Automation.Timeline;
using Hookmes.Base.Diagnostics;
using Hookmes.Cdp;
using Hookmes.Cdp.Session;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Execution;

/// <summary>
/// 动作执行器。<strong>所有页面操作的唯一落点</strong> ——
/// 人工录制、终端命令、AI 工具、脚本回放最终都走到这里。
/// </summary>
public sealed class ActionExecutor(ICdpSessionRegistry registry, IAppLogger logger, ActionTimelineStore timeline)
{
    private readonly IAppLogger _logger = logger.ForCategory(nameof(ActionExecutor));

    /// <summary>动作执行完成。时间线与录制器订阅此事件。</summary>
    public event Action<ActionDescriptor, ActionResult>? Executed;

    public async Task<ActionResult> ExecuteAsync(
        string pageId,
        ActionDescriptor action,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var session = registry.Get(pageId);
        if (session is null)
        {
            var miss = ActionResult.Fail($"页面 {pageId} 没有可用的 CDP 会话", sw.Elapsed);
            timeline.Append(pageId, action, miss);
            Executed?.Invoke(action, miss);
            return miss;
        }

        ActionResult result;

        try
        {
            result = action.Kind switch
            {
                ActionKind.Navigate => await NavigateAsync(session, action, cancellationToken).ConfigureAwait(false),
                ActionKind.Evaluate => await EvaluateAsync(session, action, cancellationToken).ConfigureAwait(false),
                ActionKind.Screenshot => await ScreenshotAsync(session, cancellationToken).ConfigureAwait(false),
                ActionKind.Wait => await WaitAsync(session, action, cancellationToken).ConfigureAwait(false),
                ActionKind.Assert => await AssertAsync(session, action, cancellationToken).ConfigureAwait(false),
                ActionKind.Click or ActionKind.Type or ActionKind.Hover or ActionKind.Select or ActionKind.Scroll
                    => await ElementActionAsync(session, action, cancellationToken).ConfigureAwait(false),
                ActionKind.Press => await PressAsync(session, action, cancellationToken).ConfigureAwait(false),
                _ => ActionResult.Fail($"尚未实现的动作类型: {action.Kind}")
            };

            result = result with { Elapsed = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            result = ActionResult.Fail("动作被取消", sw.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.Error($"执行 {action.Describe()} 失败", ex);
            result = ActionResult.Fail(ex.Message, sw.Elapsed);
        }

        _logger.Debug($"{action.Describe()} → {(result.Success ? "成功" : "失败: " + result.Error)} ({result.Elapsed.TotalMilliseconds:F0} ms)");
        timeline.Append(pageId, action, result);
        Executed?.Invoke(action, result);
        return result;
    }

    #region 不针对元素的动作

    private static async Task<ActionResult> NavigateAsync(ICdpSession session, ActionDescriptor action, CancellationToken ct)
    {
        var url = action.Arg("url");
        if (string.IsNullOrWhiteSpace(url))
            return ActionResult.Fail("导航缺少 url 参数");

        await session.SendAsync("Page.navigate", CdpJson.Params(("url", url)), ct).ConfigureAwait(false);
        return ActionResult.Ok();
    }

    private static async Task<ActionResult> EvaluateAsync(ICdpSession session, ActionDescriptor action, CancellationToken ct)
    {
        var expression = action.Arg("expression");
        if (string.IsNullOrWhiteSpace(expression))
            return ActionResult.Fail("求值缺少 expression 参数");

        var json = await session.SendAsync(
            "Runtime.evaluate",
            CdpJson.Params(("expression", expression), ("returnByValue", true), ("awaitPromise", true)),
            ct).ConfigureAwait(false);

        // 页面里抛出的异常不是执行器的失败,但要如实回报。
        if (CdpJson.TryGetElement(json, "exceptionDetails") is { ValueKind: JsonValueKind.Object })
        {
            var text = CdpJson.TryGetString(json, "exceptionDetails", "exception", "description")
                       ?? CdpJson.TryGetString(json, "exceptionDetails", "text")
                       ?? "求值抛出异常";
            return ActionResult.Fail(text);
        }

        var value = CdpJson.TryGetElement(json, "result", "value");
        return ActionResult.Ok(value?.ToString());
    }

    private static async Task<ActionResult> ScreenshotAsync(ICdpSession session, CancellationToken ct)
    {
        var json = await session.SendAsync(
            "Page.captureScreenshot",
            CdpJson.Params(("format", "png")),
            ct).ConfigureAwait(false);

        var data = CdpJson.TryGetString(json, "data");
        return data is null
            ? ActionResult.Fail("截图未返回数据")
            : ActionResult.Ok(data);
    }

    /// <summary>
    /// 等待条件成立。支持 selector(元素出现)、gone(元素消失)、expression(表达式为真)。
    /// </summary>
    private async Task<ActionResult> WaitAsync(ICdpSession session, ActionDescriptor action, CancellationToken ct)
    {
        var condition = action.Arg("condition") ?? "selector";
        var value = action.Arg("value") ?? action.Target?.Primary;

        if (string.IsNullOrWhiteSpace(value))
            return ActionResult.Fail("等待缺少目标");

        var deadline = DateTime.UtcNow + action.Options.Timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var satisfied = condition switch
            {
                "gone" => !(await ProbeAsync(session, value, SelectorStrategy.Css, ct).ConfigureAwait(false)).Found,
                "expression" => await EvaluateTruthyAsync(session, value, ct).ConfigureAwait(false),
                _ => (await ProbeAsync(session, value, SelectorStrategy.Css, ct).ConfigureAwait(false)).Found
            };

            if (satisfied)
                return ActionResult.Ok();

            await Task.Delay(action.Options.RetryInterval, ct).ConfigureAwait(false);
        }

        return ActionResult.Fail($"等待超时: {condition} {value}");
    }

    private static async Task<bool> EvaluateTruthyAsync(ICdpSession session, string expression, CancellationToken ct)
    {
        try
        {
            var json = await session.SendAsync(
                "Runtime.evaluate",
                CdpJson.Params(("expression", $"!!({expression})"), ("returnByValue", true)),
                ct).ConfigureAwait(false);

            return CdpJson.TryGetElement(json, "result", "value") is { ValueKind: JsonValueKind.True };
        }
        catch
        {
            return false;
        }
    }

    private async Task<ActionResult> AssertAsync(ICdpSession session, ActionDescriptor action, CancellationToken ct)
    {
        var assertion = action.Arg("assertion")?.ToLowerInvariant() ?? "exists";
        var selector = action.Target?.Primary ?? action.Arg("selector");

        bool passed;
        switch (assertion)
        {
            case "exists":
                if (string.IsNullOrWhiteSpace(selector)) return ActionResult.Fail("Assert exists requires a selector.");
                passed = (await ProbeAsync(session, selector, SelectorStrategy.Css, ct).ConfigureAwait(false)).Found;
                break;
            case "gone":
                if (string.IsNullOrWhiteSpace(selector)) return ActionResult.Fail("Assert gone requires a selector.");
                passed = !(await ProbeAsync(session, selector, SelectorStrategy.Css, ct).ConfigureAwait(false)).Found;
                break;
            case "text":
                if (string.IsNullOrWhiteSpace(selector)) return ActionResult.Fail("Assert text requires a selector.");
                var expected = action.Arg("value") ?? string.Empty;
                var expression = $"(document.querySelector({JsonSerializer.Serialize(selector)})?.textContent ?? '').includes({JsonSerializer.Serialize(expected)})";
                passed = await EvaluateTruthyAsync(session, expression, ct).ConfigureAwait(false);
                break;
            case "expression":
                var expressionValue = action.Arg("value");
                if (string.IsNullOrWhiteSpace(expressionValue)) return ActionResult.Fail("Assert expression requires an expression.");
                passed = await EvaluateTruthyAsync(session, expressionValue, ct).ConfigureAwait(false);
                break;
            default:
                return ActionResult.Fail($"Unsupported assertion: {assertion}.");
        }

        return passed ? ActionResult.Ok() : ActionResult.Fail($"Assertion failed: {assertion}.");
    }

    #endregion

    #region 针对元素的动作

    /// <summary>
    /// 定位元素并执行交互。选择器候选链依次尝试,记录实际命中的是第几个。
    /// </summary>
    private async Task<ActionResult> ElementActionAsync(ICdpSession session, ActionDescriptor action, CancellationToken ct)
    {
        if (action.Target is null)
            return ActionResult.Fail($"{action.Kind} 需要目标元素");

        var deadline = DateTime.UtcNow + action.Options.Timeout;
        string? lastReason = null;

        // 外层循环等待元素就绪,内层遍历候选链。
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var index = 0;

            foreach (var candidate in action.Target.Attempts)
            {
                var probe = await ProbeAsync(session, candidate.Value, candidate.Strategy, ct).ConfigureAwait(false);

                if (!probe.Found)
                {
                    lastReason = "元素未找到";
                    index++;
                    continue;
                }

                if (action.Options.ScrollIntoView && !probe.InViewport)
                {
                    await CallFunctionAsync(session, ElementProbe.ScrollIntoViewFunction,
                        [candidate.Value, candidate.Strategy.ToString()], ct).ConfigureAwait(false);

                    probe = await ProbeAsync(session, candidate.Value, candidate.Strategy, ct).ConfigureAwait(false);
                }

                if (action.Options.RequireInteractable && !probe.Interactable)
                {
                    lastReason = probe.Disabled ? "元素被禁用" : probe.Covered ? "元素被遮挡" : "元素不可见";
                    index++;
                    continue;
                }

                var outcome = await PerformAsync(session, action, candidate, probe, ct).ConfigureAwait(false);

                return outcome.Success
                    ? outcome with { MatchedSelector = candidate.Value, MatchedIndex = index }
                    : outcome;
            }

            await Task.Delay(action.Options.RetryInterval, ct).ConfigureAwait(false);
        }

        return ActionResult.Fail($"定位失败({lastReason ?? "超时"}): {action.Target.Primary}");
    }

    private async Task<ActionResult> PerformAsync(
        ICdpSession session,
        ActionDescriptor action,
        SelectorCandidate candidate,
        ProbeResult probe,
        CancellationToken ct)
    {
        switch (action.Kind)
        {
            case ActionKind.Click:
                await DispatchMouseAsync(session, "mouseMoved", probe.X, probe.Y, 0, ct).ConfigureAwait(false);
                await DispatchMouseAsync(session, "mousePressed", probe.X, probe.Y, 1, ct).ConfigureAwait(false);
                await DispatchMouseAsync(session, "mouseReleased", probe.X, probe.Y, 1, ct).ConfigureAwait(false);
                return ActionResult.Ok();

            case ActionKind.Hover:
                await DispatchMouseAsync(session, "mouseMoved", probe.X, probe.Y, 0, ct).ConfigureAwait(false);
                return ActionResult.Ok();

            case ActionKind.Type:
            {
                var focus = await CallFunctionAsync(session, ElementProbe.FocusFunction,
                    [candidate.Value, candidate.Strategy.ToString(), action.Arg("clearFirst") ?? "true"],
                    ct).ConfigureAwait(false);

                if (focus != "ok")
                    return ActionResult.Fail("无法聚焦元素");

                var text = action.Arg("text") ?? string.Empty;

                // insertText 走的是输入法路径,能正确触发 input 事件且支持中文;
                // 逐字符 dispatchKeyEvent 只适合需要精确按键序列的场景。
                await session.SendAsync("Input.insertText", CdpJson.Params(("text", text)), ct).ConfigureAwait(false);
                return ActionResult.Ok();
            }

            case ActionKind.Select:
            {
                var value = action.Arg("value") ?? string.Empty;
                var script = $$"""
                    (function (sel, val) {
                      var el = document.querySelector(sel);
                      if (!el) return 'not-found';
                      el.value = val;
                      el.dispatchEvent(new Event('change', { bubbles: true }));
                      return 'ok';
                    })
                    """;

                var outcome = await CallFunctionAsync(session, script, [candidate.Value, value], ct).ConfigureAwait(false);
                return outcome == "ok" ? ActionResult.Ok() : ActionResult.Fail("下拉选择失败");
            }

            case ActionKind.Scroll:
                await CallFunctionAsync(session, ElementProbe.ScrollIntoViewFunction,
                    [candidate.Value, candidate.Strategy.ToString()], ct).ConfigureAwait(false);
                return ActionResult.Ok();

            default:
                return ActionResult.Fail($"元素动作不支持 {action.Kind}");
        }
    }

    private static async Task<ActionResult> PressAsync(ICdpSession session, ActionDescriptor action, CancellationToken ct)
    {
        var key = action.Arg("key");
        if (string.IsNullOrWhiteSpace(key))
            return ActionResult.Fail("按键缺少 key 参数");

        var (code, keyCode) = MapKey(key);

        await session.SendAsync("Input.dispatchKeyEvent",
            CdpJson.Params(("type", "keyDown"), ("key", key), ("code", code), ("windowsVirtualKeyCode", keyCode)),
            ct).ConfigureAwait(false);

        await session.SendAsync("Input.dispatchKeyEvent",
            CdpJson.Params(("type", "keyUp"), ("key", key), ("code", code), ("windowsVirtualKeyCode", keyCode)),
            ct).ConfigureAwait(false);

        return ActionResult.Ok();
    }

    private static (string Code, int KeyCode) MapKey(string key) => key switch
    {
        "Enter" => ("Enter", 13),
        "Tab" => ("Tab", 9),
        "Escape" => ("Escape", 27),
        "Backspace" => ("Backspace", 8),
        "Delete" => ("Delete", 46),
        "ArrowUp" => ("ArrowUp", 38),
        "ArrowDown" => ("ArrowDown", 40),
        "ArrowLeft" => ("ArrowLeft", 37),
        "ArrowRight" => ("ArrowRight", 39),
        _ => (key, key.Length == 1 ? char.ToUpperInvariant(key[0]) : 0)
    };

    private static Task DispatchMouseAsync(ICdpSession session, string type, double x, double y, int clickCount, CancellationToken ct) =>
        session.SendAsync("Input.dispatchMouseEvent",
            CdpJson.Params(
                ("type", type),
                ("x", x),
                ("y", y),
                ("button", type == "mouseMoved" ? "none" : "left"),
                ("buttons", type == "mousePressed" ? 1 : 0),
                ("clickCount", clickCount)),
            ct);

    #endregion

    #region 页面内求值辅助

    private sealed record ProbeResult(
        bool Found,
        double X,
        double Y,
        bool Visible,
        bool Interactable,
        bool Disabled,
        bool Covered,
        bool InViewport,
        string? Tag);

    private async Task<ProbeResult> ProbeAsync(ICdpSession session, string selector, SelectorStrategy strategy, CancellationToken ct)
    {
        var json = await CallFunctionAsync(session, ElementProbe.ResolveFunction,
            [selector, strategy.ToString()], ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(json))
            return new ProbeResult(false, 0, 0, false, false, false, false, false, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True)
                return new ProbeResult(false, 0, 0, false, false, false, false, false, null);

            return new ProbeResult(
                true,
                root.GetProperty("x").GetDouble(),
                root.GetProperty("y").GetDouble(),
                root.GetProperty("visible").GetBoolean(),
                root.GetProperty("interactable").GetBoolean(),
                root.GetProperty("disabled").GetBoolean(),
                root.GetProperty("covered").GetBoolean(),
                root.GetProperty("inViewport").GetBoolean(),
                root.TryGetProperty("tag", out var tag) ? tag.GetString() : null);
        }
        catch (Exception ex)
        {
            _logger.Debug($"解析探测结果失败: {ex.Message}");
            return new ProbeResult(false, 0, 0, false, false, false, false, false, null);
        }
    }

    /// <summary>
    /// 调用一个页面内函数并取回字符串结果。
    /// <para>
    /// 参数经 <see cref="JsonSerializer"/> 编码后内联进表达式,而不是拼字符串 ——
    /// 选择器里出现引号、反斜杠、换行都很常见,手工拼接必然出问题。
    /// </para>
    /// </summary>
    private static async Task<string?> CallFunctionAsync(
        ICdpSession session,
        string functionSource,
        string[] args,
        CancellationToken ct)
    {
        var encoded = string.Join(", ", Array.ConvertAll(args, a => JsonSerializer.Serialize(a)));
        var expression = $"({functionSource})({encoded})";

        var json = await session.SendAsync(
            "Runtime.evaluate",
            CdpJson.Params(("expression", expression), ("returnByValue", true)),
            ct).ConfigureAwait(false);

        return CdpJson.TryGetString(json, "result", "value");
    }

    #endregion
}
