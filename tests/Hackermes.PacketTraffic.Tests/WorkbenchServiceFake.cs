using Hackermes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Shared ITrafficWorkbenchService fake for workbench view-model tests. Records the
/// surface calls the tests assert on and lets tests raise the policy-changed event.
/// </summary>
internal sealed class WorkbenchServiceFake : ITrafficWorkbenchService
{
    public IReadOnlyList<TrafficExchange> Exchanges =>
        [new("packet", DateTimeOffset.UtcNow, "POST", "https://example.test", null, "POST / HTTP/1.1\r\n\r\nbody", "")];
    public IReadOnlyList<TrafficFindingItem> StructuredFindings { get; init; } = [];
    public bool IsInterceptEnabled { get; set; }
    public bool IsResponseInterceptEnabled { get; set; }
    public event Action? Changed;
    public event Action? HistoryPolicyChanged;
    public (string Path, string? Filter)? Exported { get; private set; }
    public string? ImportedPath { get; private set; }
    public string? DraftStatus { get; init; }
    public bool DiscardResult { get; init; }
    public (string Id, string Side)? Discarded { get; private set; }
    public TrafficExchangeFilter? LastQuery { get; private set; }
    public Dictionary<string, TrafficAnnotationItem> Annotations { get; } = new(StringComparer.Ordinal);
    public string? DeletedAnnotationId { get; private set; }
    public int HistoryOverviewRequests { get; private set; }
    public TrafficHistoryOverview NextHistoryOverview { get; set; } =
        new(0, 0, 0, null, null, 5000, 256L * 1024 * 1024, 30, true, [], "global");
    public void RaiseHistoryPolicyChanged() => HistoryPolicyChanged?.Invoke();
    public void RaiseChanged() => Changed?.Invoke();
    public TrafficExchangePage Query(TrafficExchangeFilter filter)
    {
        LastQuery = filter;
        return new([], 0, filter.Offset, filter.Limit);
    }
    public Task<int> ExportArchiveFileAsync(string path, string? filter, CancellationToken cancellationToken) { Exported = (path, filter); return Task.FromResult(7); }
    public Task<int> ImportArchiveFileAsync(string path, CancellationToken cancellationToken) { ImportedPath = path; return Task.FromResult(3); }
    public Task<int> ExportSignedAuditFileAsync(string path, string? packetId, int limit, CancellationToken cancellationToken) => Task.FromResult(2);
    public Task<TrafficAuditVerificationItem> VerifySignedAuditFileAsync(string path, string? expectedKeyId, CancellationToken cancellationToken) =>
        Task.FromResult(new TrafficAuditVerificationItem(true, expectedKeyId ?? "test-key", 2, DateTimeOffset.UtcNow, null));
    public Task<TrafficOperationResult> AnalyzeAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(new TrafficOperationResult(true, "ok"));
    public Task<IReadOnlyList<TrafficFindingItem>> AnalyzeFindingsAsync(string exchangeId, string side, string rawPacket, CancellationToken cancellationToken) => Task.FromResult(StructuredFindings);
    public Task<TrafficOperationResult> ReplayAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(new TrafficOperationResult(true, "ok"));
    public Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DropAsync(string exchangeId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<string> CreateRepeaterAsync(string exchangeId, CancellationToken cancellationToken) => Task.FromResult("draft");
    public Task<string> EditBinaryBodyAsync(string exchangeId, string side, string kind, long offset, long count, string data, string encoding, CancellationToken cancellationToken) => Task.FromResult("ok");
    public Task<string> ReadBinaryBodyAsync(string exchangeId, string side, long offset, int count, string encoding, CancellationToken cancellationToken) => Task.FromResult("");
    public Task<TrafficBinaryBodyInfo> GetBinaryBodyInfoAsync(string exchangeId, string side, CancellationToken cancellationToken) =>
        Task.FromResult(new TrafficBinaryBodyInfo(0, new string('0', 64), null, null));
    public Task<string?> GetBinaryDraftStatusAsync(string exchangeId, string side, CancellationToken cancellationToken) => Task.FromResult(DraftStatus);
    public Task<bool> DiscardBinaryDraftAsync(string exchangeId, string side, CancellationToken cancellationToken) { Discarded = (exchangeId, side); return Task.FromResult(DiscardResult); }
    public Task<TrafficPacketCommitResult> ResolveContinueAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(Commit("Continue", exchangeId, "request"));
    public Task<TrafficPacketCommitResult> ResolveDropAsync(string exchangeId, CancellationToken cancellationToken) => Task.FromResult(Commit("Drop", exchangeId, "request"));
    public Task<TrafficPacketCommitResult> ResolveFulfillAsync(string exchangeId, string response, CancellationToken cancellationToken) => Task.FromResult(Commit("Fulfill", exchangeId, "response"));
    public Task<TrafficPacketCommitResult> ResolveDiscardAsync(string exchangeId, string side, CancellationToken cancellationToken)
    {
        Discarded = (exchangeId, side);
        return Task.FromResult(DiscardResult ? Commit("Discard", exchangeId, side) :
            new TrafficPacketCommitResult(false, "Discard", exchangeId, side, "Paused", "0 B", "0 B", "audit-test", "not_found", "not found"));
    }
    public IReadOnlyList<TrafficAuditItem> GetAudit(string exchangeId, int limit = 100) => [];
    public TrafficHistoryOverview GetHistoryOverview()
    {
        HistoryOverviewRequests++;
        return NextHistoryOverview;
    }
    public string PreviewHistoryCleanup() => "No entries would be removed.";
    public Task<TrafficHistoryOverview> UpdateHistoryPolicyAsync(int maxEntries, long maxBytes, int retentionDays, bool autoPrune, CancellationToken cancellationToken) =>
        Task.FromResult(new TrafficHistoryOverview(0, 0, 0, null, null, maxEntries, maxBytes, retentionDays, autoPrune, []));
    public Task<TrafficHistoryOverview> SetHistorySiteQuotaAsync(string hostPattern, int maxEntries, long maxBytes, CancellationToken cancellationToken) =>
        Task.FromResult(new TrafficHistoryOverview(0, 0, 0, null, null, 5000, 256L * 1024 * 1024, 30, true,
            [new TrafficHistorySiteQuotaItem(hostPattern, maxEntries, maxBytes)]));
    public Task<TrafficHistoryOverview> RemoveHistorySiteQuotaAsync(string hostPattern, CancellationToken cancellationToken) =>
        Task.FromResult(GetHistoryOverview());
    public Task<string> CleanupTrafficHistoryAsync(CancellationToken cancellationToken) => Task.FromResult("No entries removed.");
    public Task ClearTrafficHistoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket) => [];
    public string SetParameter(string rawPacket, string location, string name, int occurrence, string value) => rawPacket;
    public TrafficAnnotationItem? GetAnnotation(string exchangeId) => Annotations.GetValueOrDefault(exchangeId);
    public Task<TrafficAnnotationItem> SaveAnnotationAsync(string exchangeId, bool starred, string tags, string note, string status, CancellationToken cancellationToken)
    {
        var saved = new TrafficAnnotationItem(starred, tags, note, status, 1);
        Annotations[exchangeId] = saved;
        return Task.FromResult(saved);
    }
    public Task<bool> DeleteAnnotationAsync(string exchangeId, CancellationToken cancellationToken)
    {
        DeletedAnnotationId = exchangeId;
        return Task.FromResult(Annotations.Remove(exchangeId));
    }
    private static TrafficPacketCommitResult Commit(string operation, string id, string side) =>
        new(true, operation, id, side, "Continued", "0 B", "0 B", "audit-test", null, "ok");
}
