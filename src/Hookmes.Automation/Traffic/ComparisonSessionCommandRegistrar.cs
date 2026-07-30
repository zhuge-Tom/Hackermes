using Hookmes.Automation.Commands;
using Hookmes.Traffic.Comparison;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Traffic;

public static class ComparisonSessionCommandRegistrar
{
    public const string CommandName = "compare-session";

    public static void Register(CommandRegistry commands, ITrafficComparisonService comparisons)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(comparisons);
        commands.Register(new CommandDefinition
        {
            Name = CommandName,
            Summary = "Manage persistent named HTTP comparison sessions",
            Usage = "compare-session <list|create|rename|recalculate|delete> ...",
            IsMutating = true,
            Handler = (context, ct) => ExecuteAsync(comparisons, context, ct)
        });
    }

    public static Task<CommandResult> ExecuteAsync(
        ITrafficComparisonService comparisons,
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(context.Arg(0)?.ToLowerInvariant() switch
            {
                "list" or "ls" => CommandResult.Ok(FormatList(comparisons.GetAll())),
                "create" => Create(comparisons, context),
                "rename" => Rename(comparisons, context),
                "recalculate" or "recalc" => Recalculate(comparisons, context),
                "delete" or "rm" => Delete(comparisons, context),
                _ => CommandResult.Fail("Usage: compare-session <list|create|rename|recalculate|delete> ...")
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Task.FromResult(CommandResult.Fail(ex.Message));
        }
    }

    public static ComparisonSource ParseSource(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (var (prefix, kind) in new[]
                 {
                     ("traffic-request:", ComparisonSourceKind.TrafficRequest),
                     ("traffic-response:", ComparisonSourceKind.TrafficResponse)
                 })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var packetId = value[prefix.Length..];
                if (packetId.Length == 0) throw new ArgumentException("Comparison packet id is required.", nameof(value));
                return new ComparisonSource(kind, PacketId: packetId);
            }
        }

        foreach (var (prefix, kind) in new[]
                 {
                     ("repeater-request:", ComparisonSourceKind.RepeaterRequest),
                     ("repeater-response:", ComparisonSourceKind.RepeaterResponse)
                 })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var reference = value[prefix.Length..];
            var separator = reference.IndexOf(':');
            if (separator <= 0 || separator == reference.Length - 1)
                throw new ArgumentException("Repeater source must contain draft id and send result id.", nameof(value));
            return new ComparisonSource(kind, DraftId: reference[..separator], SendResultId: reference[(separator + 1)..]);
        }
        throw new ArgumentException("Source must start with traffic-request:, traffic-response:, repeater-request:, or repeater-response:.", nameof(value));
    }

    private static CommandResult Create(ITrafficComparisonService comparisons, CommandContext context)
    {
        var left = ParseSource(Required(context, 1, "left source"));
        var right = ParseSource(Required(context, 2, "right source"));
        var name = context.Rest(3);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Comparison name is required.");
        var session = comparisons.Create(name, left, right);
        return CommandResult.Ok(FormatSession(session));
    }

    private static CommandResult Rename(ITrafficComparisonService comparisons, CommandContext context)
    {
        var id = Required(context, 1, "session id");
        var name = context.Rest(2);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Comparison name is required.");
        return CommandResult.Ok(FormatSession(comparisons.Rename(id, name)));
    }

    private static CommandResult Recalculate(ITrafficComparisonService comparisons, CommandContext context) =>
        CommandResult.Ok(FormatSession(comparisons.Recalculate(Required(context, 1, "session id"))));

    private static CommandResult Delete(ITrafficComparisonService comparisons, CommandContext context) =>
        CommandResult.Ok(comparisons.Delete(Required(context, 1, "session id"))
            ? "Comparison session deleted."
            : "Comparison session did not exist.");

    private static string FormatList(IReadOnlyList<TrafficComparisonSession> sessions) => sessions.Count == 0
        ? "No saved comparison sessions."
        : string.Join(Environment.NewLine, sessions.Select(FormatSession));

    private static string FormatSession(TrafficComparisonSession session) =>
        $"{session.Id}\t{session.Name}\trev={session.Revision}\t{(session.Result.Equal ? "equal" : "different")}";

    private static string Required(CommandContext context, int index, string name) =>
        context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
}
