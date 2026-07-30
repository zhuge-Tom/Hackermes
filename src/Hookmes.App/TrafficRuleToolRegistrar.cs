using Hookmes.AiPanel.Tools;
using Hookmes.Automation.Commands;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Rules;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

internal static class TrafficRuleToolRegistrar
{
    public static void Register(CommandRegistry commands, IAiToolRegistry tools, ITrafficRuleManager rules)
    {
        commands.Register(new CommandDefinition
        {
            Name = "rule", Summary = "Manage persistent traffic interception and mock rules",
            Usage = "rule <ls|add|enable|disable|remove|move|export|import> ...", IsMutating = true,
            Handler = (context, ct) => ExecuteAsync(rules, context, ct)
        });

        tools.Register(new AiToolDefinition("traffic_rule_list", "List persistent traffic interception rules.",
            Schema(new { }), AiToolRisk.ReadOnly, (invocation, ct) =>
                WrapAsync(rules, Context("ls"), ct)));
        tools.Register(new AiToolDefinition("traffic_rule_change",
            "Add, enable, disable, remove, or reorder a persistent traffic rule. Mutations require confirmation.",
            Schema(new
            {
                action = new { type = "string", @enum = new[] { "add", "enable", "disable", "remove", "move" } },
                id = new { type = "string" }, urlPattern = new { type = "string" }, method = new { type = "string" },
                stage = new { type = "string", @enum = new[] { "request", "response" } },
                behavior = new { type = "string", @enum = new[] { "pause", "drop" } }, index = new { type = "integer" }
            }, ["action", "id"]), AiToolRisk.Mutating, (invocation, ct) =>
                WrapAsync(rules, Context(BuildAgentArgs(invocation.Arguments)), ct)));
    }

    private static async Task<CommandResult> ExecuteAsync(ITrafficRuleManager rules, CommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return context.Arg(0)?.ToLowerInvariant() switch
            {
                "ls" => CommandResult.Ok(FormatRules(rules)),
                "add" => Add(rules, context),
                "enable" => Toggle(rules, Required(context, 1, "id"), true),
                "disable" => Toggle(rules, Required(context, 1, "id"), false),
                "remove" => CommandResult.Ok(rules.Remove(Required(context, 1, "id")) ? "Rule removed." : "Rule did not exist."),
                "move" => Move(rules, Required(context, 1, "id"), int.Parse(Required(context, 2, "index"))),
                "export" => Export(rules, Required(context, 1, "path")),
                "import" => Import(rules, Required(context, 1, "path"), context.Arg(2)),
                _ => CommandResult.Fail("Usage: rule <ls|add|enable|disable|remove|move|export|import> ...")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or FormatException)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    private static CommandResult Add(ITrafficRuleManager rules, CommandContext context)
    {
        var id = Required(context, 1, "id");
        var pattern = Required(context, 2, "url pattern");
        var method = NormalizeOptional(context.Arg(3));
        var stage = context.Arg(4)?.Equals("response", StringComparison.OrdinalIgnoreCase) == true
            ? TrafficStage.Response : TrafficStage.Request;
        var behavior = context.Arg(5)?.ToLowerInvariant() ?? "pause";
        rules.Add(new TrafficRule(id, pattern, method, stage, Fail: behavior == "drop", Pause: behavior != "drop"));
        return CommandResult.Ok($"Rule '{id}' added.");
    }

    private static CommandResult Toggle(ITrafficRuleManager rules, string id, bool enabled)
    {
        rules.SetEnabled(id, enabled);
        return CommandResult.Ok($"Rule '{id}' {(enabled ? "enabled" : "disabled")}.");
    }

    private static CommandResult Move(ITrafficRuleManager rules, string id, int index)
    {
        rules.Move(id, index); return CommandResult.Ok($"Rule '{id}' moved to {index}.");
    }

    private static CommandResult Export(ITrafficRuleManager rules, string path)
    {
        File.WriteAllText(path, rules.ExportJson()); return CommandResult.Ok($"Rules exported to {Path.GetFullPath(path)}.");
    }

    private static CommandResult Import(ITrafficRuleManager rules, string path, string? mode)
    {
        rules.ImportJson(File.ReadAllText(path), mode?.Equals("merge", StringComparison.OrdinalIgnoreCase) == true
            ? TrafficRuleImportMode.Merge : TrafficRuleImportMode.Replace);
        return CommandResult.Ok($"Rules imported from {Path.GetFullPath(path)}.");
    }

    private static string FormatRules(ITrafficRuleManager rules)
    {
        var items = rules.GetAll();
        return items.Count == 0 ? "No traffic rules." : string.Join(Environment.NewLine, items.Select((r, i) =>
            $"{i}\t{r.Id}\t{(r.Enabled ? "on" : "off")}\t{r.Stage?.ToString().ToLowerInvariant() ?? "any"}\t{r.Method ?? "*"}\t{r.UrlPattern}\t{(r.Fail ? "drop" : r.Pause ? "pause" : "edit")}"));
    }

    private static async ValueTask<ToolResult> WrapAsync(ITrafficRuleManager rules, CommandContext context, CancellationToken ct)
    {
        var result = await ExecuteAsync(rules, context, ct).ConfigureAwait(false);
        return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
    }

    private static string BuildAgentArgs(JsonElement args)
    {
        var action = Get(args, "action"); var id = Get(args, "id");
        return action switch
        {
            "add" => $"add {id} {Get(args, "urlPattern", "*")} {Get(args, "method", "*")} {Get(args, "stage", "request")} {Get(args, "behavior", "pause")}",
            "move" => $"move {id} {Get(args, "index", "0")}",
            _ => $"{action} {id}"
        };
    }

    private static CommandContext Context(string arguments) => new()
    {
        Args = CommandLineParser.Tokenize(arguments), PageId = null, RawInput = "rule " + arguments, RawArguments = arguments
    };
    private static JsonElement Schema(object properties, string[]? required = null) => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties, required = required ?? Array.Empty<string>(), additionalProperties = false
    });
    private static string Required(CommandContext context, int index, string name) => context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) || value == "*" ? null : value;
    private static string Get(JsonElement value, string name, string fallback = "") => value.TryGetProperty(name, out var p) ? p.ToString() : fallback;
}
