using Hookmes.Automation.Commands;
using Hookmes.Traffic.History;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Traffic;

public static class HistoryManagementCommandRegistrar
{
    public const string CommandName = "traffic-history";

    public static void Register(CommandRegistry commands, ITrafficHistoryManagementService history)
    {
        commands.Register(new CommandDefinition
        {
            Name = CommandName,
            Summary = "Inspect and manage persistent traffic history retention",
            Usage = "traffic-history <stats|policy|set|preview|cleanup|clear> ...",
            IsMutating = true,
            Handler = (context, ct) => ExecuteAsync(history, context, ct)
        });
    }

    public static Task<CommandResult> ExecuteAsync(
        ITrafficHistoryManagementService history, CommandContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = context.Arg(0)?.ToLowerInvariant() switch
            {
                "stats" => CommandResult.Ok(Format(history.GetStatistics())),
                "policy" => CommandResult.Ok(Format(history.Policy)),
                "set" => Set(history, context),
                "preview" => CommandResult.Ok(Format(history.PreviewCleanup())),
                "cleanup" => CommandResult.Ok(Format(history.Cleanup())),
                "clear" => Clear(history),
                _ => CommandResult.Fail("Usage: traffic-history <stats|policy|set|preview|cleanup|clear> ...")
            };
            return Task.FromResult(result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(CommandResult.Fail(ex.Message));
        }
    }

    private static CommandResult Set(ITrafficHistoryManagementService history, CommandContext context)
    {
        if (context.Args.Count < 5)
            return CommandResult.Fail("Usage: traffic-history set <maxEntries> <maxBytes> <retentionDays> <autoPrune:true|false>");
        var policy = new TrafficHistoryPolicy(
            ParseInt(context.Arg(1), "maxEntries"),
            ParseLong(context.Arg(2), "maxBytes"),
            ParseInt(context.Arg(3), "retentionDays"),
            bool.TryParse(context.Arg(4), out var autoPrune) ? autoPrune : throw new ArgumentException("autoPrune must be true or false."));
        return CommandResult.Ok(Format(history.UpdatePolicy(policy)));
    }

    private static CommandResult Clear(ITrafficHistoryManagementService history)
    {
        history.Clear();
        return CommandResult.Ok("Traffic history cleared.");
    }

    public static string Format(TrafficHistoryStatistics value) =>
        $"entries={value.EntryCount}\tcontentBytes={value.EstimatedContentBytes}\tfileBytes={value.PersistedFileBytes}\t" +
        $"oldest={value.OldestCapture?.ToString("O") ?? "-"}\tnewest={value.NewestCapture?.ToString("O") ?? "-"}\n{Format(value.Policy)}";

    public static string Format(TrafficHistoryPolicy value) =>
        $"maxEntries={value.MaxEntries}\tmaxBytes={value.MaxStorageBytes}\tretentionDays={value.RetentionDays}\tautoPrune={value.AutoPrune}";

    public static string Format(TrafficCleanupPreview value) =>
        $"removed={value.RemovedEntries}\tremovedBytes={value.RemovedEstimatedBytes}\tremaining={value.RemainingEntries}\tremainingBytes={value.RemainingEstimatedBytes}";

    private static int ParseInt(string? value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result : throw new ArgumentException($"{name} must be an integer.");
    private static long ParseLong(string? value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result : throw new ArgumentException($"{name} must be an integer.");
}
