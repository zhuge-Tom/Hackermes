using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Commands;

/// <summary>
/// 一条领域命令。
/// <para>
/// 命令定义是<strong>单一真相源</strong>:终端 REPL 与 AI 工具都是它的前端。
/// 这样"人能敲的"和"AI 能调的"永远是同一组能力,不会出现两边行为不一致。
/// </para>
/// </summary>
public sealed record CommandDefinition
{
    public required string Name { get; init; }

    /// <summary>一行摘要,用于 help 列表。</summary>
    public required string Summary { get; init; }

    /// <summary>用法示例,例如 <c>type &lt;选择器&gt; &lt;文本&gt;</c>。</summary>
    public required string Usage { get; init; }

    public required Func<CommandContext, CancellationToken, Task<CommandResult>> Handler { get; init; }

    /// <summary>别名,例如 <c>go</c> 之于 <c>open</c>。</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>是否会改变页面状态。只读命令在 AI 策略闸门里可直接放行。</summary>
    public bool IsMutating { get; init; }
}

/// <summary>命令执行上下文。</summary>
public sealed class CommandContext
{
    public required IReadOnlyList<string> Args { get; init; }

    /// <summary>当前作用的页面。为空表示没有活动标签页。</summary>
    public required string? PageId { get; init; }

    /// <summary>原始输入行,含命令名。</summary>
    public required string RawInput { get; init; }

    /// <summary>
    /// 命令名之后的原始文本,<strong>未经分词</strong>。
    /// <para>
    /// JS 表达式与含引号的选择器必须用它:分词器会把 <c>'btn-result'</c> 的单引号
    /// 当成参数包裹符剥掉,`eval document.getElementById('x')` 就变成了
    /// `document.getElementById(x)`,直接 ReferenceError。
    /// </para>
    /// </summary>
    public required string RawArguments { get; init; }

    public string? Arg(int index) => index < Args.Count ? Args[index] : null;

    /// <summary>从第 index 个参数起的剩余部分,按原样拼回。</summary>
    // 不能用 string.Join(char, array, start, count):那个重载只接受 string[],
    // 而 Args 是 IReadOnlyList<string>,会静默退化成 params object[] 重载,
    // 拼出 "System.String[] 0 1" 这种东西。
    public string Rest(int index) =>
        index >= Args.Count ? string.Empty : string.Join(' ', Args.Skip(index));
}

public sealed record CommandResult(bool Success, string Output, string? MediaType = null, string? MediaBase64 = null)
{
    public static CommandResult Ok(string output = "") => new(true, output);

    public static CommandResult Fail(string message) => new(false, message);
}
