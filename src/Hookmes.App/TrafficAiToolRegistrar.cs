using Hookmes.AiPanel.Tools;
using Hookmes.Automation.Commands;
using Hookmes.Automation.Packet;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

/// <summary>Agent-facing packet tools with per-operation risk classification.</summary>
internal static class TrafficAiToolRegistrar
{
    public static void Register(IAiToolRegistry registry, IPacketCommandService packets)
    {
        Register(registry, packets, "packet_list", "List captured HTTP packets. Values are not returned.",
            AiToolRisk.ReadOnly, "filter", false, a => Args("ls", Optional(a, "filter")));
        Register(registry, packets, "packet_show", "Show a captured HTTP request or response. Sensitive header values are redacted.",
            AiToolRisk.ReadOnly, "id", true, a => Args("show", Required(a, "id"), Optional(a, "side", "request")));
        Register(registry, packets, "packet_analyze", "Analyze an HTTP packet for protocol anomalies and sensitive fields.",
            AiToolRisk.ReadOnly, "id", true, a => Args("analyze", Required(a, "id"), Optional(a, "side", "request")));
        Register(registry, packets, "packet_diff", "Compare two captured HTTP packets semantically.",
            AiToolRisk.ReadOnly, "leftId", true, a => Args("diff", Required(a, "leftId"), Required(a, "rightId"), Optional(a, "side", "request")));
        Register(registry, packets, "packet_parameters", "List structured query, form and top-level JSON parameters. Sensitive values are redacted.",
            AiToolRisk.ReadOnly, "id", true, a => Args("param-list", Required(a, "id"), Optional(a, "side", "request")));
        Register(registry, packets, "packet_parameter_set", "Set one structured parameter occurrence and submit the held packet. Requires confirmation.",
            AiToolRisk.Dangerous, "id", true, a => Args("param-set", Required(a, "id"), Required(a, "side"),
                Required(a, "location"), Required(a, "name"), Required(a, "occurrence"), Required(a, "value")));
        Register(registry, packets, "packet_replay", "Replay a captured HTTP request in its browser session.",
            AiToolRisk.Mutating, "id", true, a => Args("replay", Required(a, "id")));
        Register(registry, packets, "packet_intercept", "Enable or disable holding browser requests for inspection.",
            AiToolRisk.Mutating, "enabled", true, a => Args("intercept", Required(a, "enabled") == "true" ? "on" : "off"));
        Register(registry, packets, "packet_continue", "Continue a held HTTP request without edits.",
            AiToolRisk.Mutating, "id", true, a => Args("continue", Required(a, "id")));
        Register(registry, packets, "packet_drop", "Drop a held HTTP request.",
            AiToolRisk.Dangerous, "id", true, a => Args("drop", Required(a, "id")));
        Register(registry, packets, "packet_edit", "Replace and continue a held request, or fulfill it with an edited response.",
            AiToolRisk.Dangerous, "id", true, a => Args("edit", Required(a, "id"), Required(a, "side"), EscapeRaw(Required(a, "rawHttp"))));
        if (packets is IPacketBodyReadService bodies) RegisterBodyTools(registry, bodies);
        if (packets is IPacketBodyEditService editor) RegisterBodyEditTool(registry, editor);
    }

    private static void RegisterBodyEditTool(IAiToolRegistry registry, IPacketBodyEditService editor)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                id = new { type = "string" }, side = new { type = "string", @enum = new[] { "request", "response" } },
                kind = new { type = "string", @enum = new[] { "replace", "insert", "delete" } },
                offset = new { type = "integer", minimum = 0 }, count = new { type = "integer", minimum = 0 },
                data = new { type = "string" }, encoding = new { type = "string", @enum = new[] { "hex", "base64" } }
            }, required = new[] { "id", "side", "kind", "offset" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_body_edit",
            "Apply a bounded hex/base64 replace, insert or delete to a captured body. Requires confirmation.",
            schema, AiToolRisk.Dangerous, async (invocation, ct) =>
            {
                var args = invocation.Arguments;
                var kind = Enum.Parse<BinaryEditKind>(Required(args, "kind"), true);
                var encoding = args.TryGetProperty("encoding", out var encodingElement) && encodingElement.GetString() == "base64"
                    ? BinaryTextEncoding.Base64 : BinaryTextEncoding.Hex;
                var edit = new BinaryBodyEdit(kind, args.GetProperty("offset").GetInt64(),
                    args.TryGetProperty("count", out var count) ? count.GetInt64() : 0,
                    args.TryGetProperty("data", out var data) ? data.GetString() : null, encoding);
                var result = await editor.EditBodyAsync(Required(args, "id"), Required(args, "side"), edit, ct).ConfigureAwait(false);
                return ToolResult.Ok(JsonSerializer.Serialize(result));
            }));
    }

    private static void RegisterBodyTools(IAiToolRegistry registry, IPacketBodyReadService bodies)
    {
        var infoSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                id = new { type = "string" }, side = new { type = "string", @enum = new[] { "request", "response" } }
            }, required = new[] { "id", "side" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_body_info",
            "Get packet body byte length, SHA-256 and content metadata without returning the body.", infoSchema,
            AiToolRisk.ReadOnly, async (invocation, ct) =>
            {
                var result = await bodies.DescribeBodyAsync(Required(invocation.Arguments, "id"),
                    Required(invocation.Arguments, "side"), ct).ConfigureAwait(false);
                return ToolResult.Ok(JsonSerializer.Serialize(result));
            }));

        var chunkSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                id = new { type = "string" }, side = new { type = "string", @enum = new[] { "request", "response" } },
                offset = new { type = "integer", minimum = 0 }, count = new { type = "integer", minimum = 1, maximum = PacketBodyChunker.MaximumChunkSize },
                encoding = new { type = "string", @enum = new[] { "base64", "safeText" } }
            }, required = new[] { "id", "side", "offset" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_body_chunk",
            "Read one bounded packet body byte range. Unsafe text automatically falls back to base64.", chunkSchema,
            AiToolRisk.ReadOnly, async (invocation, ct) =>
            {
                var args = invocation.Arguments;
                var offset = args.GetProperty("offset").GetInt64();
                var count = args.TryGetProperty("count", out var countElement) ? countElement.GetInt32() : PacketBodyChunker.DefaultChunkSize;
                var encoding = args.TryGetProperty("encoding", out var encodingElement) && encodingElement.GetString() == "safeText"
                    ? PacketBodyChunkEncoding.SafeText : PacketBodyChunkEncoding.Base64;
                var result = await bodies.ReadBodyChunkAsync(Required(args, "id"), Required(args, "side"), offset, count, encoding, ct)
                    .ConfigureAwait(false);
                return ToolResult.Ok(JsonSerializer.Serialize(result));
            }));
    }

    private static void Register(IAiToolRegistry registry, IPacketCommandService packets, string name,
        string description, AiToolRisk risk, string primary, bool required,
        Func<JsonElement, string> arguments)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string" }, leftId = new { type = "string" }, rightId = new { type = "string" },
                side = new { type = "string", @enum = new[] { "request", "response" } },
                filter = new { type = "string" }, enabled = new { type = "boolean" }, rawHttp = new { type = "string" },
                location = new { type = "string", @enum = new[] { "query", "form", "json" } },
                name = new { type = "string" }, occurrence = new { type = "integer", minimum = 0 }, value = new { type = "string" }
            },
            required = required ? RequiredFields(name, primary) : Array.Empty<string>(),
            additionalProperties = false
        });
        registry.Register(new AiToolDefinition(name, description, schema, risk,
            async (invocation, ct) => await ExecuteAsync(packets, arguments(invocation.Arguments), name == "packet_show", ct)));
    }

    private static string[] RequiredFields(string name, string primary) => name switch
    {
        "packet_diff" => ["leftId", "rightId"],
        "packet_edit" => ["id", "side", "rawHttp"],
        "packet_parameter_set" => ["id", "side", "location", "name", "occurrence", "value"],
        _ => [primary]
    };

    private static async ValueTask<ToolResult> ExecuteAsync(
        IPacketCommandService packets, string args, bool redact, CancellationToken ct)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var context = new CommandContext
        {
            Args = tokens,
            PageId = null,
            RawInput = "packet " + args,
            RawArguments = args
        };
        var result = await PacketCommandRegistrar.ExecuteAsync(packets, context, ct).ConfigureAwait(false);
        var output = redact || args.StartsWith("param-list ", StringComparison.Ordinal) ? Redact(result.Output) : result.Output;
        return result.Success ? ToolResult.Ok(output) : ToolResult.Fail(output);
    }

    private static string Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : property.GetRawText() : throw new ArgumentException($"Missing {name}.");
    private static string Optional(JsonElement value, string name, string fallback = "") =>
        value.TryGetProperty(name, out var property) ? property.GetString() ?? fallback : fallback;
    private static string Args(params string[] values) => string.Join(' ', values);
    private static string EscapeRaw(string raw) => raw.Replace("\r\n", "\\r\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Redact(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var columns = lines[i].Split('\t');
            if (columns.Length >= 3 && IsSensitiveName(columns[1]))
            {
                columns[2] = "<redacted>";
                lines[i] = string.Join('\t', columns);
                continue;
            }
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            var name = lines[i][..separator].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase))
                lines[i] = lines[i][..(separator + 1)] + " <redacted>\r";
        }
        return string.Join('\n', lines);
    }

    private static bool IsSensitiveName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
}
