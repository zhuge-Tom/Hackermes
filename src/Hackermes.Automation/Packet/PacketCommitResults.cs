using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Packet;

/// <summary>Stable, metadata-only commit outcome. Message and ErrorCode must never contain packet content or secrets.</summary>
public sealed record PacketCommitResult(
    bool Success,
    string Operation,
    string PacketId,
    string Side,
    string FinalState,
    PacketEditVersion Before,
    PacketEditVersion After,
    string? AuditId,
    string? ErrorCode,
    string Message);

/// <summary>Optional structured mutation contract shared by UI, CLI and Agent adapters.</summary>
public interface IPacketCommitService
{
    Task<PacketCommitResult> CommitContinueAsync(string id, CancellationToken cancellationToken);
    Task<PacketCommitResult> CommitDropAsync(string id, CancellationToken cancellationToken);
    Task<PacketCommitResult> CommitEditAsync(string id, string side, string rawPacket, CancellationToken cancellationToken);
    Task<PacketCommitResult> CommitDiscardAsync(string id, string side, CancellationToken cancellationToken);
}
