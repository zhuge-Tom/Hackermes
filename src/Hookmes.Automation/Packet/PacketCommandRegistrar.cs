using Hookmes.Automation.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Packet;

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
        Usage = "packet <ls|show|analyze|diff|param-list|param-set|body-info|body-read|body-edit|draft-list|draft-show|draft-discard|replay|intercept|intercept-mode|continue|drop|edit|export|import> ...",
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
                "ls" => await ListAsync(service, context.Arg(1), ct),
                "show" => await ShowAsync(service, Require(context, 1, "id"), context.Arg(2) ?? "request", true, ct),
                "analyze" => await AnalyzeAsync(service, Require(context, 1, "id"), context.Arg(2) ?? "request", ct),
                "diff" => await DiffAsync(service, Require(context, 1, "left id"), Require(context, 2, "right id"), context.Arg(3) ?? "request", ct),
                "param-list" => await ParameterListAsync(service, context, ct),
                "param-set" => await ParameterSetAsync(service, context, ct),
                "body-info" => await BodyInfoAsync(service, context, ct),
                "body-read" => await BodyReadAsync(service, context, ct),
                "body-edit" => await BodyEditAsync(service, context, ct),
                "draft-list" => await DraftListAsync(service, ct),
                "draft-show" => await DraftShowAsync(service, context, ct),
                "draft-discard" => await DraftDiscardAsync(service, context, ct),
                "replay" => await Mutate(() => service.ReplayAsync(Require(context, 1, "id"), ct), "Packet replayed."),
                "intercept" => await InterceptAsync(service, Require(context, 1, "on|off"), ct),
                "intercept-mode" => await InterceptionModeAsync(service, Require(context, 1, "request|response|both|off"), ct),
                "continue" => await Mutate(() => service.ContinueAsync(Require(context, 1, "id"), ct), "Packet continued."),
                "drop" => await Mutate(() => service.DropAsync(Require(context, 1, "id"), ct), "Packet dropped."),
                "edit" => await EditAsync(service, context, ct),
                "export" => await ExportAsync(service, context, ct),
                "import" => await ImportAsync(service, context, ct),
                _ => CommandResult.Fail("Usage: packet <ls|show|analyze|diff|param-list|param-set|body-info|body-read|body-edit|draft-list|draft-show|draft-discard|replay|intercept|intercept-mode|continue|drop|edit|export|import> ...")
            };
        }
        catch (ArgumentException exception) { return CommandResult.Fail(exception.Message); }
        catch (KeyNotFoundException exception) { return CommandResult.Fail(exception.Message); }
        catch (HttpPacketParseException exception) { return CommandResult.Fail($"Invalid packet: {exception.Message}"); }
        catch (InvalidDataException exception) { return CommandResult.Fail($"Invalid archive: {exception.Message}"); }
        catch (IOException exception) { return CommandResult.Fail($"Archive I/O failed: {exception.Message}"); }
    }

    private static async Task<CommandResult> ListAsync(IPacketCommandService service, string? filter, CancellationToken ct)
    {
        var rows = await service.ListAsync(filter, ct);
        return CommandResult.Ok(rows.Count == 0 ? "No packets." : string.Join(Environment.NewLine,
            rows.Select(x => $"{x.Id}\t{x.Method}\t{x.StatusCode?.ToString() ?? "-"}\t{(x.Intercepted ? "held" : "pass")}\t{x.Url}")));
    }

    private static async Task<CommandResult> ShowAsync(IPacketCommandService service, string id, string side, bool pretty, CancellationToken ct)
    {
        var raw = await GetRequiredAsync(service, id, side, ct);
        return CommandResult.Ok(pretty ? HttpPacketCodec.Format(HttpPacketCodec.Parse(raw), true) : raw);
    }

    private static async Task<CommandResult> AnalyzeAsync(IPacketCommandService service, string id, string side, CancellationToken ct)
    {
        var analysis = HttpPacketAnalyzer.Analyze(HttpPacketCodec.Parse(await GetRequiredAsync(service, id, side, ct)));
        var lines = analysis.Findings.Select(f => $"[{f.Severity}] {f.Code}: {f.Message}" + (f.Location is null ? "" : $" ({f.Location})")).ToList();
        if (lines.Count == 0) lines.Add("No built-in findings.");
        if (analysis.SensitiveFields.Count > 0) lines.Add("Sensitive: " + string.Join(", ", analysis.SensitiveFields));
        return CommandResult.Ok(string.Join(Environment.NewLine, lines));
    }

    private static async Task<CommandResult> DiffAsync(IPacketCommandService service, string leftId, string rightId, string side, CancellationToken ct)
    {
        var left = HttpPacketCodec.Parse(await GetRequiredAsync(service, leftId, side, ct));
        var right = HttpPacketCodec.Parse(await GetRequiredAsync(service, rightId, side, ct));
        var differences = HttpPacketAnalyzer.Diff(left, right);
        return CommandResult.Ok(differences.Count == 0 ? "Packets are equivalent." : string.Join(Environment.NewLine,
            differences.Select(d => $"@@ {d.Location} @@{Environment.NewLine}- {d.Left}{Environment.NewLine}+ {d.Right}")));
    }

    private static async Task<CommandResult> InterceptAsync(IPacketCommandService service, string value, CancellationToken ct)
    {
        if (!bool.TryParse(value.Replace("on", "true", StringComparison.OrdinalIgnoreCase).Replace("off", "false", StringComparison.OrdinalIgnoreCase), out var enabled))
            return CommandResult.Fail("Usage: packet intercept <on|off>");
        await service.SetInterceptionAsync(enabled, ct);
        return CommandResult.Ok($"Interception {(enabled ? "enabled" : "disabled")}.");
    }

    private static async Task<CommandResult> InterceptionModeAsync(IPacketCommandService service, string value, CancellationToken ct)
    {
        if (service is not IPacketInterceptionModeService modes)
            return CommandResult.Fail("This packet backend does not support independent request/response interception.");
        var parsed = value.Trim().ToLowerInvariant() switch
        {
            "request" => PacketInterceptionMode.Request,
            "response" => PacketInterceptionMode.Response,
            "both" => PacketInterceptionMode.Both,
            "off" => PacketInterceptionMode.Off,
            _ => (PacketInterceptionMode?)null
        };
        if (parsed is not { } mode)
            return CommandResult.Fail("Usage: packet intercept-mode <request|response|both|off>");
        await modes.SetInterceptionModeAsync(mode, ct);
        return CommandResult.Ok($"Interception mode set to {mode.ToString().ToLowerInvariant()}.");
    }

    private static async Task<CommandResult> EditAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        var id = Require(context, 1, "id"); var side = Require(context, 2, "request|response");
        if (context.Args.Count < 4) return CommandResult.Fail("Usage: packet edit <id> <request|response> <raw-http>");
        var raw = context.Rest(3).Replace("\\r\\n", "\r\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);
        _ = HttpPacketCodec.Parse(raw); // never persist malformed edits
        await service.EditAsync(id, side, raw, ct);
        return CommandResult.Ok("Packet updated.");
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

    private static async Task<CommandResult> DraftListAsync(IPacketCommandService service, CancellationToken ct)
    {
        if (service is not IPacketEditDraftService drafts) return CommandResult.Fail("This packet backend does not support edit drafts.");
        var items = await drafts.ListPendingEditsAsync(ct);
        return CommandResult.Ok(items.Count == 0 ? "No pending packet edits." : string.Join(Environment.NewLine,
            items.Select(FormatDraft)));
    }

    private static async Task<CommandResult> DraftShowAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketEditDraftService drafts) return CommandResult.Fail("This packet backend does not support edit drafts.");
        var item = await drafts.GetPendingEditAsync(Require(context, 1, "id"), context.Arg(2) ?? "request", ct);
        return item is null ? CommandResult.Fail("Pending packet edit was not found.") : CommandResult.Ok(FormatDraft(item));
    }

    private static async Task<CommandResult> DraftDiscardAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        if (service is not IPacketEditDraftService drafts) return CommandResult.Fail("This packet backend does not support edit drafts.");
        var discarded = await drafts.DiscardPendingEditAsync(Require(context, 1, "id"), context.Arg(2) ?? "request", ct);
        return discarded ? CommandResult.Ok("Pending packet edit discarded.") : CommandResult.Fail("Pending packet edit was not found.");
    }

    private static string FormatDraft(PacketEditDraftStatus draft)
    {
        var failure = draft.LastCommitFailure is null ? "-" :
            $"attempts={draft.LastCommitFailure.Attempts}:{draft.LastCommitFailure.Message}";
        return $"{draft.Id}\t{draft.Side}\tpending={draft.Pending}\t" +
            $"before={draft.Before.Length}/{draft.Before.Sha256}/cl:{draft.Before.ContentLength ?? "-"}\t" +
            $"after={draft.After.Length}/{draft.After.Sha256}/cl:{draft.After.ContentLength ?? "-"}\tfailure={failure}";
    }

    private static async Task<CommandResult> ParameterListAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        var side = context.Arg(2) ?? "request";
        var packet = HttpPacketCodec.Parse(await GetRequiredAsync(service, Require(context, 1, "id"), side, ct));
        var parameters = HttpPacketParameters.Read(packet);
        return CommandResult.Ok(parameters.Count == 0 ? "No structured parameters." : string.Join(Environment.NewLine,
            parameters.Select(parameter => $"{parameter.Location.ToString().ToLowerInvariant()}[{parameter.Occurrence}]\t{parameter.Name}\t{parameter.Value}")));
    }

    private static async Task<CommandResult> ParameterSetAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        var id = Require(context, 1, "id");
        var side = context.Arg(2) ?? "request";
        if (!Enum.TryParse<HttpParameterLocation>(Require(context, 3, "query|form|json"), true, out var location))
            return CommandResult.Fail("Parameter location must be query, form or json.");
        var name = Require(context, 4, "name");
        if (!int.TryParse(Require(context, 5, "occurrence"), out var occurrence))
            return CommandResult.Fail("Parameter occurrence must be an integer.");
        if (context.Args.Count < 7) return CommandResult.Fail("Missing parameter value.");
        var packet = HttpPacketCodec.Parse(await GetRequiredAsync(service, id, side, ct));
        var updated = HttpPacketParameters.Set(packet, location, name, occurrence, context.Rest(6));
        await service.EditAsync(id, side, HttpPacketCodec.Format(updated, false), ct);
        return CommandResult.Ok("Parameter updated and packet submitted.");
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
    private static async Task<CommandResult> Mutate(Func<Task> operation, string message) { await operation(); return CommandResult.Ok(message); }
}
