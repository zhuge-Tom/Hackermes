using Hookmes.AiPanel.Tools;
using Hookmes.Automation.Commands;
using Hookmes.Traffic.Comparison;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

internal static class TrafficComparisonToolRegistrar
{
    public static void Register(CommandRegistry commands, IAiToolRegistry tools, ITrafficComparisonService comparisons)
    {
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
    }

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
