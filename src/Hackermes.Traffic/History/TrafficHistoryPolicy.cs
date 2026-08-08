using System;
using System.Collections.Generic;

namespace Hackermes.Traffic.History;

public sealed record TrafficHistoryPolicy(
    int MaxEntries = 5000,
    long MaxStorageBytes = 256L * 1024 * 1024,
    int RetentionDays = 30,
    bool AutoPrune = true,
    IReadOnlyList<TrafficSiteQuota>? SiteQuotas = null);

public sealed record TrafficSiteQuota(string HostPattern, int MaxEntries, long MaxStorageBytes);

public sealed record TrafficHistoryStatistics(
    int EntryCount,
    long EstimatedContentBytes,
    long PersistedFileBytes,
    DateTimeOffset? OldestCapture,
    DateTimeOffset? NewestCapture,
    TrafficHistoryPolicy Policy);

public sealed record TrafficCleanupPreview(
    int RemovedEntries,
    long RemovedEstimatedBytes,
    int RemainingEntries,
    long RemainingEstimatedBytes);

public sealed record TrafficHistoryChanged(string Operation, TrafficHistoryStatistics Statistics);
