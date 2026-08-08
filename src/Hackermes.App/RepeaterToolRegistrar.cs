using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using Hackermes.Traffic.Repeater;
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

internal static class RepeaterToolRegistrar
{
    public static void Register(CommandRegistry commands, IAiToolRegistry tools, IRepeaterService repeater)
    {
        commands.Register(new CommandDefinition
        {
            Name = "repeater", Summary = "Manage named request drafts and multi-send history",
            Usage = "repeater <ls|create|send|rename|delete|clear> ...; send <id> [timeoutSeconds]", IsMutating = true,
            Handler = (context, ct) => ExecuteAsync(repeater, context, ct)
        });

        tools.Register(new AiToolDefinition("repeater_list", "List Repeater drafts and latest send metrics.",
            Schema(new { }), AiToolRisk.ReadOnly,
            (invocation, ct) => WrapAsync(repeater, Context("ls"), ct)));
        tools.Register(new AiToolDefinition("repeater_create", "Create a Repeater draft from a captured packet.",
            Schema(new { packetId = new { type = "string" }, name = new { type = "string" } }, ["packetId"]),
            AiToolRisk.Mutating, (invocation, ct) => WrapAsync(repeater,
                Context($"create {Get(invocation.Arguments, "packetId")} {Get(invocation.Arguments, "name")}"), ct)));
        tools.Register(new AiToolDefinition("repeater_send", "Send a Repeater draft and return an explicit Completed, Failed, Cancelled, or TimedOut result.",
            Schema(new
            {
                id = new { type = "string" },
                timeoutSeconds = new { type = "number", minimum = 0.1, maximum = 600 }
            }, ["id"]), AiToolRisk.Mutating,
            (invocation, ct) => WrapAsync(repeater,
                Context($"send {Get(invocation.Arguments, "id")} {GetNumber(invocation.Arguments, "timeoutSeconds")}"), ct)));
        tools.Register(new AiToolDefinition("repeater_rename", "Rename a persistent Repeater draft.",
            Schema(new { id = new { type = "string" }, name = new { type = "string" } }, ["id", "name"]),
            AiToolRisk.Mutating, (invocation, ct) => WrapAsync(repeater,
                Context($"rename {Get(invocation.Arguments, "id")} {Get(invocation.Arguments, "name")}"), ct)));
        tools.Register(new AiToolDefinition("repeater_clear_history", "Clear all recorded send rounds for a Repeater draft.",
            Schema(new { id = new { type = "string" } }, ["id"]), AiToolRisk.Dangerous,
            (invocation, ct) => WrapAsync(repeater, Context($"clear {Get(invocation.Arguments, "id")}"), ct)));
        tools.Register(new AiToolDefinition("repeater_delete", "Delete a Repeater draft and its history.",
            Schema(new { id = new { type = "string" } }, ["id"]), AiToolRisk.Dangerous,
            (invocation, ct) => WrapAsync(repeater, Context($"delete {Get(invocation.Arguments, "id")}"), ct)));
    }

    private static async Task<CommandResult> ExecuteAsync(IRepeaterService repeater, CommandContext context, CancellationToken ct)
    {
        try
        {
            return context.Arg(0)?.ToLowerInvariant() switch
            {
                "ls" => CommandResult.Ok(List(repeater)),
                "create" => CommandResult.Ok($"Draft created: {repeater.CreateFromPacket(Required(context, 1, "packet id"), context.Rest(2)).Id}"),
                "send" => CommandResult.Ok(FormatSend(await repeater.SendAsync(
                    Required(context, 1, "draft id"), ParseSendOptions(context.Arg(2)), ct))),
                "rename" => CommandResult.Ok($"Draft renamed: {repeater.Rename(Required(context, 1, "draft id"), context.Rest(2)).Name}"),
                "delete" => CommandResult.Ok(repeater.Delete(Required(context, 1, "draft id")) ? "Draft deleted." : "Draft did not exist."),
                "clear" => Clear(repeater, Required(context, 1, "draft id")),
                _ => CommandResult.Fail("Usage: repeater <ls|create|send|rename|delete|clear> ...; send <id> [timeoutSeconds]")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    private static string List(IRepeaterService repeater)
    {
        var drafts = repeater.GetAll();
        return drafts.Count == 0 ? "No Repeater drafts." : string.Join(Environment.NewLine, drafts.Select(d =>
        {
            var latest = d.History.LastOrDefault();
            return $"{d.Id}\t{d.Name}\trev={d.Revision}\tsends={d.History.Count}\t" +
                   (latest is null ? "draft" : $"{latest.Status} {latest.ResponseStatus} {latest.DurationMilliseconds}ms");
        }));
    }

    private static string FormatSend(RepeaterSendResult result) =>
        $"{result.Status}: HTTP {result.ResponseStatus?.ToString() ?? "-"} · {result.DurationMilliseconds} ms · " +
        $"{result.RequestSize} B -> {result.ResponseSize} B" + (result.Error is null ? "" : $" · {result.Error}");

    private static CommandResult Clear(IRepeaterService repeater, string id)
    {
        repeater.ClearHistory(id); return CommandResult.Ok("Repeater history cleared.");
    }

    private static RepeaterSendOptions ParseSendOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return RepeaterSendOptions.Default;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds < RepeaterSendOptions.MinimumTimeout.TotalSeconds ||
            seconds > RepeaterSendOptions.MaximumTimeout.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Timeout must be between {RepeaterSendOptions.MinimumTimeout.TotalSeconds:0.###} and {RepeaterSendOptions.MaximumTimeout.TotalSeconds:0.###} seconds.");
        return new RepeaterSendOptions(TimeSpan.FromSeconds(seconds));
    }

    private static async ValueTask<ToolResult> WrapAsync(
        IRepeaterService repeater, CommandContext context, CancellationToken ct)
    {
        var result = await ExecuteAsync(repeater, context, ct).ConfigureAwait(false);
        return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
    }

    private static CommandContext Context(string arguments) => new()
    {
        Args = CommandLineParser.Tokenize(arguments), PageId = null,
        RawInput = "repeater " + arguments, RawArguments = arguments
    };
    private static JsonElement Schema(object properties, string[]? required = null) => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties, required = required ?? Array.Empty<string>(), additionalProperties = false
    });
    private static string Required(CommandContext context, int index, string name) =>
        context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
    private static string Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string GetNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : string.Empty;
}
