using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Traffic;
using Hackermes.Traffic.History;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

internal static class TrafficHistoryToolRegistrar
{
    public static void Register(CommandRegistry commands, IAiToolRegistry tools, ITrafficHistoryManagementService history)
    {
        HistoryManagementCommandRegistrar.Register(commands, history);
        Register(tools, history, "traffic_history_stats", "Inspect traffic history storage and retention statistics.", AiToolRisk.ReadOnly,
            new { }, _ => "stats");
        Register(tools, history, "traffic_history_policy", "Read the current traffic history retention policy.", AiToolRisk.ReadOnly,
            new { }, _ => "policy");
        Register(tools, history, "traffic_history_preview", "Preview entries and bytes removed by the current policy.", AiToolRisk.ReadOnly,
            new { }, _ => "preview");
        Register(tools, history, "traffic_history_policy_set", "Persist and immediately apply a bounded traffic history policy.", AiToolRisk.Mutating,
            new { maxEntries = new { type = "integer" }, maxBytes = new { type = "integer" }, retentionDays = new { type = "integer" }, autoPrune = new { type = "boolean" } },
            args => $"set {Raw(args, "maxEntries")} {Raw(args, "maxBytes")} {Raw(args, "retentionDays")} {Raw(args, "autoPrune")}",
            ["maxEntries", "maxBytes", "retentionDays", "autoPrune"]);
        Register(tools, history, "traffic_history_site_quota_set", "Set and apply a bounded retention quota for one exact host or wildcard domain.", AiToolRisk.Mutating,
            new { hostPattern = new { type = "string" }, maxEntries = new { type = "integer" }, maxBytes = new { type = "integer" } },
            args => $"site-set {Quote(args, "hostPattern")} {Raw(args, "maxEntries")} {Raw(args, "maxBytes")}",
            ["hostPattern", "maxEntries", "maxBytes"]);
        Register(tools, history, "traffic_history_site_quota_remove", "Remove one host-specific traffic history quota.", AiToolRisk.Mutating,
            new { hostPattern = new { type = "string" } }, args => $"site-remove {Quote(args, "hostPattern")}", ["hostPattern"]);
        Register(tools, history, "traffic_history_cleanup", "Apply the current retention policy and flush persistent history.", AiToolRisk.Dangerous,
            new { }, _ => "cleanup");
        Register(tools, history, "traffic_history_clear", "Permanently clear all captured traffic history.", AiToolRisk.Dangerous,
            new { }, _ => "clear");
    }

    private static void Register(IAiToolRegistry tools, ITrafficHistoryManagementService history,
        string name, string description, AiToolRisk risk, object properties,
        Func<JsonElement, string> arguments, string[]? required = null)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties, required = required ?? Array.Empty<string>(), additionalProperties = false
        });
        tools.Register(new AiToolDefinition(name, description, schema, risk,
            async (invocation, ct) => await ExecuteAsync(history, arguments(invocation.Arguments), ct).ConfigureAwait(false)));
    }

    private static async ValueTask<ToolResult> ExecuteAsync(
        ITrafficHistoryManagementService history, string arguments, CancellationToken cancellationToken)
    {
        var context = new CommandContext
        {
            Args = CommandLineParser.Tokenize(arguments), PageId = null,
            RawInput = HistoryManagementCommandRegistrar.CommandName + " " + arguments,
            RawArguments = arguments
        };
        var result = await HistoryManagementCommandRegistrar.ExecuteAsync(history, context, cancellationToken).ConfigureAwait(false);
        return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
    }

    private static string Raw(JsonElement element, string name) => element.GetProperty(name).GetRawText();
    private static string Quote(JsonElement element, string name) => JsonSerializer.Serialize(element.GetProperty(name).GetString() ?? string.Empty);
}
