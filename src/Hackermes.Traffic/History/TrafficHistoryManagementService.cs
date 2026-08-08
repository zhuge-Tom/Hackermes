using Hackermes.Traffic.Models;
using Hackermes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hackermes.Traffic.History;

public interface ITrafficHistoryManagementService
{
    event Action<TrafficHistoryChanged>? Changed;
    TrafficHistoryPolicy Policy { get; }
    TrafficHistoryStatistics GetStatistics();
    TrafficCleanupPreview PreviewCleanup(TrafficHistoryPolicy? policy = null);
    TrafficHistoryPolicy UpdatePolicy(TrafficHistoryPolicy policy, bool applyNow = true);
    TrafficCleanupPreview Cleanup();
    void Clear();
}

public sealed class TrafficHistoryManagementService : ITrafficHistoryManagementService
{
    private readonly TrafficStore _store;
    private readonly ITrafficHistoryPolicyStore _policies;
    private readonly ITrafficHistoryPersistence _persistence;

    public TrafficHistoryManagementService(
        TrafficStore store,
        ITrafficHistoryPolicyStore policies,
        ITrafficHistoryPersistence persistence)
    {
        _store = store;
        _policies = policies;
        _persistence = persistence;
    }

    public event Action<TrafficHistoryChanged>? Changed;
    public TrafficHistoryPolicy Policy => _policies.Current;

    public TrafficHistoryStatistics GetStatistics()
    {
        var items = _store.ReadAllChronological();
        return new TrafficHistoryStatistics(items.Count, items.Sum(TrafficHistorySizing.Estimate),
            PersistedLength(),
            items.Count == 0 ? null : items[0].CapturedAt,
            items.Count == 0 ? null : items[^1].CapturedAt, Policy);
    }

    public TrafficCleanupPreview PreviewCleanup(TrafficHistoryPolicy? policy = null) =>
        TrafficHistoryRetention.Preview(_store.ReadAllChronological(),
            TrafficHistoryPolicyStore.Normalize(policy ?? Policy), DateTimeOffset.UtcNow);

    public TrafficHistoryPolicy UpdatePolicy(TrafficHistoryPolicy policy, bool applyNow = true)
    {
        var updated = _policies.Update(policy);
        if (applyNow) _store.ApplyRetentionPolicy(updated, force: true);
        Publish("policy");
        return updated;
    }

    public TrafficCleanupPreview Cleanup()
    {
        var result = _store.ApplyRetentionPolicy(Policy, force: true);
        _persistence.Flush();
        Publish("cleanup");
        return result;
    }

    public void Clear()
    {
        _store.Clear();
        _persistence.Flush();
        Publish("clear");
    }

    private void Publish(string operation) => Changed?.Invoke(new TrafficHistoryChanged(operation, GetStatistics()));

    private long PersistedLength()
    {
        try { return File.Exists(_persistence.FilePath) ? new FileInfo(_persistence.FilePath).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

internal static class TrafficHistorySizing
{
    public static long Estimate(TrafficMessage item)
    {
        long size = item.RequestBody?.LongLength ?? 0;
        size += item.ResponseBody?.LongLength ?? 0;
        size += Text(item.Id) + Text(item.PageId) + Text(item.Method) + Text(item.Url) + Text(item.ResourceType);
        size += Text(item.ResponseStatusText) + Text(item.AppliedRuleId) + Text(item.Error);
        size += item.RequestHeaders.Sum(header => Text(header.Name) + Text(header.Value));
        size += item.ResponseHeaders.Sum(header => Text(header.Name) + Text(header.Value));
        return size;
    }

    private static int Text(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}

internal static class TrafficHistoryRetention
{
    public static TrafficCleanupPreview Preview(
        IReadOnlyList<TrafficMessage> chronological,
        TrafficHistoryPolicy policy,
        DateTimeOffset now)
    {
        var plan = Plan(chronological, policy, now);
        var total = chronological.Sum(TrafficHistorySizing.Estimate);
        return new TrafficCleanupPreview(plan.Ids.Count, plan.RemovedBytes,
            chronological.Count - plan.Ids.Count, total - plan.RemovedBytes);
    }

    public static TrafficRemovalPlan Plan(
        IReadOnlyList<TrafficMessage> items,
        TrafficHistoryPolicy policy,
        DateTimeOffset now)
    {
        var retained = items.OrderBy(item => item.CapturedAt).ToList();
        long total = retained.Sum(TrafficHistorySizing.Estimate);
        long removedBytes = 0;
        var removedIds = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = now.AddDays(-policy.RetentionDays);
        foreach (var expired in retained.Where(item => item.CapturedAt < cutoff).ToArray())
        {
            var size = TrafficHistorySizing.Estimate(expired);
            retained.Remove(expired);
            total -= size;
            removedBytes += size;
            removedIds.Add(expired.Id);
        }
        foreach (var quota in policy.SiteQuotas ?? [])
        {
            var matching = retained.Where(item => MatchesHost(item.Url, quota.HostPattern))
                .OrderBy(item => item.CapturedAt).ToList();
            long siteBytes = matching.Sum(TrafficHistorySizing.Estimate);
            while (matching.Count > quota.MaxEntries || siteBytes > quota.MaxStorageBytes)
            {
                var oldest = matching[0];
                matching.RemoveAt(0);
                retained.Remove(oldest);
                var size = TrafficHistorySizing.Estimate(oldest);
                total -= size;
                siteBytes -= size;
                removedBytes += size;
                removedIds.Add(oldest.Id);
            }
        }
        while (retained.Count > policy.MaxEntries || total > policy.MaxStorageBytes)
        {
            var oldest = retained[0];
            var size = TrafficHistorySizing.Estimate(oldest);
            retained.RemoveAt(0);
            total -= size;
            removedBytes += size;
            removedIds.Add(oldest.Id);
        }
        return new TrafficRemovalPlan(removedIds, removedBytes);
    }

    private static bool MatchesHost(string url, string pattern)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record TrafficRemovalPlan(IReadOnlySet<string> Ids, long RemovedBytes);
}
