using Hackermes.Automation.Commands;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Tools;

/// <summary>Projects the same CommandRegistry used by the REPL into AI tools.</summary>
public sealed class CommandToolAdapter
{
    // 文件路径型命令不直接暴露给模型；它们需经过独立文件工具的路径约束与策略检查。
    // annotation / traffic-history / compare-session 已有专用强类型 AI 工具
    // （packet_annotation_*、traffic_history_*、comparison_*），单字符串投影只会诱导参数错误。
    // identity / signing-keys 为 CLI-only 操作者治理命令，不得投影为 page_* 工具。
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "help", "timeline", "save", "load", "packet", "rule", "repeater", "compare", "compare-session",
          "agent", "assessment", "annotation", "traffic-history", "identity", "signing-keys" };
    private readonly CommandRegistry _commands;
    private readonly IBrowserTabOpener? _tabs;

    public CommandToolAdapter(CommandRegistry commands, IBrowserTabOpener? tabs = null)
    {
        _commands = commands;
        _tabs = tabs;
    }

    public IReadOnlyList<AiToolDefinition> EnumerateTools() => _commands.All
        .Where(c => !Excluded.Contains(c.Name))
        .Select(CreateDefinition)
        .ToArray();

    public void RegisterAll(IAiToolRegistry registry)
    {
        foreach (var tool in EnumerateTools()) registry.Register(tool);
        if (registry.TryGet("page_eval", out var eval) && eval is not null)
        {
            registry.Register(new AiToolDefinition(
                "page_eval_read",
                "Evaluate a read-only JavaScript expression for inspection. Do not use it to change page state; use page_eval for writes.",
                eval.InputSchema, AiToolRisk.ReadOnly, eval.Handler));
        }
    }

    private AiToolDefinition CreateDefinition(CommandDefinition command)
    {
        return new AiToolDefinition(
            ToToolName(command.Name), command.Summary, CreateSchema(command),
            command.IsMutating || command.Name.Equals("rec", StringComparison.OrdinalIgnoreCase)
                ? AiToolRisk.Mutating : AiToolRisk.ReadOnly,
            (invocation, ct) => ExecuteAsync(command, invocation, ct));
    }

    private async ValueTask<ToolResult> ExecuteAsync(
        CommandDefinition command, ToolInvocation invocation, CancellationToken ct)
    {
        var arguments = ResolveArguments(command.Name, invocation.Arguments);
        if (command.Name.Equals("open", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(invocation.PageId))
        {
            return await OpenNewTabAsync(arguments, ct).ConfigureAwait(false);
        }

        var result = await _commands.ExecuteAsync(
            string.IsNullOrWhiteSpace(arguments) ? command.Name : $"{command.Name} {arguments}",
            invocation.PageId, ct).ConfigureAwait(false);
        if (!result.Success) return ToolResult.Fail(result.Output);
        if (result.MediaBase64 is { Length: > 0 } data)
            return new ToolResult(true, result.Output, Images: [new ChatImage(result.MediaType ?? "image/png", data)]);
        return ToolResult.Ok(result.Output);
    }

    private async ValueTask<ToolResult> OpenNewTabAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("缺少地址");
        if (_tabs is null)
            return ToolResult.Fail("没有活动的页面,请先新建标签页");

        ct.ThrowIfCancellationRequested();
        string pageId;
        try
        {
            pageId = await OpenTabOnUiThreadAsync(url).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("无法打开页面: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(pageId))
            return ToolResult.Fail("无法打开页面: 未返回标签页 id");

        return new ToolResult(
            true,
            $"已打开新标签页 {pageId} → {url.Trim()}",
            AttachedPageId: pageId);
    }

    private async Task<string> OpenTabOnUiThreadAsync(string url)
    {
        try
        {
            return await UiThreadBridge.InvokeAsync(() => _tabs!.OpenTab(url)).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return _tabs!.OpenTab(url);
        }
    }

    private static string ResolveArguments(string commandName, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            var typed = commandName.ToLowerInvariant() switch
            {
                "open" => JoinPresent(args, "url"),
                "click" or "hover" or "wait" or "dom" => JoinPresent(args, "selector"),
                "type" => JoinPresent(args, "selector", "text"),
                "press" => JoinPresent(args, "key"),
                "eval" => JoinPresent(args, "expression"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(typed))
                return typed;

            if (args.TryGetProperty("arguments", out var value))
                return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? JoinPresent(JsonElement args, params string[] names)
    {
        var parts = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            if (!args.TryGetProperty(names[i], out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            parts[i] = value.GetString() ?? string.Empty;
        }

        return string.Join(' ', parts);
    }

    private static JsonElement CreateSchema(CommandDefinition command)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        var required = new List<string>();
        switch (command.Name.ToLowerInvariant())
        {
            case "open":
                properties["url"] = StringProp("要打开的地址");
                required.Add("url");
                break;
            case "click":
            case "hover":
            case "wait":
                properties["selector"] = StringProp("CSS 选择器");
                required.Add("selector");
                break;
            case "type":
                properties["selector"] = StringProp("CSS 选择器");
                properties["text"] = StringProp("要输入的文本");
                required.Add("selector");
                required.Add("text");
                break;
            case "press":
                properties["key"] = StringProp("按键名，例如 Enter");
                required.Add("key");
                break;
            case "eval":
                properties["expression"] = StringProp("JavaScript 表达式");
                required.Add("expression");
                break;
            case "dom":
                properties["selector"] = StringProp("可选 CSS 选择器");
                break;
        }

        properties["arguments"] = new { type = "string", description = command.Usage };
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            required,
            additionalProperties = false
        });
    }

    private static object StringProp(string description) => new { type = "string", description };

    private static string ToToolName(string command) => command switch
    {
        "open" => "page_navigate", "click" => "page_click", "type" => "page_type",
        "hover" => "page_hover", "press" => "page_press", "eval" => "page_eval",
        "wait" => "page_wait", "dom" => "page_query", "snap" => "page_screenshot",
        "rec" => "script_record", "replay" => "script_run", _ => $"page_{command.Replace('-', '_')}"
    };
}
