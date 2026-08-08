using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Traffic;
using Hackermes.Traffic.Comparison;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

internal static class TrafficComparisonToolRegistrar
{
    public static void Register(CommandRegistry commands, IAiToolRegistry tools, ITrafficComparisonService comparisons)
    {
        ComparisonSessionCommandRegistrar.Register(commands, comparisons);
        commands.Register(new CommandDefinition
        {
            Name = "compare", Summary = "Compare captured HTTP packets without corrupting binary bodies",
            Usage = "compare <left-packet-id> <right-packet-id> [request|response]", IsMutating = false,
            Handler = (context, ct) => CompareAsync(comparisons, context, ct)
        });
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                leftPacketId = new { type = "string" }, rightPacketId = new { type = "string" },
                side = new { type = "string", @enum = new[] { "request", "response" } }
            }, required = new[] { "leftPacketId", "rightPacketId" }, additionalProperties = false
        });
        tools.Register(new AiToolDefinition("packet_compare_structured",
            "Compare packet start lines, duplicate headers and binary-safe body hashes.", schema, AiToolRisk.ReadOnly,
            async (invocation, ct) =>
            {
                var args = invocation.Arguments;
                var context = Context($"{Get(args, "leftPacketId")} {Get(args, "rightPacketId")} {Get(args, "side", "request")}");
                var result = await CompareAsync(comparisons, context, ct).ConfigureAwait(false);
                return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
            }));
        RegisterSessionTools(tools, comparisons);
    }

    private static void RegisterSessionTools(IAiToolRegistry tools, ITrafficComparisonService comparisons)
    {
        tools.Register(new AiToolDefinition("comparison_session_list", "List persistent named comparison sessions.",
            SessionSchema(new { }), AiToolRisk.ReadOnly,
            (invocation, ct) => ExecuteSessionTool(comparisons, "list", ct)));
        tools.Register(new AiToolDefinition("comparison_session_create",
            "Save a named comparison. Sources use traffic-request:<packetId>, traffic-response:<packetId>, repeater-request:<draftId>:<sendId>, or repeater-response:<draftId>:<sendId>.",
            SessionSchema(new { left = new { type = "string" }, right = new { type = "string" }, name = new { type = "string" } }, ["left", "right", "name"]),
            AiToolRisk.Mutating, (invocation, ct) => ExecuteSessionTool(comparisons,
                $"create {Get(invocation.Arguments, "left")} {Get(invocation.Arguments, "right")} {Get(invocation.Arguments, "name")}", ct)));
        tools.Register(new AiToolDefinition("comparison_session_rename", "Rename a persistent comparison session.",
            SessionSchema(new { id = new { type = "string" }, name = new { type = "string" } }, ["id", "name"]),
            AiToolRisk.Mutating, (invocation, ct) => ExecuteSessionTool(comparisons,
                $"rename {Get(invocation.Arguments, "id")} {Get(invocation.Arguments, "name")}", ct)));
        tools.Register(new AiToolDefinition("comparison_session_recalculate", "Recalculate a saved session from its current packet or Repeater sources.",
            SessionSchema(new { id = new { type = "string" } }, ["id"]), AiToolRisk.Mutating,
            (invocation, ct) => ExecuteSessionTool(comparisons, $"recalculate {Get(invocation.Arguments, "id")}", ct)));
        tools.Register(new AiToolDefinition("comparison_session_delete", "Permanently delete a saved comparison session.",
            SessionSchema(new { id = new { type = "string" } }, ["id"]), AiToolRisk.Dangerous,
            (invocation, ct) => ExecuteSessionTool(comparisons, $"delete {Get(invocation.Arguments, "id")}", ct)));
    }

    private static async ValueTask<ToolResult> ExecuteSessionTool(
        ITrafficComparisonService comparisons, string arguments, CancellationToken ct)
    {
        var result = await ComparisonSessionCommandRegistrar.ExecuteAsync(comparisons, Context(arguments), ct).ConfigureAwait(false);
        return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
    }

    private static JsonElement SessionSchema(object properties, string[]? required = null) => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties, required = required ?? Array.Empty<string>(), additionalProperties = false
    });

    private static Task<CommandResult> CompareAsync(ITrafficComparisonService comparisons, CommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var kind = context.Arg(2)?.Equals("response", StringComparison.OrdinalIgnoreCase) == true
                ? ComparisonSourceKind.TrafficResponse : ComparisonSourceKind.TrafficRequest;
            var result = comparisons.Compare(
                new ComparisonSource(kind, PacketId: Required(context, 0, "left packet id")),
                new ComparisonSource(kind, PacketId: Required(context, 1, "right packet id")));
            return Task.FromResult(CommandResult.Ok(TrafficComparisonAdapter.Format(result)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            return Task.FromResult(CommandResult.Fail(ex.Message));
        }
    }

    private static CommandContext Context(string args) => new()
    {
        Args = CommandLineParser.Tokenize(args), PageId = null, RawInput = "compare " + args, RawArguments = args
    };
    private static string Required(CommandContext context, int index, string name) => context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
    private static string Get(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
}
