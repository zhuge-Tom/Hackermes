using Hookmes.Automation.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.AiPanel.Tools;

/// <summary>Projects the same CommandRegistry used by the REPL into AI tools.</summary>
public sealed class CommandToolAdapter
{
    // 文件路径型命令不直接暴露给模型；它们需经过独立文件工具的路径约束与策略检查。
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "help", "timeline", "save", "load", "packet", "rule", "repeater", "compare" };
    private readonly CommandRegistry _commands;

    public CommandToolAdapter(CommandRegistry commands) => _commands = commands;

    public IReadOnlyList<AiToolDefinition> EnumerateTools() => _commands.All
        .Where(c => !Excluded.Contains(c.Name))
        .Select(CreateDefinition)
        .ToArray();

    public void RegisterAll(IAiToolRegistry registry)
    {
        foreach (var tool in EnumerateTools()) registry.Register(tool);
    }

    private AiToolDefinition CreateDefinition(CommandDefinition command)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { arguments = new { type = "string", description = command.Usage } },
            required = Array.Empty<string>(),
            additionalProperties = false
        });

        return new AiToolDefinition(
            ToToolName(command.Name), command.Summary, schema,
            command.IsMutating || command.Name.Equals("rec", StringComparison.OrdinalIgnoreCase)
                ? AiToolRisk.Mutating : AiToolRisk.ReadOnly,
            (invocation, ct) => ExecuteAsync(command, invocation, ct));
    }

    private async ValueTask<ToolResult> ExecuteAsync(
        CommandDefinition command, ToolInvocation invocation, CancellationToken ct)
    {
        var arguments = invocation.Arguments.ValueKind == JsonValueKind.Object
            && invocation.Arguments.TryGetProperty("arguments", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
        var result = await _commands.ExecuteAsync(
            string.IsNullOrWhiteSpace(arguments) ? command.Name : $"{command.Name} {arguments}",
            invocation.PageId, ct).ConfigureAwait(false);
        return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
    }

    private static string ToToolName(string command) => command switch
    {
        "open" => "page_navigate", "click" => "page_click", "type" => "page_type",
        "hover" => "page_hover", "press" => "page_press", "eval" => "page_eval",
        "wait" => "page_wait", "dom" => "page_query", "snap" => "page_screenshot",
        "rec" => "script_record", "replay" => "script_run", _ => $"page_{command.Replace('-', '_')}"
    };
}
