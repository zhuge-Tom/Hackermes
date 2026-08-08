using System;
using System.Collections.Generic;
using System.Linq;

namespace Hackermes.Automation.Model;

/// <summary>
/// 元素定位描述。
/// <para>
/// 单一 CSS 选择器在真实页面里非常脆弱:类名是构建工具生成的、DOM 结构随版本变化、
/// 同一个按钮在不同状态下路径不同。因此这里携带一条<strong>按稳定性排序的候选链</strong>,
/// 执行时依次尝试,并记录实际命中的是第几个。
/// </para>
/// <para>
/// 命中靠后的候选是一个有用的信号:说明页面结构变了,脚本正在腐化,
/// 应该提示用户重新录制而不是等到彻底失败。
/// </para>
/// </summary>
public sealed record TargetSelector
{
    /// <summary>首选选择器。等于候选链的第一项。</summary>
    public required string Primary { get; init; }

    /// <summary>候选链,已按稳定性从高到低排序。</summary>
    public IReadOnlyList<SelectorCandidate> Candidates { get; init; } = [];

    /// <summary>人类可读的描述,例如"提交按钮"。用于时间线展示与脚本可读性。</summary>
    public string? Description { get; init; }

    public static TargetSelector Css(string selector, string? description = null) => new()
    {
        Primary = selector,
        Description = description,
        Candidates = [new SelectorCandidate(selector, SelectorStrategy.Css, SelectorStrategy.Css.BaseScore())]
    };

    /// <summary>从候选集合构建,自动按评分排序并取最高者为首选。</summary>
    public static TargetSelector FromCandidates(IEnumerable<SelectorCandidate> candidates, string? description = null)
    {
        var ordered = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .OrderByDescending(c => c.Score)
            .ToArray();

        if (ordered.Length == 0)
            throw new ArgumentException("候选链不能为空", nameof(candidates));

        return new TargetSelector
        {
            Primary = ordered[0].Value,
            Candidates = ordered,
            Description = description
        };
    }

    /// <summary>执行时按此顺序尝试。至少包含 <see cref="Primary"/>。</summary>
    public IEnumerable<SelectorCandidate> Attempts =>
        Candidates.Count > 0
            ? Candidates
            : [new SelectorCandidate(Primary, SelectorStrategy.Css, SelectorStrategy.Css.BaseScore())];
}

public sealed record SelectorCandidate(string Value, SelectorStrategy Strategy, int Score);

/// <summary>
/// 定位策略,按稳定性排序。选择器生成器会为一个元素同时产出多种策略的候选。
/// </summary>
public enum SelectorStrategy
{
    /// <summary>专为测试添加的属性(data-testid / data-test / data-cy)。最稳定 —— 它存在的唯一目的就是被定位。</summary>
    TestId,

    /// <summary>id 属性。稳定,但要排除构建工具生成的随机 id。</summary>
    Id,

    /// <summary>可访问性角色 + 名称。对组件化框架友好,且贴近用户认知。</summary>
    Role,

    /// <summary>文本内容精确匹配。适合按钮与链接,但受国际化影响。</summary>
    Text,

    /// <summary>结构化 CSS 路径。兜底方案,最容易随 DOM 变化失效。</summary>
    Css,

    /// <summary>XPath。仅在 CSS 无法表达时使用(例如按文本选父级)。</summary>
    XPath
}

public static class SelectorStrategyExtensions
{
    /// <summary>
    /// 策略基础分。生成器会在此基础上按具体情况加减
    /// (例如 id 看起来是随机生成的就扣分,文本过长或含数字也扣分)。
    /// </summary>
    public static int BaseScore(this SelectorStrategy strategy) => strategy switch
    {
        SelectorStrategy.TestId => 100,
        SelectorStrategy.Id => 80,
        SelectorStrategy.Role => 60,
        SelectorStrategy.Text => 45,
        SelectorStrategy.Css => 30,
        SelectorStrategy.XPath => 20,
        _ => 0
    };
}
