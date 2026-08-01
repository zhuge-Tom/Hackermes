using Hookmes.AiPanel.Tools;
using Hookmes.Automation.Commands;
using Hookmes.Automation.Packet;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

/// <summary>Agent-facing packet tools with per-operation risk classification.</summary>
internal static class TrafficAiToolRegistrar
{
    private static readonly JsonSerializerOptions CommitJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Register(IAiToolRegistry registry, IPacketCommandService packets)
    {
        Register(registry, packets, "packet_list", "List captured HTTP packets. Values are not returned.",
            AiToolRisk.ReadOnly, "filter", false, a => Args("ls", Optional(a, "filter")));
        Register(registry, packets, "packet_show", "Show a captured HTTP request or response. Sensitive header values are redacted.",
            AiToolRisk.ReadOnly, "id", true, a => Args("show", Required(a, "id"), Optional(a, "side", "request")));
        RegisterAnalysisTool(registry, packets);
        Register(registry, packets, "packet_diff", "Compare two captured HTTP packets semantically.",
            AiToolRisk.ReadOnly, "leftId", true, a => Args("diff", Required(a, "leftId"), Required(a, "rightId"), Optional(a, "side", "request")));
        if (packets is IPacketAuditQueryService)
            Register(registry, packets, "packet_audit", "Query bounded metadata-only traffic operation audit entries.",
                AiToolRisk.ReadOnly, "packetId", false, a => Args("audit", Optional(a, "packetId", "*"),
                    a.TryGetProperty("limit", out var limit) ? limit.GetRawText() : "100"));
        if (packets is IPacketAuditExportService auditExports) RegisterAuditExportTools(registry, auditExports);
        Register(registry, packets, "packet_parameters", "List structured query, form and top-level JSON parameters. Sensitive values are redacted.",
            AiToolRisk.ReadOnly, "id", true, a => Args("param-list", Required(a, "id"), Optional(a, "side", "request")));
        RegisterParameterSetTool(registry, packets);
        Register(registry, packets, "packet_replay", "Replay a captured HTTP request in its browser session.",
            AiToolRisk.Mutating, "id", true, a => Args("replay", Required(a, "id")));
        Register(registry, packets, "packet_intercept", "Enable or disable holding browser requests for inspection.",
            AiToolRisk.Mutating, "enabled", true, a => Args("intercept", Required(a, "enabled") == "true" ? "on" : "off"));
        if (packets is IPacketInterceptionModeService)
            Register(registry, packets, "packet_intercept_mode", "Set independent request/response interception: request, response, both or off.",
                AiToolRisk.Mutating, "mode", true, a => Args("intercept-mode", Required(a, "mode")));
        if (packets is IPacketCommitService commits)
            RegisterCommitTools(registry, commits);
        else
        {
            Register(registry, packets, "packet_continue", "Continue a held HTTP request without edits.",
                AiToolRisk.Mutating, "id", true, a => Args("continue", Required(a, "id")));
            Register(registry, packets, "packet_drop", "Drop a held HTTP request.",
                AiToolRisk.Dangerous, "id", true, a => Args("drop", Required(a, "id")));
            Register(registry, packets, "packet_edit", "Replace and continue a held request, or fulfill it with an edited response.",
                AiToolRisk.Dangerous, "id", true, a => Args("edit", Required(a, "id"), Required(a, "side"), EscapeRaw(Required(a, "rawHttp"))));
        }
        if (packets is IPacketEditDraftService)
        {
            Register(registry, packets, "packet_edit_drafts", "List pending binary edits with before/after length, SHA-256, Content-Length and last commit failure.",
                AiToolRisk.ReadOnly, "id", false, _ => "draft-list");
            Register(registry, packets, "packet_edit_draft", "Inspect one pending binary edit and its latest commit failure.",
                AiToolRisk.ReadOnly, "id", true, a => Args("draft-show", Required(a, "id"), Optional(a, "side", "request")));
            if (packets is not IPacketCommitService)
                Register(registry, packets, "packet_edit_discard", "Discard a pending binary edit and restore its original body and headers.",
                    AiToolRisk.Mutating, "id", true, a => Args("draft-discard", Required(a, "id"), Optional(a, "side", "request")));
        }
        if (packets is IPacketBodyReadService bodies) RegisterBodyTools(registry, bodies);
        if (packets is IPacketBodyEditService editor) RegisterBodyEditTool(registry, editor);
        if (packets is IPacketArchiveService archive) RegisterArchiveTools(registry, archive);
    }

    private static void RegisterCommitTools(IAiToolRegistry registry, IPacketCommitService commits)
    {
        RegisterCommitTool(registry, "packet_continue", "Continue a held HTTP request without edits.",
            AiToolRisk.Mutating, false, (args, ct) => commits.CommitContinueAsync(Required(args, "id"), ct));
        RegisterCommitTool(registry, "packet_drop", "Drop a held HTTP request.",
            AiToolRisk.Dangerous, false, (args, ct) => commits.CommitDropAsync(Required(args, "id"), ct));
        RegisterCommitTool(registry, "packet_edit", "Replace and continue a held request, or fulfill it with an edited response.",
            AiToolRisk.Dangerous, true, (args, ct) => commits.CommitEditAsync(Required(args, "id"),
                Required(args, "side"), Required(args, "rawHttp"), ct));
        RegisterCommitTool(registry, "packet_edit_discard", "Discard a pending binary edit and restore its original body and headers.",
            AiToolRisk.Mutating, false, (args, ct) => commits.CommitDiscardAsync(Required(args, "id"),
                Optional(args, "side", "request"), ct));
    }

    private static void RegisterAuditExportTools(IAiToolRegistry registry, IPacketAuditExportService exports)
    {
        var exportSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                packetId = new { type = "string", maxLength = 256 },
                limit = new { type = "integer", minimum = 1, maximum = PacketAuditExportService.MaximumEntries }
            },
            additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_audit_export",
            $"Return signed metadata-only audit JSON (maximum {PacketAuditExportService.MaximumEntries} entries); no filesystem path is accepted.",
            exportSchema, AiToolRisk.Dangerous, (invocation, _) =>
            {
                try
                {
                    var args = invocation.Arguments;
                    var packetId = Optional(args, "packetId", null!);
                    if (packetId?.Length > 256) return ValueTask.FromResult(ToolResult.Fail("Packet id must not exceed 256 characters."));
                    var limit = args.TryGetProperty("limit", out var rawLimit) ? rawLimit.GetInt32() : 100;
                    if (limit is < 1 or > PacketAuditExportService.MaximumEntries)
                        return ValueTask.FromResult(ToolResult.Fail($"Audit limit must be between 1 and {PacketAuditExportService.MaximumEntries}."));
                    return ValueTask.FromResult(ToolResult.Ok(exports.Export(new PacketAuditQuery(packetId, Limit: limit))));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
                {
                    return ValueTask.FromResult(ToolResult.Fail(ex.Message));
                }
            }));

        var verifySchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                content = new { type = "string", maxLength = PacketAuditExportService.MaximumContentBytes },
                expectedKeyId = new { type = "string", maxLength = 128 }
            },
            required = new[] { "content" },
            additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_audit_verify",
            "Verify bounded signed audit JSON content without reading a filesystem path.",
            verifySchema, AiToolRisk.ReadOnly, (invocation, _) =>
            {
                var args = invocation.Arguments;
                var content = Required(args, "content");
                var expectedKeyId = Optional(args, "expectedKeyId", null!);
                if (Encoding.UTF8.GetByteCount(content) > PacketAuditExportService.MaximumContentBytes)
                    return ValueTask.FromResult(ToolResult.Fail($"Audit content exceeds {PacketAuditExportService.MaximumContentBytes} UTF-8 bytes."));
                if (expectedKeyId?.Length > 128) return ValueTask.FromResult(ToolResult.Fail("Expected key id must not exceed 128 characters."));
                var result = exports.Verify(content, expectedKeyId);
                var json = JsonSerializer.Serialize(result, CommitJsonOptions);
                return ValueTask.FromResult(result.Valid ? ToolResult.Ok(json) : ToolResult.Fail(json));
            }));
    }

    private static void RegisterCommitTool(IAiToolRegistry registry, string name, string description,
        AiToolRisk risk, bool rawHttp, Func<JsonElement, CancellationToken, Task<PacketCommitResult>> commit)
    {
        var schema = rawHttp
            ? JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { id = new { type = "string" }, side = new { type = "string", @enum = new[] { "request", "response" } }, rawHttp = new { type = "string" } },
                required = new[] { "id", "side", "rawHttp" }, additionalProperties = false
            })
            : name == "packet_edit_discard"
                ? JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { id = new { type = "string" }, side = new { type = "string", @enum = new[] { "request", "response" } } },
                    required = new[] { "id" }, additionalProperties = false
                })
                : JsonSerializer.SerializeToElement(new
                {
                    type = "object", properties = new { id = new { type = "string" } },
                    required = new[] { "id" }, additionalProperties = false
                });
        registry.Register(new AiToolDefinition(name, description, schema, risk, async (invocation, ct) =>
        {
            var result = await commit(invocation.Arguments, ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, CommitJsonOptions);
            return result.Success ? ToolResult.Ok(json) : ToolResult.Fail(json);
        }));
    }

    private static void RegisterAnalysisTool(IAiToolRegistry registry, IPacketCommandService packets)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                id = new { type = "string" },
                side = new { type = "string", @enum = new[] { "request", "response" } }
            }, required = new[] { "id" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_analyze",
            "Return structured protocol and sensitive-data findings with stable codes and edit locations.",
            schema, AiToolRisk.ReadOnly, async (invocation, ct) =>
            {
                var id = Required(invocation.Arguments, "id");
                var side = Optional(invocation.Arguments, "side", "request");
                var raw = await packets.GetRawAsync(id, side, ct).ConfigureAwait(false);
                if (raw is null) return ToolResult.Fail($"Packet '{id}' has no {side} data.");
                var analysis = HttpPacketAnalyzer.Analyze(HttpPacketCodec.Parse(raw));
                return ToolResult.Ok(JsonSerializer.Serialize(new
                {
                    findings = analysis.Findings.Select(finding => new
                    {
                        severity = finding.Severity.ToString(), code = finding.Code, message = finding.Message,
                        side = finding.Side.ToString(), locationKind = finding.LocationKind.ToString(),
                        field = finding.Field, headerName = finding.HeaderName, headerOccurrence = finding.HeaderOccurrence,
                        bodyOffset = finding.BodyOffset, bodyLength = finding.BodyLength
                    }),
                    sensitiveFields = analysis.SensitiveFields
                }));
            }));
    }

    private static void RegisterArchiveTools(IAiToolRegistry registry, IPacketArchiveService archive)
    {
        var exportSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                format = new { type = "string", @enum = new[] { "hookmesJson", "har" } },
                filter = new { type = "string" }
            }, required = new[] { "format" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_archive_export",
            $"Export up to {PacketArchiveContent.MaximumEntries} filtered packets as bounded JSON/HAR content. " +
            "No filesystem path is accepted. Bulk packet data may contain secrets and requires explicit confirmation.",
            exportSchema, AiToolRisk.Dangerous, async (invocation, ct) =>
            {
                try
                {
                    var args = invocation.Arguments;
                    var formatText = Required(args, "format");
                    var entries = await archive.ExportArchiveAsync(Optional(args, "filter", null!), ct).ConfigureAwait(false);
                    var content = PacketArchiveContent.Serialize(entries, PacketArchiveContent.ParseFormat(formatText));
                    return ToolResult.Ok(JsonSerializer.Serialize(new { format = formatText, count = entries.Count, content }));
                }
                catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException)
                {
                    return ToolResult.Fail(ex.Message);
                }
            }));

        var importSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                format = new { type = "string", @enum = new[] { "hookmesJson", "har" } },
                content = new { type = "string", maxLength = PacketArchiveContent.MaximumUtf8Bytes }
            }, required = new[] { "format", "content" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_archive_import",
            $"Import bounded JSON/HAR content into packet history (maximum {PacketArchiveContent.MaximumEntries} entries). " +
            "No filesystem path is accepted.", importSchema, AiToolRisk.Mutating, async (invocation, ct) =>
            {
                try
                {
                    var args = invocation.Arguments;
                    var entries = PacketArchiveContent.Deserialize(Required(args, "content"),
                        PacketArchiveContent.ParseFormat(Required(args, "format")));
                    var count = await archive.ImportArchiveAsync(entries, ct).ConfigureAwait(false);
                    return ToolResult.Ok($"Imported {count} packet(s) from bounded archive content.");
                }
                catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException or JsonException)
                {
                    return ToolResult.Fail(ex.Message);
                }
            }));
    }

    private static void RegisterParameterSetTool(IAiToolRegistry registry, IPacketCommandService packets)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                id = new { type = "string" }, side = new { type = "string", @enum = new[] { "request", "response" } },
                location = new { type = "string", @enum = new[] { "query", "form", "json" } },
                name = new { type = "string" }, occurrence = new { type = "integer", minimum = 0 }, value = new { type = "string" }
            }, required = new[] { "id", "side", "location", "name", "occurrence", "value" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_parameter_set",
            "Set one structured parameter occurrence and submit the held packet. Requires confirmation.",
            schema, AiToolRisk.Dangerous, async (invocation, ct) =>
            {
                var args = invocation.Arguments;
                var id = Required(args, "id");
                var side = Required(args, "side");
                if (side is not ("request" or "response"))
                    return ToolResult.Fail("Side must be request or response.");
                var raw = await packets.GetRawAsync(id, side, ct).ConfigureAwait(false);
                if (raw is null) return ToolResult.Fail($"Packet '{id}' has no {side}.");
                if (!Enum.TryParse<HttpParameterLocation>(Required(args, "location"), true, out var location))
                    return ToolResult.Fail("Parameter location must be query, form or json.");
                var updated = HttpPacketParameters.Set(HttpPacketCodec.Parse(raw), location,
                    Required(args, "name"), args.GetProperty("occurrence").GetInt32(), Required(args, "value"));
                await packets.EditAsync(id, side, HttpPacketCodec.Format(updated, false), ct).ConfigureAwait(false);
                return ToolResult.Ok("Parameter updated and packet submitted.");
            }));
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
                name = new { type = "string" }, occurrence = new { type = "integer", minimum = 0 }, value = new { type = "string" },
                mode = new { type = "string", @enum = new[] { "request", "response", "both", "off" } },
                packetId = new { type = "string" }, limit = new { type = "integer", minimum = 1, maximum = 500 }
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
