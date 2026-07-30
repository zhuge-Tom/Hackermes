using Hookmes.AiPanel.Tools;
using Hookmes.Automation.Commands;
using Hookmes.Traffic.Annotations;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hookmes.App;

/// <summary>Shared CLI and agent adapters for persistent packet analyst annotations.</summary>
internal static class TrafficAnnotationToolRegistrar
{
    public static void Register(CommandRegistry commands, IAiToolRegistry tools, ITrafficAnnotationService annotations)
    {
        commands.Register(new CommandDefinition
        {
            Name = "annotation",
            Summary = "Bookmark, tag and review captured packets",
            Usage = "annotation <list|show|set|delete|prune> ...",
            IsMutating = true,
            Handler = (context, _) => Task.FromResult(ExecuteCommand(annotations, context))
        });

        tools.Register(new AiToolDefinition("packet_annotation_get",
            "Get persistent bookmark, tags, note and review status for one packet.", GetSchema(),
            AiToolRisk.ReadOnly, (invocation, _) => ValueTask.FromResult(Get(annotations, invocation.Arguments))));
        tools.Register(new AiToolDefinition("packet_annotation_list",
            "Query persistent packet annotations by tag, review status, bookmark and text.", ListSchema(),
            AiToolRisk.ReadOnly, (invocation, _) => ValueTask.FromResult(List(annotations, invocation.Arguments))));
        tools.Register(new AiToolDefinition("packet_annotation_set",
            "Persist bookmark, tags, note and review status for one captured packet.", SetSchema(),
            AiToolRisk.Mutating, (invocation, _) => ValueTask.FromResult(Set(annotations, invocation.Arguments))));
        tools.Register(new AiToolDefinition("packet_annotation_delete",
            "Delete persistent analyst annotation for one packet.", GetSchema(),
            AiToolRisk.Mutating, (invocation, _) => ValueTask.FromResult(Delete(annotations, invocation.Arguments))));
        tools.Register(new AiToolDefinition("packet_annotation_prune",
            "Delete annotations whose captured packet no longer exists.", EmptySchema(),
            AiToolRisk.Mutating, (_, _) => ValueTask.FromResult(
                ToolResult.Ok($"Pruned {annotations.PruneMissingPackets()} annotation(s)."))));
    }

    private static CommandResult ExecuteCommand(ITrafficAnnotationService service, CommandContext context)
    {
        try
        {
            return context.Arg(0)?.ToLowerInvariant() switch
            {
                "list" => CommandResult.Ok(Format(service.Query(new TrafficAnnotationQuery(
                    Tag: NullIfKeep(context.Arg(1)),
                    Status: ParseOptionalStatus(context.Arg(2)),
                    Starred: ParseOptionalBool(context.Arg(3))))),
                "show" => CommandResult.Ok(FormatOne(service.Get(Required(context, 1, "packet id")))),
                "set" => SetCommand(service, context),
                "delete" => CommandResult.Ok(service.Delete(Required(context, 1, "packet id")) ? "Annotation deleted." : "No annotation."),
                "prune" => CommandResult.Ok($"Pruned {service.PruneMissingPackets()} annotation(s)."),
                _ => CommandResult.Fail("Usage: annotation <list [tag] [status] [starred]|show <id>|set <id> <starred|keep> <status|keep> <tags|keep> [note]|delete <id>|prune>")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    private static CommandResult SetCommand(ITrafficAnnotationService service, CommandContext context)
    {
        var id = Required(context, 1, "packet id");
        var starred = ParseOptionalBool(context.Arg(2));
        var status = ParseOptionalStatus(context.Arg(3));
        var tagsText = context.Arg(4);
        var tags = NullIfKeep(tagsText) is { } value
            ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null;
        var hasNote = context.Args.Count > 5;
        var note = hasNote ? context.Rest(5) : null;
        var changed = service.Update(id, new TrafficAnnotationUpdate(starred, tags, note, hasNote, status));
        return CommandResult.Ok(FormatOne(changed));
    }

    private static ToolResult Get(ITrafficAnnotationService service, JsonElement args) =>
        ToolResult.Ok(JsonSerializer.Serialize(service.Get(Required(args, "id"))));

    private static ToolResult List(ITrafficAnnotationService service, JsonElement args)
    {
        var status = args.TryGetProperty("status", out var statusElement)
            ? Enum.Parse<TrafficReviewStatus>(statusElement.GetString()!, true) : null;
        var query = new TrafficAnnotationQuery(
            Optional(args, "tag"), status,
            args.TryGetProperty("starred", out var starred) ? starred.GetBoolean() : null,
            Optional(args, "text"));
        return ToolResult.Ok(JsonSerializer.Serialize(service.Query(query)));
    }

    private static ToolResult Set(ITrafficAnnotationService service, JsonElement args)
    {
        var tags = args.TryGetProperty("tags", out var tagsElement)
            ? tagsElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : null;
        var status = args.TryGetProperty("status", out var statusElement)
            ? Enum.Parse<TrafficReviewStatus>(statusElement.GetString()!, true) : null;
        var hasNote = args.TryGetProperty("note", out var noteElement);
        var result = service.Update(Required(args, "id"), new TrafficAnnotationUpdate(
            args.TryGetProperty("starred", out var starred) ? starred.GetBoolean() : null,
            tags, hasNote ? noteElement.GetString() : null, hasNote, status));
        return ToolResult.Ok(JsonSerializer.Serialize(result));
    }

    private static ToolResult Delete(ITrafficAnnotationService service, JsonElement args) =>
        ToolResult.Ok(service.Delete(Required(args, "id")) ? "Annotation deleted." : "No annotation.");

    private static JsonElement GetSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { id = new { type = "string" } },
        required = new[] { "id" }, additionalProperties = false
    });

    private static JsonElement SetSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string" }, starred = new { type = "boolean" },
            tags = new { type = "array", items = new { type = "string" }, maxItems = 32 },
            note = new { type = new[] { "string", "null" } },
            status = new { type = "string", @enum = new[] { "unreviewed", "inReview", "resolved", "ignored" } }
        },
        required = new[] { "id" }, additionalProperties = false
    });

    private static JsonElement ListSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            tag = new { type = "string" }, text = new { type = "string" }, starred = new { type = "boolean" },
            status = new { type = "string", @enum = new[] { "unreviewed", "inReview", "resolved", "ignored" } }
        },
        additionalProperties = false
    });

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { }, additionalProperties = false
    });

    private static string Format(System.Collections.Generic.IReadOnlyList<TrafficAnnotation> values) =>
        values.Count == 0 ? "No annotations." : string.Join(Environment.NewLine, values.Select(FormatOne));

    private static string FormatOne(TrafficAnnotation? value) => value is null ? "No annotation." :
        $"{value.PacketId}\tstarred={value.Starred}\tstatus={value.Status}\ttags={string.Join(',', value.Tags)}\trev={value.Revision}\t{value.Note}";

    private static bool? ParseOptionalBool(string? value) => NullIfKeep(value) is not { } actual ? null :
        bool.TryParse(actual, out var parsed) ? parsed : throw new ArgumentException("Starred must be true, false or keep.");
    private static TrafficReviewStatus? ParseOptionalStatus(string? value) => NullIfKeep(value) is not { } actual ? null :
        Enum.TryParse<TrafficReviewStatus>(actual, true, out var parsed) ? parsed : throw new ArgumentException("Invalid review status.");
    private static string? NullIfKeep(string? value) => string.IsNullOrWhiteSpace(value) || value.Equals("keep", StringComparison.OrdinalIgnoreCase) || value == "-" ? null : value;
    private static string Required(CommandContext context, int index, string name) => context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
    private static string Required(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : throw new ArgumentException($"Missing {name}.");
    private static string? Optional(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
}
