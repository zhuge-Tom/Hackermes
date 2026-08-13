using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Packet;

/// <summary>
/// Typed packet operations shared by command-line and Agent adapters. Adapters own parsing and
/// presentation; this executor owns validation and traffic-side effects.
/// </summary>
public abstract record PacketOperationIntent;

public sealed record PacketListIntent(string? Filter = null) : PacketOperationIntent;
public sealed record PacketShowIntent(string Id, string Side = "request", bool Pretty = true) : PacketOperationIntent;
public sealed record PacketDiffIntent(string LeftId, string RightId, string Side = "request") : PacketOperationIntent;
public sealed record PacketQueryIntent(PacketQuery Query) : PacketOperationIntent;
public sealed record PacketAuditIntent(string? PacketId = null, int Limit = 100) : PacketOperationIntent;
public sealed record PacketParameterListIntent(string Id, string Side = "request") : PacketOperationIntent;
public sealed record PacketParameterSetIntent(
    string Id,
    string Side,
    HttpParameterLocation Location,
    string Name,
    int Occurrence,
    string Value) : PacketOperationIntent;
public sealed record PacketReplayIntent(string Id) : PacketOperationIntent;
public sealed record PacketInterceptionIntent(bool Enabled) : PacketOperationIntent;
public sealed record PacketInterceptionModeIntent(PacketInterceptionMode Mode) : PacketOperationIntent;
public sealed record PacketDraftListIntent : PacketOperationIntent;
public sealed record PacketDraftShowIntent(string Id, string Side = "request") : PacketOperationIntent;

public enum PacketCommitAction { Continue, Drop, Edit, Discard }

public sealed record PacketCommitIntent(
    PacketCommitAction Action,
    string Id,
    string Side = "request",
    string? RawHttp = null) : PacketOperationIntent;

public abstract record PacketOperationOutcome;
public sealed record PacketTextOutcome(string Text) : PacketOperationOutcome;
public sealed record PacketListOutcome(IReadOnlyList<PacketSummary> Items) : PacketOperationOutcome;
public sealed record PacketQueryOutcome(PacketQueryPage Page) : PacketOperationOutcome;
public sealed record PacketAuditOutcome(IReadOnlyList<PacketAuditEntry> Entries) : PacketOperationOutcome;
public sealed record PacketParametersOutcome(IReadOnlyList<HttpPacketParameter> Parameters) : PacketOperationOutcome;
public sealed record PacketDraftsOutcome(IReadOnlyList<PacketEditDraftStatus> Drafts) : PacketOperationOutcome;
public sealed record PacketCommitOutcome(PacketCommitResult Result) : PacketOperationOutcome;
public sealed record PacketOperationFailure(string Error) : PacketOperationOutcome;

public static class PacketOperationExecutor
{
    public static async Task<PacketOperationOutcome> ExecuteAsync(
        IPacketCommandService service,
        PacketOperationIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(intent);

        try
        {
            return intent switch
            {
                PacketListIntent list => await ListAsync(service, list, cancellationToken).ConfigureAwait(false),
                PacketShowIntent show => await ShowAsync(service, show, cancellationToken).ConfigureAwait(false),
                PacketDiffIntent diff => await DiffAsync(service, diff, cancellationToken).ConfigureAwait(false),
                PacketQueryIntent query => await QueryAsync(service, query, cancellationToken).ConfigureAwait(false),
                PacketAuditIntent audit => Audit(service, audit),
                PacketParameterListIntent parameters => await ListParametersAsync(service, parameters, cancellationToken).ConfigureAwait(false),
                PacketParameterSetIntent parameter => await SetParameterAsync(service, parameter, cancellationToken).ConfigureAwait(false),
                PacketReplayIntent replay => await ReplayAsync(service, replay, cancellationToken).ConfigureAwait(false),
                PacketInterceptionIntent interception => await SetInterceptionAsync(service, interception, cancellationToken).ConfigureAwait(false),
                PacketInterceptionModeIntent mode => await SetInterceptionModeAsync(service, mode, cancellationToken).ConfigureAwait(false),
                PacketDraftListIntent => await ListDraftsAsync(service, cancellationToken).ConfigureAwait(false),
                PacketDraftShowIntent draft => await ShowDraftAsync(service, draft, cancellationToken).ConfigureAwait(false),
                PacketCommitIntent commit => await CommitAsync(service, commit, cancellationToken).ConfigureAwait(false),
                _ => new PacketOperationFailure("Unsupported packet operation.")
            };
        }
        catch (ArgumentException exception) { return new PacketOperationFailure(exception.Message); }
        catch (KeyNotFoundException exception) { return new PacketOperationFailure(exception.Message); }
        catch (HttpPacketParseException exception) { return new PacketOperationFailure($"Invalid packet: {exception.Message}"); }
        catch (InvalidDataException exception) { return new PacketOperationFailure($"Invalid archive: {exception.Message}"); }
    }

    private static async Task<PacketOperationOutcome> ListAsync(
        IPacketCommandService service,
        PacketListIntent intent,
        CancellationToken cancellationToken) =>
        new PacketListOutcome(await service.ListAsync(intent.Filter, cancellationToken).ConfigureAwait(false));

    private static async Task<PacketOperationOutcome> ShowAsync(
        IPacketCommandService service,
        PacketShowIntent intent,
        CancellationToken cancellationToken)
    {
        var raw = await GetRequiredAsync(service, intent.Id, intent.Side, cancellationToken).ConfigureAwait(false);
        return new PacketTextOutcome(intent.Pretty ? HttpPacketCodec.Format(HttpPacketCodec.Parse(raw), true) : raw);
    }

    private static async Task<PacketOperationOutcome> DiffAsync(
        IPacketCommandService service,
        PacketDiffIntent intent,
        CancellationToken cancellationToken)
    {
        var left = HttpPacketCodec.Parse(await GetRequiredAsync(
            service, intent.LeftId, intent.Side, cancellationToken).ConfigureAwait(false));
        var right = HttpPacketCodec.Parse(await GetRequiredAsync(
            service, intent.RightId, intent.Side, cancellationToken).ConfigureAwait(false));
        var differences = HttpPacketAnalyzer.Diff(left, right);
        return new PacketTextOutcome(differences.Count == 0 ? "Packets are equivalent." : string.Join(Environment.NewLine,
            System.Linq.Enumerable.Select(differences, difference =>
                $"@@ {difference.Location} @@{Environment.NewLine}- {difference.Left}{Environment.NewLine}+ {difference.Right}")));
    }

    private static async Task<PacketOperationOutcome> QueryAsync(
        IPacketCommandService service,
        PacketQueryIntent intent,
        CancellationToken cancellationToken)
    {
        if (service is not IPacketQueryService queries)
            return new PacketOperationFailure("This packet backend does not support structured packet queries.");

        var query = PacketQueryLimits.Validate(intent.Query);
        return new PacketQueryOutcome(await queries.QueryPacketsAsync(query, cancellationToken).ConfigureAwait(false));
    }

    private static PacketOperationOutcome Audit(IPacketCommandService service, PacketAuditIntent intent)
    {
        if (service is not IPacketAuditQueryService audit)
            return new PacketOperationFailure("This packet backend does not support traffic audit queries.");
        if (intent.Limit <= 0)
            return new PacketOperationFailure("Audit limit must be a positive integer.");

        var packetId = intent.PacketId == "*" ? null : intent.PacketId;
        return new PacketAuditOutcome(audit.QueryAudit(new PacketAuditQuery(packetId, Limit: intent.Limit)));
    }

    private static async Task<PacketOperationOutcome> ListParametersAsync(
        IPacketCommandService service,
        PacketParameterListIntent intent,
        CancellationToken cancellationToken)
    {
        var packet = HttpPacketCodec.Parse(await GetRequiredAsync(
            service, intent.Id, intent.Side, cancellationToken).ConfigureAwait(false));
        return new PacketParametersOutcome(HttpPacketParameters.Read(packet));
    }

    private static async Task<PacketOperationOutcome> SetParameterAsync(
        IPacketCommandService service,
        PacketParameterSetIntent intent,
        CancellationToken cancellationToken)
    {
        var packet = HttpPacketCodec.Parse(await GetRequiredAsync(
            service, intent.Id, intent.Side, cancellationToken).ConfigureAwait(false));
        var updated = HttpPacketParameters.Set(
            packet, intent.Location, intent.Name, intent.Occurrence, intent.Value);
        await service.EditAsync(intent.Id, intent.Side, HttpPacketCodec.Format(updated, false), cancellationToken)
            .ConfigureAwait(false);
        return new PacketTextOutcome("Parameter updated and packet submitted.");
    }

    private static async Task<PacketOperationOutcome> ReplayAsync(
        IPacketCommandService service,
        PacketReplayIntent intent,
        CancellationToken cancellationToken)
    {
        await service.ReplayAsync(intent.Id, cancellationToken).ConfigureAwait(false);
        return new PacketTextOutcome("Packet replayed.");
    }

    private static async Task<PacketOperationOutcome> SetInterceptionAsync(
        IPacketCommandService service,
        PacketInterceptionIntent intent,
        CancellationToken cancellationToken)
    {
        await service.SetInterceptionAsync(intent.Enabled, cancellationToken).ConfigureAwait(false);
        return new PacketTextOutcome($"Interception {(intent.Enabled ? "enabled" : "disabled")}.");
    }

    private static async Task<PacketOperationOutcome> SetInterceptionModeAsync(
        IPacketCommandService service,
        PacketInterceptionModeIntent intent,
        CancellationToken cancellationToken)
    {
        if (service is not IPacketInterceptionModeService modes)
            return new PacketOperationFailure("This packet backend does not support independent request/response interception.");
        await modes.SetInterceptionModeAsync(intent.Mode, cancellationToken).ConfigureAwait(false);
        return new PacketTextOutcome($"Interception mode set to {intent.Mode.ToString().ToLowerInvariant()}.");
    }

    private static async Task<PacketOperationOutcome> ListDraftsAsync(
        IPacketCommandService service,
        CancellationToken cancellationToken)
    {
        if (service is not IPacketEditDraftService drafts)
            return new PacketOperationFailure("This packet backend does not support edit drafts.");
        return new PacketDraftsOutcome(await drafts.ListPendingEditsAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<PacketOperationOutcome> ShowDraftAsync(
        IPacketCommandService service,
        PacketDraftShowIntent intent,
        CancellationToken cancellationToken)
    {
        if (service is not IPacketEditDraftService drafts)
            return new PacketOperationFailure("This packet backend does not support edit drafts.");
        var draft = await drafts.GetPendingEditAsync(intent.Id, intent.Side, cancellationToken).ConfigureAwait(false);
        return draft is null
            ? new PacketOperationFailure("Pending packet edit was not found.")
            : new PacketDraftsOutcome([draft]);
    }

    private static async Task<PacketOperationOutcome> CommitAsync(
        IPacketCommandService service,
        PacketCommitIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent.Action == PacketCommitAction.Edit && intent.RawHttp is null)
            return new PacketOperationFailure("Missing raw HTTP packet.");
        if (intent.Action == PacketCommitAction.Edit)
            _ = HttpPacketCodec.Parse(intent.RawHttp!); // never submit a malformed edit

        if (service is IPacketCommitService commits)
        {
            var result = intent.Action switch
            {
                PacketCommitAction.Continue => await commits.CommitContinueAsync(intent.Id, cancellationToken).ConfigureAwait(false),
                PacketCommitAction.Drop => await commits.CommitDropAsync(intent.Id, cancellationToken).ConfigureAwait(false),
                PacketCommitAction.Edit => await commits.CommitEditAsync(intent.Id, intent.Side, intent.RawHttp!, cancellationToken).ConfigureAwait(false),
                PacketCommitAction.Discard => await commits.CommitDiscardAsync(intent.Id, intent.Side, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(intent.Action))
            };
            return new PacketCommitOutcome(result);
        }

        switch (intent.Action)
        {
            case PacketCommitAction.Continue:
                await service.ContinueAsync(intent.Id, cancellationToken).ConfigureAwait(false);
                return new PacketTextOutcome("Packet continued.");
            case PacketCommitAction.Drop:
                await service.DropAsync(intent.Id, cancellationToken).ConfigureAwait(false);
                return new PacketTextOutcome("Packet dropped.");
            case PacketCommitAction.Edit:
                await service.EditAsync(intent.Id, intent.Side, intent.RawHttp!, cancellationToken).ConfigureAwait(false);
                return new PacketTextOutcome("Packet updated.");
            case PacketCommitAction.Discard:
                if (service is not IPacketEditDraftService drafts)
                    return new PacketOperationFailure("This packet backend does not support edit drafts.");
                var discarded = await drafts.DiscardPendingEditAsync(intent.Id, intent.Side, cancellationToken).ConfigureAwait(false);
                return discarded
                    ? new PacketTextOutcome("Pending packet edit discarded.")
                    : new PacketOperationFailure("Pending packet edit was not found.");
            default:
                throw new ArgumentOutOfRangeException(nameof(intent.Action));
        }
    }

    private static async Task<string> GetRequiredAsync(
        IPacketCommandService service,
        string id,
        string side,
        CancellationToken cancellationToken)
    {
        if (side is not ("request" or "response"))
            throw new ArgumentException("Side must be request or response.");
        return await service.GetRawAsync(id, side, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Packet '{id}' has no {side}.");
    }
}
