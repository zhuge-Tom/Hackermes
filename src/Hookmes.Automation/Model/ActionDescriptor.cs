using System;
using System.Collections.Generic;

namespace Hookmes.Automation.Model;

/// <summary>
/// 一个页面动作。
/// <para>
/// 这是整个架构的核心抽象:人工点击、终端命令、AI 工具调用、脚本回放
/// 四条路径全部收敛成这一种可序列化的描述,再由单一执行器落地。
/// </para>
/// <para>
/// 如果四条路径各写一份实现,行为必然发散,录制也无从下手 ——
/// 统一之后,录制、审计、回放都成了架构的自然结果而非额外功能。
/// </para>
/// </summary>
public sealed record ActionDescriptor
{
    public required ActionKind Kind { get; init; }

    /// <summary>目标元素。导航、求值等不针对元素的动作为 null。</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>动作参数。含义随 <see cref="Kind"/> 变化,见各执行分支。</summary>
    public IReadOnlyDictionary<string, string?> Args { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public ActionOptions Options { get; init; } = ActionOptions.Default;

    /// <summary>动作来源。用于时间线区分与审计。</summary>
    public ActionOrigin Origin { get; init; } = ActionOrigin.Script;

    /// <summary>人类可读的一行描述,用于时间线与日志。</summary>
    public string Describe()
    {
        var target = Target is null ? string.Empty : $" {Target.Primary}";

        return Kind switch
        {
            ActionKind.Navigate => $"打开 {Arg("url")}",
            ActionKind.Click => $"点击{target}",
            ActionKind.Type => $"输入{target} = \"{Arg("text")}\"",
            ActionKind.Select => $"选择{target} = \"{Arg("value")}\"",
            ActionKind.Hover => $"悬停{target}",
            ActionKind.Press => $"按键 {Arg("key")}",
            ActionKind.Scroll => $"滚动{target}",
            ActionKind.Wait => $"等待 {Arg("condition")}",
            ActionKind.Evaluate => $"求值 {Truncate(Arg("expression"), 60)}",
            ActionKind.Assert => $"断言 {Arg("assertion")}{target}",
            ActionKind.Screenshot => "截图",
            _ => Kind.ToString()
        };
    }

    public string? Arg(string key) => Args.TryGetValue(key, out var value) ? value : null;

    private static string Truncate(string? text, int max) =>
        text is null ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    public static ActionDescriptor Navigate(string url, ActionOrigin origin = ActionOrigin.Script) => new()
    {
        Kind = ActionKind.Navigate,
        Origin = origin,
        Args = new Dictionary<string, string?>(StringComparer.Ordinal) { ["url"] = url }
    };

    public static ActionDescriptor Click(TargetSelector target, ActionOrigin origin = ActionOrigin.Script) => new()
    {
        Kind = ActionKind.Click,
        Target = target,
        Origin = origin
    };

    public static ActionDescriptor Type(TargetSelector target, string text, bool clearFirst = true,
        ActionOrigin origin = ActionOrigin.Script) => new()
    {
        Kind = ActionKind.Type,
        Target = target,
        Origin = origin,
        Args = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["clearFirst"] = clearFirst ? "true" : "false"
        }
    };

    public static ActionDescriptor Evaluate(string expression, ActionOrigin origin = ActionOrigin.Script) => new()
    {
        Kind = ActionKind.Evaluate,
        Origin = origin,
        Args = new Dictionary<string, string?>(StringComparer.Ordinal) { ["expression"] = expression }
    };

    public static ActionDescriptor WaitFor(string condition, string? value = null,
        ActionOrigin origin = ActionOrigin.Script) => new()
    {
        Kind = ActionKind.Wait,
        Origin = origin,
        Args = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["condition"] = condition,
            ["value"] = value
        }
    };
}

public enum ActionKind
{
    Navigate,
    Click,
    Type,
    Select,
    Hover,
    Press,
    Scroll,
    Wait,
    Evaluate,
    Assert,
    Screenshot
}

/// <summary>动作来源。四条路径产出同构的动作,靠这个字段区分。</summary>
public enum ActionOrigin
{
    /// <summary>用户在浏览器里直接操作,由 Page Agent 录制。</summary>
    Human,

    /// <summary>终端 REPL 命令。</summary>
    Repl,

    /// <summary>AI 工具调用。</summary>
    Ai,

    /// <summary>脚本回放。</summary>
    Script
}

public sealed record ActionOptions
{
    public static readonly ActionOptions Default = new();

    /// <summary>整个动作的超时。含等待元素出现的时间。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>元素未就绪时的重试间隔。</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>交互前是否先滚动到可视区。不可见元素点了也没用。</summary>
    public bool ScrollIntoView { get; init; } = true;

    /// <summary>是否要求元素可交互(可见、未禁用、未被遮挡)。</summary>
    public bool RequireInteractable { get; init; } = true;
}

/// <summary>动作执行结果。</summary>
public sealed record ActionResult
{
    public required bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>求值类动作的返回值(JSON)。</summary>
    public string? Value { get; init; }

    /// <summary>实际命中的选择器。候选链里靠后的选择器命中意味着页面结构变了。</summary>
    public string? MatchedSelector { get; init; }

    /// <summary>候选链里第几个命中(0 表示首选)。</summary>
    public int MatchedIndex { get; init; }

    public TimeSpan Elapsed { get; init; }

    public static ActionResult Ok(string? value = null, string? selector = null, int index = 0, TimeSpan elapsed = default) =>
        new() { Success = true, Value = value, MatchedSelector = selector, MatchedIndex = index, Elapsed = elapsed };

    public static ActionResult Fail(string error, TimeSpan elapsed = default) =>
        new() { Success = false, Error = error, Elapsed = elapsed };
}
