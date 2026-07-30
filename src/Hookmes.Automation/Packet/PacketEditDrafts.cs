using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Packet;

public sealed record PacketEditVersion(
    long Length,
    string Sha256,
    string? ContentLength);

public sealed record PacketEditCommitFailure(
    string Message,
    DateTimeOffset OccurredAt,
    int Attempts);

public sealed record PacketEditDraftStatus(
    string Id,
    string Side,
    bool Pending,
    PacketEditVersion Before,
    PacketEditVersion After,
    PacketEditCommitFailure? LastCommitFailure = null);

/// <summary>Shared pending-edit lifecycle used by CLI, Agent and UI adapters.</summary>
public interface IPacketEditDraftService
{
    Task<IReadOnlyList<PacketEditDraftStatus>> ListPendingEditsAsync(CancellationToken cancellationToken);
    Task<PacketEditDraftStatus?> GetPendingEditAsync(string id, string side, CancellationToken cancellationToken);
    Task<bool> DiscardPendingEditAsync(string id, string side, CancellationToken cancellationToken);
}
