using Hackermes.Automation.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Packet;

public sealed record PacketSummary(string Id, string Method, string Url, int? StatusCode, bool Intercepted);

/// <summary>Narrow adapter implemented by the traffic subsystem; keeps CLI and capture backends independent.</summary>
public interface IPacketCommandService
{
    Task<IReadOnlyList<PacketSummary>> ListAsync(string? filter, CancellationToken cancellationToken);
    Task<string?> GetRawAsync(string id, string side, CancellationToken cancellationToken);
    Task ReplayAsync(string id, CancellationToken cancellationToken);
    Task SetInterceptionAsync(bool enabled, CancellationToken cancellationToken);
    Task ContinueAsync(string id, CancellationToken cancellationToken);
    Task DropAsync(string id, CancellationToken cancellationToken);
    Task EditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken);
}

public enum PacketInterceptionMode { Off, Request, Response, Both }

/// <summary>Optional independent request/response interception control.</summary>
public interface IPacketInterceptionModeService
{
    PacketInterceptionMode InterceptionMode { get; }
    Task SetInterceptionModeAsync(PacketInterceptionMode mode, CancellationToken cancellationToken);
}

public static class PacketCommandRegistrar
{
    public static void Register(CommandRegistry registry, IPacketCommandService service) => registry.Register(new CommandDefinition
    {
        Name = "packet",
        Summary = "Inspect, analyze, edit and replay captured HTTP packets",
        Usage = "packet <ls|query|show|analyze|diff|audit|audit-export|audit-verify|param-list|param-set|body-info|body-read|body-edit|draft-list|draft-show|draft-discard|replay|intercept|intercept-mode|continue|drop|edit|export|import> ...",
        IsMutating = true, // policy must gate the mutating subcommands; callers may expose read-only wrappers to AI.
        Handler = (context, cancellationToken) => ExecuteAsync(service, context, cancellationToken)
    });

    public static async Task<CommandResult> ExecuteAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        var action = context.Arg(0)?.ToLowerInvariant();
        try
        {
            return action switch
            {
                "ls" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketListIntent(context.Arg(1)), ct)),
                "query" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service, ParseQuery(context), ct)),
                "show" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketShowIntent(Require(context, 1, "id"), context.Arg(2) ?? "request"), ct)),
                "analyze" => await AnalyzeAsync(service, Require(context, 1, "id"), context.Arg(2) ?? "request", ct),
                "diff" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketDiffIntent(Require(context, 1, "left id"), Require(context, 2, "right id"), context.Arg(3) ?? "request"), ct)),
                "audit" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service, ParseAudit(context), ct)),
                "audit-export" => await AuditExportAsync(service, context, ct),
                "audit-verify" => await AuditVerifyAsync(service, context, ct),
                "param-list" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketParameterListIntent(Require(context, 1, "id"), context.Arg(2) ?? "request"), ct)),
                "param-set" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service, ParseParameterSet(context), ct)),
                "body-info" => await BodyInfoAsync(service, context, ct),
                "body-read" => await BodyReadAsync(service, context, ct),
                "body-edit" => await BodyEditAsync(service, context, ct),
                "draft-list" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service, new PacketDraftListIntent(), ct)),
                "draft-show" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketDraftShowIntent(Require(context, 1, "id"), context.Arg(2) ?? "request"), ct)),
                "draft-discard" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketCommitIntent(PacketCommitAction.Discard, Require(context, 1, "id"), context.Arg(2) ?? "request"), ct)),
                "replay" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketReplayIntent(Require(context, 1, "id")), ct)),
                "intercept" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    ParseInterception(Require(context, 1, "on|off")), ct)),
                "intercept-mode" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketInterceptionModeIntent(ParseInterceptionMode(Require(context, 1, "request|response|both|off"))), ct)),
                "continue" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketCommitIntent(PacketCommitAction.Continue, Require(context, 1, "id")), ct)),
                "drop" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service,
                    new PacketCommitIntent(PacketCommitAction.Drop, Require(context, 1, "id")), ct)),
                "edit" => FormatOutcome(await PacketOperationExecutor.ExecuteAsync(service, ParseEdit(context), ct)),
                "export" => await ExportAsync(service, context, ct),
                "import" => await ImportAsync(service, context, ct),
                _ => CommandResult.Fail("Usage: packet <ls|query|show|analyze|diff|audit|audit-export|audit-verify|param-list|param-set|body-info|body-read|body-edit|draft-list|draft-show|draft-discard|replay|intercept|intercept-mode|continue|drop|edit|export|import> ...")
            };
        }
        catch (ArgumentException exception) { return CommandResult.Fail(exception.Message); }
        catch (KeyNotFoundException exception) { return CommandResult.Fail(exception.Message); }
        catch (HttpPacketParseException exception) { return CommandResult.Fail($"Invalid packet: {exception.Message}"); }
        catch (InvalidDataException exception) { return CommandResult.Fail($"Invalid archive: {exception.Message}"); }
        catch (IOException exception) { return CommandResult.Fail($"Archive I/O failed: {exception.Message}"); }
    }

    private static PacketQueryIntent ParseQuery(CommandContext context) => new(new PacketQuery(
        Wildcard(context.Arg(1)), Wildcard(context.Arg(2)), ParseOptionalInt(context.Arg(3), "status"),
        Wildcard(context.Arg(4)), ParseHeld(context.Arg(5)), ParseInt(context.Arg(6), "offset", 0),
        ParseInt(context.Arg(7), "limit", 100)));

    private static string? Wildcard(string? value) => value is null or "*" ? null : value;
    private static int? ParseOptionalInt(string? value, string name) => value is null or "*" ? null : ParseInt(value, name, 0);
    private static int ParseInt(string? value, string name, int fallback) => value is null ? fallback :
        int.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"{name} must be an integer.");
    private static bool ParseHeld(string? value) => value?.ToLowerInvariant() switch
    {
        null or "*" or "all" => false,
        "held" => true,
        _ => throw new ArgumentException("state must be held or all.")
    };

    private static async Task<CommandResult> AnalyzeAsync(IPacketCommandService service, string id, string side, CancellationToken ct)
    {
        var analysis = HttpPacketAnalyzer.Analyze(HttpPacketCodec.Parse(await GetRequiredAsync(service, id, side, ct)));
        var lines = analysis.Findings.Select(f =>
            $"[{f.Severity}] {f.Code}: {f.Message}" +
            $" (side={f.Side}; kind={f.LocationKind}" +
            (f.HeaderName is null ? "" : $"; header={f.HeaderName}[{f.HeaderOccurrence ?? 0}]") +
            (f.BodyOffset is null ? "" : $"; body={f.BodyOffset}+{f.BodyLength ?? 0}") +
            (f.Field is null ? "" : $"; field={f.Field}") + ")").ToList();
        if (lines.Count == 0) lines.Add("No built-in findings.");
        if (analysis.SensitiveFields.Count > 0) lines.Add("Sensitive: " + string.Join(", ", analysis.SensitiveFields));
        return CommandResult.Ok(string.Join(Environment.NewLine, lines));
    }

    private static PacketInterceptionIntent ParseInterception(string value)
    {
        if (!bool.TryParse(value.Replace("on", "true", StringComparison.OrdinalIgnoreCase)
                .Replace("off", "false", StringComparison.OrdinalIgnoreCase), out var enabled))
            throw new ArgumentException("Usage: packet intercept <on|off>");
        return new PacketInterceptionIntent(enabled);
    }

    private static PacketInterceptionMode ParseInterceptionMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "request" => PacketInterceptionMode.Request,
        "response" => PacketInterceptionMode.Response,
        "both" => PacketInterceptionMode.Both,
        "off" => PacketInterceptionMode.Off,
        _ => throw new ArgumentException("Usage: packet intercept-mode <request|response|both|off>")
    };

    private static PacketCommitIntent ParseEdit(CommandContext context)
    {
        var id = Require(context, 1, "id"); var side = Require(context, 2, "request|response");
        if (context.Args.Count < 4) throw new ArgumentException("Usage: packet edit <id> <request|response> <raw-http>");
        var raw = context.Rest(3).Replace("\\r\\n", "\r\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);
        _ = HttpPacketCodec.Parse(raw); // never persist malformed edits
        return new PacketCommitIntent(PacketCommitAction.Edit, id, side, raw);
    }

    private static async Task<CommandResult> ExportAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketArchiveService archive) return CommandResult.Fail("This packet backend does not support archives.");
        var path = Require(context, 1, "path (.json or .har)");
        var entries = await archive.ExportArchiveAsync(context.Arg(2), ct);
        await File.WriteAllTextAsync(path, PacketArchiveCodec.Serialize(entries, PacketArchiveCodec.DetectFormat(path)), ct);
        return CommandResult.Ok($"Exported {entries.Count} packet(s) to {Path.GetFullPath(path)}.");
    }

    private static async Task<CommandResult> ImportAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketArchiveService archive) return CommandResult.Fail("This packet backend does not support archives.");
        var path = Require(context, 1, "path (.json or .har)");
        var entries = PacketArchiveCodec.Deserialize(await File.ReadAllTextAsync(path, ct), PacketArchiveCodec.DetectFormat(path));
        var imported = await archive.ImportArchiveAsync(entries, ct);
        return CommandResult.Ok($"Imported {imported} packet(s) from {Path.GetFullPath(path)}.");
    }

    private static async Task<CommandResult> BodyInfoAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketBodyReadService bodies) return CommandResult.Fail("This packet backend does not support ranged body reads.");
        var result = await bodies.DescribeBodyAsync(Require(context, 1, "id"), context.Arg(2) ?? "request", ct);
        return CommandResult.Ok($"length={result.Length}\tsha256={result.Sha256}\tcontent-type={result.ContentType ?? "-"}\tcharset={result.Charset ?? "-"}");
    }

    private static PacketAuditIntent ParseAudit(CommandContext context)
    {
        var packetId = context.Arg(1);
        var limit = 100;
        if (context.Arg(2) is { } rawLimit && (!int.TryParse(rawLimit, out limit) || limit <= 0))
            throw new ArgumentException("Audit limit must be a positive integer.");
        return new PacketAuditIntent(packetId, limit);
    }

    private static async Task<CommandResult> AuditExportAsync(
        IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketAuditExportService exports)
            return CommandResult.Fail("This packet backend does not support signed audit exports.");
        var path = Require(context, 1, "path");
        var packetId = context.Arg(2);
        if (packetId == "*") packetId = null;
        if (packetId is { Length: > 256 }) return CommandResult.Fail("Packet id must not exceed 256 characters.");
        if (!TryAuditLimit(context.Arg(3), out var limit, out var error)) return CommandResult.Fail(error!);
        var content = exports.Export(new PacketAuditQuery(packetId, Limit: limit));
        await File.WriteAllTextAsync(path, content, ct);
        return CommandResult.Ok($"Exported signed audit document to {Path.GetFullPath(path)}.");
    }

    private static async Task<CommandResult> AuditVerifyAsync(
        IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketAuditExportService exports)
            return CommandResult.Fail("This packet backend does not support signed audit verification.");
        var path = Require(context, 1, "path");
        var expectedKeyId = context.Arg(2);
        if (expectedKeyId is { Length: > 128 }) return CommandResult.Fail("Expected key id must not exceed 128 characters.");
        if (new FileInfo(path).Length > PacketAuditExportService.MaximumContentBytes)
            return CommandResult.Fail($"Audit document exceeds {PacketAuditExportService.MaximumContentBytes} bytes.");
        var verification = exports.Verify(await File.ReadAllTextAsync(path, ct), expectedKeyId);
        var output = $"valid={verification.Valid.ToString().ToLowerInvariant()}\tkeyId={verification.KeyId ?? "-"}\t" +
            $"entries={verification.EntryCount}\texportedAt={verification.ExportedAt?.ToString("O") ?? "-"}\terror={verification.ErrorCode ?? "-"}";
        return verification.Valid ? CommandResult.Ok(output) : CommandResult.Fail(output);
    }

    private static bool TryAuditLimit(string? value, out int limit, out string? error)
    {
        limit = 100;
        error = null;
        if (value is null) return true;
        if (!int.TryParse(value, out limit) || limit is < 1 or > PacketAuditExportService.MaximumEntries)
        {
            error = $"Audit limit must be between 1 and {PacketAuditExportService.MaximumEntries}.";
            return false;
        }
        return true;
    }

    public static CommandResult FormatOutcome(PacketOperationOutcome outcome)
    {
        if (outcome is PacketOperationFailure failure) return CommandResult.Fail(failure.Error);
        if (outcome is PacketTextOutcome text) return CommandResult.Ok(text.Text);
        if (outcome is PacketListOutcome list)
            return CommandResult.Ok(list.Items.Count == 0 ? "No packets." : string.Join(Environment.NewLine,
                list.Items.Select(item =>
                    $"{item.Id}\t{item.Method}\t{item.StatusCode?.ToString() ?? "-"}\t{(item.Intercepted ? "held" : "pass")}\t{item.Url}")));
        if (outcome is PacketQueryOutcome query)
        {
            var page = query.Page;
            var header = $"total={page.Total}\toffset={page.Offset}\tlimit={page.Limit}";
            return CommandResult.Ok(page.Items.Count == 0 ? header + Environment.NewLine + "No packets." :
                header + Environment.NewLine + string.Join(Environment.NewLine, page.Items.Select(item =>
                    $"{item.Id}\t{item.Method}\t{item.StatusCode?.ToString() ?? "-"}\t{(item.Intercepted ? "held" : "pass")}\t{item.Url}")));
        }
        if (outcome is PacketAuditOutcome audit)
            return CommandResult.Ok(audit.Entries.Count == 0 ? "No traffic audit entries." : string.Join(Environment.NewLine,
                audit.Entries.Select(entry => $"{entry.Timestamp:O}\t{entry.Operation}\t{entry.EntryPoint}\t{entry.PacketId}\t{entry.Side}\t" +
                    $"{entry.Before.Length}/{entry.Before.Sha256}->{entry.After.Length}/{entry.After.Sha256}\t{entry.Result}\t{entry.ErrorCode ?? "-"}\t" +
                    $"rule={entry.RuleId ?? "-"}\taction={entry.RuleAction ?? "-"}")));
        if (outcome is PacketParametersOutcome parameters)
            return CommandResult.Ok(parameters.Parameters.Count == 0 ? "No structured parameters." : string.Join(Environment.NewLine,
                parameters.Parameters.Select(parameter =>
                    $"{parameter.Location.ToString().ToLowerInvariant()}[{parameter.Occurrence}]\t{parameter.Name}\t{parameter.Value}")));
        if (outcome is PacketDraftsOutcome drafts)
            return CommandResult.Ok(drafts.Drafts.Count == 0 ? "No pending packet edits." : string.Join(Environment.NewLine,
                drafts.Drafts.Select(FormatDraft)));
        if (outcome is not PacketCommitOutcome commit)
            return CommandResult.Fail("Unsupported packet operation outcome.");

        var result = commit.Result;
        var output = string.Join(Environment.NewLine,
            $"success={result.Success.ToString().ToLowerInvariant()}",
            $"operation={SafeValue(result.Operation)}",
            $"id={SafeValue(result.PacketId)}",
            $"side={SafeValue(result.Side)}",
            $"state={SafeValue(result.FinalState)}",
            $"auditId={SafeValue(result.AuditId)}",
            $"before.length={result.Before.Length}",
            $"before.sha256={SafeValue(result.Before.Sha256)}",
            $"before.contentLength={SafeValue(result.Before.ContentLength)}",
            $"after.length={result.After.Length}",
            $"after.sha256={SafeValue(result.After.Sha256)}",
            $"after.contentLength={SafeValue(result.After.ContentLength)}",
            $"error={SafeValue(result.ErrorCode)}");
        return result.Success ? CommandResult.Ok(output) : CommandResult.Fail(output);
    }

    private static string SafeValue(string? value) => string.IsNullOrWhiteSpace(value) ? "-" :
        value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    private static string FormatDraft(PacketEditDraftStatus draft)
    {
        var failure = draft.LastCommitFailure is null ? "-" :
            $"attempts={draft.LastCommitFailure.Attempts}:{draft.LastCommitFailure.Message}";
        return $"{draft.Id}\t{draft.Side}\tpending={draft.Pending}\t" +
            $"before={draft.Before.Length}/{draft.Before.Sha256}/cl:{draft.Before.ContentLength ?? "-"}\t" +
            $"after={draft.After.Length}/{draft.After.Sha256}/cl:{draft.After.ContentLength ?? "-"}\tfailure={failure}";
    }

    private static PacketParameterSetIntent ParseParameterSet(CommandContext context)
    {
        var id = Require(context, 1, "id");
        var side = context.Arg(2) ?? "request";
        if (!Enum.TryParse<HttpParameterLocation>(Require(context, 3, "query|form|json|header|cookie"), true, out var location))
            throw new ArgumentException("Parameter location must be query, form, json, header or cookie.");
        var name = Require(context, 4, "name");
        if (!int.TryParse(Require(context, 5, "occurrence"), out var occurrence))
            throw new ArgumentException("Parameter occurrence must be an integer.");
        if (context.Args.Count < 7) throw new ArgumentException("Missing parameter value.");
        return new PacketParameterSetIntent(id, side, location, name, occurrence, context.Rest(6));
    }

    private static async Task<CommandResult> BodyReadAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketBodyReadService bodies) return CommandResult.Fail("This packet backend does not support ranged body reads.");
        var offset = long.Parse(context.Arg(3) ?? "0");
        var count = int.Parse(context.Arg(4) ?? PacketBodyChunker.DefaultChunkSize.ToString());
        var encoding = context.Arg(5)?.Equals("text", StringComparison.OrdinalIgnoreCase) == true
            ? PacketBodyChunkEncoding.SafeText : PacketBodyChunkEncoding.Base64;
        var result = await bodies.ReadBodyChunkAsync(Require(context, 1, "id"), context.Arg(2) ?? "request", offset, count, encoding, ct);
        return CommandResult.Ok($"offset={result.Offset}\tcount={result.Count}\ttotal={result.TotalLength}\tencoding={result.Encoding}\tend={result.IsEnd}{Environment.NewLine}{result.Data}");
    }

    private static async Task<CommandResult> BodyEditAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketBodyEditService editor) return CommandResult.Fail("This packet backend does not support binary edits.");
        if (!Enum.TryParse<BinaryEditKind>(Require(context, 3, "replace|insert|delete"), true, out var kind))
            return CommandResult.Fail("Edit kind must be replace, insert or delete.");
        var offset = long.Parse(Require(context, 4, "offset"));
        var count = long.Parse(context.Arg(5) ?? "0");
        var encodingText = context.Arg(6) ?? "hex";
        if (!Enum.TryParse<BinaryTextEncoding>(encodingText, true, out var encoding))
            return CommandResult.Fail("Body encoding must be hex or base64.");
        var data = kind == BinaryEditKind.Delete ? null : context.Rest(7);
        var result = await editor.EditBodyAsync(Require(context, 1, "id"), context.Arg(2) ?? "request",
            new BinaryBodyEdit(kind, offset, count, data, encoding), ct);
        return CommandResult.Ok($"Body edited: length={result.Length} sha256={result.Sha256}");
    }

    private static async Task<string> GetRequiredAsync(IPacketCommandService service, string id, string side, CancellationToken ct)
    {
        if (side is not ("request" or "response")) throw new ArgumentException("Side must be request or response.");
        return await service.GetRawAsync(id, side, ct) ?? throw new KeyNotFoundException($"Packet '{id}' has no {side}.");
    }

    private static string Require(CommandContext context, int index, string name) =>
        context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
}
