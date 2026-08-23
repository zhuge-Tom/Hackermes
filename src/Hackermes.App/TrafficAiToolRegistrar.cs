using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>Agent-facing packet tools with per-operation risk classification.</summary>
internal static class TrafficAiToolRegistrar
{
    private static readonly JsonSerializerOptions CommitJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Register(IAiToolRegistry registry, IPacketCommandService packets)
    {
        RegisterTyped(registry, packets, "packet_list", "List captured HTTP packets. Values are not returned.",
            AiToolRisk.ReadOnly, "filter", false, a => new PacketListIntent(OptionalValue(a, "filter")));
        if (packets is IPacketQueryService) RegisterPacketQueryTool(registry, packets);
        RegisterTyped(registry, packets, "packet_show", "Show a captured HTTP request or response. Sensitive header values are redacted.",
            AiToolRisk.ReadOnly, "id", true, a => new PacketShowIntent(Required(a, "id"), Optional(a, "side", "request")), redact: true);
        RegisterAnalysisTool(registry, packets);
        RegisterTyped(registry, packets, "packet_diff", "Compare two captured HTTP packets semantically.",
            AiToolRisk.ReadOnly, "leftId", true, a => new PacketDiffIntent(Required(a, "leftId"), Required(a, "rightId"), Optional(a, "side", "request")));
        if (packets is IPacketAuditQueryService)
            RegisterTyped(registry, packets, "packet_audit", "Query bounded metadata-only traffic operation audit entries.",
                AiToolRisk.ReadOnly, "packetId", false, a => new PacketAuditIntent(Optional(a, "packetId", "*"),
                    a.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 100));
        if (packets is IPacketAuditExportService auditExports) RegisterAuditExportTools(registry, auditExports);
        RegisterTyped(registry, packets, "packet_parameters", "List structured query, form and top-level JSON parameters. Sensitive values are redacted.",
            AiToolRisk.ReadOnly, "id", true, a => new PacketParameterListIntent(Required(a, "id"), Optional(a, "side", "request")), redact: true);
        RegisterParameterSetTool(registry, packets);
        RegisterTyped(registry, packets, "packet_replay", "Replay a captured HTTP request in its browser session.",
            AiToolRisk.Mutating, "id", true, a => new PacketReplayIntent(Required(a, "id")));
        RegisterTyped(registry, packets, "packet_intercept", "Enable or disable holding browser requests for inspection.",
            AiToolRisk.Mutating, "enabled", true, a => new PacketInterceptionIntent(a.GetProperty("enabled").GetBoolean()));
        if (packets is IPacketInterceptionModeService)
            RegisterTyped(registry, packets, "packet_intercept_mode", "Set independent request/response interception: request, response, both or off.",
                AiToolRisk.Mutating, "mode", true, a => new PacketInterceptionModeIntent(ParseInterceptionMode(Required(a, "mode"))));
        if (packets is IPacketCommitService)
            RegisterCommitTools(registry, packets);
        else
        {
            RegisterTyped(registry, packets, "packet_continue", "Continue a held HTTP request without edits.",
                AiToolRisk.Mutating, "id", true, a => new PacketCommitIntent(PacketCommitAction.Continue, Required(a, "id")));
            RegisterTyped(registry, packets, "packet_drop", "Drop a held HTTP request.",
                AiToolRisk.Dangerous, "id", true, a => new PacketCommitIntent(PacketCommitAction.Drop, Required(a, "id")));
            RegisterTyped(registry, packets, "packet_edit", "Replace and continue a held request, or fulfill it with an edited response.",
                AiToolRisk.Dangerous, "id", true, a => new PacketCommitIntent(PacketCommitAction.Edit, Required(a, "id"),
                    Required(a, "side"), Required(a, "rawHttp")));
        }
        if (packets is IPacketEditDraftService)
        {
            RegisterTyped(registry, packets, "packet_edit_drafts", "List pending binary edits with before/after length, SHA-256, Content-Length and last commit failure.",
                AiToolRisk.ReadOnly, "id", false, _ => new PacketDraftListIntent());
            RegisterTyped(registry, packets, "packet_edit_draft", "Inspect one pending binary edit and its latest commit failure.",
                AiToolRisk.ReadOnly, "id", true, a => new PacketDraftShowIntent(Required(a, "id"), Optional(a, "side", "request")));
            if (packets is not IPacketCommitService)
                RegisterTyped(registry, packets, "packet_edit_discard", "Discard a pending binary edit and restore its original body and headers.",
                    AiToolRisk.Mutating, "id", true, a => new PacketCommitIntent(PacketCommitAction.Discard, Required(a, "id"),
                        Optional(a, "side", "request")));
        }
        if (packets is IPacketBodyReadService bodies) RegisterBodyTools(registry, bodies);
        if (packets is IPacketBodyEditService editor) RegisterBodyEditTool(registry, editor);
        if (packets is IPacketArchiveService archive) RegisterArchiveTools(registry, archive);
    }

    private static void RegisterCommitTools(IAiToolRegistry registry, IPacketCommandService packets)
    {
        RegisterCommitTool(registry, "packet_continue", "Continue a held HTTP request without edits.",
            AiToolRisk.Mutating, false, packets, args => new PacketCommitIntent(PacketCommitAction.Continue, Required(args, "id")));
        RegisterCommitTool(registry, "packet_drop", "Drop a held HTTP request.",
            AiToolRisk.Dangerous, false, packets, args => new PacketCommitIntent(PacketCommitAction.Drop, Required(args, "id")));
        RegisterCommitTool(registry, "packet_edit", "Replace and continue a held request, or fulfill it with an edited response.",
            AiToolRisk.Dangerous, true, packets, args => new PacketCommitIntent(PacketCommitAction.Edit, Required(args, "id"),
                Required(args, "side"), Required(args, "rawHttp")));
        RegisterCommitTool(registry, "packet_edit_discard", "Discard a pending binary edit and restore its original body and headers.",
            AiToolRisk.Mutating, false, packets, args => new PacketCommitIntent(PacketCommitAction.Discard, Required(args, "id"),
                Optional(args, "side", "request")));
    }

    private static void RegisterPacketQueryTool(IAiToolRegistry registry, IPacketCommandService packets)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                text = new { type = "string", maxLength = 512 },
                method = new { type = "string", maxLength = 32 },
                statusCode = new { type = "integer", minimum = 100, maximum = 999 },
                resourceType = new { type = "string", maxLength = 64 },
                onlyIntercepted = new { type = "boolean" },
                offset = new { type = "integer", minimum = 0, description = "Page start index." },
                limit = new { type = "integer", minimum = 1, maximum = PacketQueryLimits.MaximumPageSize, description = "Page size; advance offset to walk the full result set." }
            },
            additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_query",
            "Query captured HTTP packet metadata with compound filters and bounded pagination. Packet values are not returned.",
            schema, AiToolRisk.ReadOnly, async (invocation, ct) =>
            {
                try
                {
                    var args = invocation.Arguments;
                    var query = new PacketQuery(
                        OptionalValue(args, "text"), OptionalValue(args, "method"), OptionalInt(args, "statusCode"),
                        OptionalValue(args, "resourceType"), OptionalBool(args, "onlyIntercepted"),
                        OptionalInt(args, "offset") ?? 0, OptionalInt(args, "limit") ?? 100);
                    var outcome = await PacketOperationExecutor.ExecuteAsync(packets, new PacketQueryIntent(query), ct).ConfigureAwait(false);
                    return outcome switch
                    {
                        PacketQueryOutcome page => ToolResult.Ok(JsonSerializer.Serialize(page.Page, CommitJsonOptions)),
                        PacketOperationFailure failure => ToolResult.Fail(failure.Error),
                        _ => ToolResult.Fail("Unsupported packet operation outcome.")
                    };
                }
                catch (ArgumentException exception)
                {
                    return ToolResult.Fail(exception.Message);
                }
            }));
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
        AiToolRisk risk, bool rawHttp, IPacketCommandService packets, Func<JsonElement, PacketCommitIntent> intent)
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
            var outcome = await PacketOperationExecutor.ExecuteAsync(packets, intent(invocation.Arguments), ct).ConfigureAwait(false);
            if (outcome is PacketOperationFailure failure) return ToolResult.Fail(failure.Error);
            if (outcome is not PacketCommitOutcome commit) return ToolResult.Fail("Unsupported packet operation outcome.");
            var json = JsonSerializer.Serialize(commit.Result, CommitJsonOptions);
            return commit.Result.Success ? ToolResult.Ok(json) : ToolResult.Fail(json);
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
                format = new { type = "string", @enum = new[] { "hackermesJson", "har" } },
                filter = new { type = "string", maxLength = 512, description = "Substring matched against packet URL or method." },
                offset = new { type = "integer", minimum = 0, description = "Entry index of the first packet in this batch." },
                limit = new { type = "integer", minimum = 1, maximum = PacketArchiveContent.MaximumEntries, description = "Maximum packets in this batch; walk offset until you have collected total entries." }
            }, required = new[] { "format" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_archive_export",
            $"Export filtered packets as bounded JSON/HAR content in batches of up to {PacketArchiveContent.MaximumEntries} " +
            $"entries (offset/limit paging; response carries total so you can fetch further batches until you have all of them). " +
            "No filesystem path is accepted. Bulk packet data may contain secrets and requires explicit confirmation.",
            exportSchema, AiToolRisk.Dangerous, async (invocation, ct) =>
            {
                try
                {
                    var args = invocation.Arguments;
                    var formatText = Required(args, "format");
                    var format = PacketArchiveContent.ParseFormat(formatText);
                    var offset = args.TryGetProperty("offset", out var rawOffset) ? rawOffset.GetInt32() : 0;
                    var limit = args.TryGetProperty("limit", out var rawLimit)
                        ? rawLimit.GetInt32() : PacketArchiveContent.MaximumEntries;
                    var page = await archive.ExportArchivePageAsync(
                        new PacketArchiveExchangeQuery(OptionalValue(args, "filter"), offset, limit), ct).ConfigureAwait(false);
                    var content = PacketArchiveContent.Serialize(page.Entries, format);
                    return ToolResult.Ok(JsonSerializer.Serialize(
                        new { format = formatText, count = page.Entries.Count, total = page.Total, offset, content },
                        CommitJsonOptions));
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
                format = new { type = "string", @enum = new[] { "hackermesJson", "har" } },
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
                location = new { type = "string", @enum = new[] { "query", "form", "json", "header", "cookie", "multipart" } },
                name = new { type = "string", minLength = 1, maxLength = HttpPacketParameters.MaximumNameLength },
                occurrence = new { type = "integer", minimum = 0 },
                value = new { type = "string", maxLength = HttpPacketParameters.MaximumValueLength }
            }, required = new[] { "id", "side", "location", "name", "occurrence", "value" }, additionalProperties = false
        });
        registry.Register(new AiToolDefinition("packet_parameter_set",
            "Set one structured parameter occurrence and submit the held packet. Requires confirmation.",
            schema, AiToolRisk.Dangerous, async (invocation, ct) =>
            {
                try
                {
                    var args = invocation.Arguments;
                    if (!Enum.TryParse<HttpParameterLocation>(Required(args, "location"), true, out var location))
                        return ToolResult.Fail("Parameter location must be query, form, json, header, cookie or multipart.");
                    var outcome = await PacketOperationExecutor.ExecuteAsync(packets, new PacketParameterSetIntent(
                        Required(args, "id"), Required(args, "side"), location, Required(args, "name"),
                        args.GetProperty("occurrence").GetInt32(), Required(args, "value")), ct).ConfigureAwait(false);
                    var result = PacketCommandRegistrar.FormatOutcome(outcome);
                    return result.Success ? ToolResult.Ok(result.Output) : ToolResult.Fail(result.Output);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                                  KeyNotFoundException or HttpPacketParseException)
                {
                    return ToolResult.Fail(exception.Message);
                }
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
                offset = new { type = "integer", minimum = 0, description = "Byte offset to read from." },
                count = new { type = "integer", minimum = 1, maximum = PacketBodyChunker.MaximumChunkSize, description = "Bytes to load in this chunk; walk offsets for large bodies." },
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

    private static void RegisterTyped(IAiToolRegistry registry, IPacketCommandService packets, string name,
        string description, AiToolRisk risk, string primary, bool required,
        Func<JsonElement, PacketOperationIntent> intent, bool redact = false)
    {
        var schema = CreateSchema(name, primary, required);
        registry.Register(new AiToolDefinition(name, description, schema, risk, async (invocation, ct) =>
        {
            var outcome = await PacketOperationExecutor.ExecuteAsync(packets, intent(invocation.Arguments), ct).ConfigureAwait(false);
            var result = PacketCommandRegistrar.FormatOutcome(outcome);
            var output = redact ? Redact(result.Output) : result.Output;
            return result.Success ? ToolResult.Ok(output) : ToolResult.Fail(output);
        }));
    }

    private static JsonElement CreateSchema(string name, string primary, bool required) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string" }, leftId = new { type = "string" }, rightId = new { type = "string" },
                side = new { type = "string", @enum = new[] { "request", "response" } },
                filter = new { type = "string" }, enabled = new { type = "boolean" }, rawHttp = new { type = "string" },
                location = new { type = "string", @enum = new[] { "query", "form", "json", "header", "cookie", "multipart" } },
                name = new { type = "string" }, occurrence = new { type = "integer", minimum = 0 }, value = new { type = "string" },
                mode = new { type = "string", @enum = new[] { "request", "response", "both", "off" } },
                packetId = new { type = "string" },
                limit = new { type = "integer", minimum = 1, maximum = 500, description = "Maximum audit entries returned." }
            },
            required = required ? RequiredFields(name, primary) : Array.Empty<string>(),
            additionalProperties = false
        });

    private static string[] RequiredFields(string name, string primary) => name switch
    {
        "packet_diff" => ["leftId", "rightId"],
        "packet_edit" => ["id", "side", "rawHttp"],
        "packet_parameter_set" => ["id", "side", "location", "name", "occurrence", "value"],
        _ => [primary]
    };

    private static string Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : property.GetRawText() : throw new ArgumentException($"Missing {name}.");
    private static string Optional(JsonElement value, string name, string fallback = "") =>
        value.TryGetProperty(name, out var property) ? property.GetString() ?? fallback : fallback;
    private static string? OptionalValue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() : null;
    private static int? OptionalInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetInt32() : null;
    private static bool OptionalBool(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.GetBoolean();
    private static PacketInterceptionMode ParseInterceptionMode(string value) => value.ToLowerInvariant() switch
    {
        "request" => PacketInterceptionMode.Request,
        "response" => PacketInterceptionMode.Response,
        "both" => PacketInterceptionMode.Both,
        "off" => PacketInterceptionMode.Off,
        _ => throw new ArgumentException("Mode must be request, response, both or off.")
    };

    private static string Redact(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var columns = lines[i].Split('\t');
            if (columns.Length >= 3 &&
                (columns[0].StartsWith("cookie[", StringComparison.OrdinalIgnoreCase) || IsSensitiveName(columns[1])))
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
        name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
}
