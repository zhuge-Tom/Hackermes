using Hookmes.Automation.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
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

public static class PacketCommandRegistrar
{
    public static void Register(CommandRegistry registry, IPacketCommandService service) => registry.Register(new CommandDefinition
    {
        Name = "packet",
        Summary = "Inspect, analyze, edit and replay captured HTTP packets",
        Usage = "packet <ls|show|analyze|diff|replay|intercept|continue|drop|edit> ...",
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
                "replay" => await Mutate(() => service.ReplayAsync(Require(context, 1, "id"), ct), "Packet replayed."),
                "intercept" => await InterceptAsync(service, Require(context, 1, "on|off"), ct),
                "continue" => await Mutate(() => service.ContinueAsync(Require(context, 1, "id"), ct), "Packet continued."),
                "drop" => await Mutate(() => service.DropAsync(Require(context, 1, "id"), ct), "Packet dropped."),
                "edit" => await EditAsync(service, context, ct),
                _ => CommandResult.Fail("Usage: packet <ls|show|analyze|diff|replay|intercept|continue|drop|edit> ...")
            };
        }
        catch (ArgumentException exception) { return CommandResult.Fail(exception.Message); }
        catch (KeyNotFoundException exception) { return CommandResult.Fail(exception.Message); }
        catch (HttpPacketParseException exception) { return CommandResult.Fail($"Invalid packet: {exception.Message}"); }
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

    private static async Task<CommandResult> EditAsync(IPacketCommandService service, CommandContext context, CancellationToken ct)
    {
        var id = Require(context, 1, "id"); var side = Require(context, 2, "request|response");
        if (context.Args.Count < 4) return CommandResult.Fail("Usage: packet edit <id> <request|response> <raw-http>");
        var raw = context.Rest(3).Replace("\\r\\n", "\r\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);
        _ = HttpPacketCodec.Parse(raw); // never persist malformed edits
        await service.EditAsync(id, side, raw, ct);
        return CommandResult.Ok("Packet updated.");
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
