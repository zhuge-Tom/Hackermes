using Hackermes.Traffic.Persistence;
using System;
using System.Linq;

namespace Hackermes.Traffic.History;

public interface ITrafficHistoryPolicyStore
{
    string StorageFilePath { get; }
    string PolicySource { get; }
    TrafficHistoryPolicy Current { get; }
    TrafficHistoryPolicy Update(TrafficHistoryPolicy policy);
    void Reload();
    /// <summary>Hot-swaps the backing policy file (e.g. per-workspace) and reloads from it.</summary>
    TrafficHistoryPolicy SwitchStorage(string filePath, string source);
}

public sealed class TrafficHistoryPolicyStore : ITrafficHistoryPolicyStore
{
    public const string GlobalSource = "global";

    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private string _storageFilePath;
    private string _policySource = GlobalSource;
    private TrafficHistoryPolicy _current = Normalize(new TrafficHistoryPolicy());

    public TrafficHistoryPolicyStore()
        : this(VersionedJsonFile.DefaultPath("traffic-history-policy.json")) { }

    public TrafficHistoryPolicyStore(string storageFilePath)
    {
        if (string.IsNullOrWhiteSpace(storageFilePath))
            throw new ArgumentException("Storage file path is required.", nameof(storageFilePath));
        _storageFilePath = System.IO.Path.GetFullPath(storageFilePath);
        Reload();
    }

    public string StorageFilePath => _storageFilePath;
    public string PolicySource { get { lock (_gate) return _policySource; } }
    public TrafficHistoryPolicy Current { get { lock (_gate) return _current; } }

    public TrafficHistoryPolicy Update(TrafficHistoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = Normalize(policy);
        lock (_gate)
        {
            VersionedJsonFile.Write(_storageFilePath, new PolicyDocument(SchemaVersion, normalized), IsValidDocument);
            _current = normalized;
            return _current;
        }
    }

    public void Reload()
    {
        var document = VersionedJsonFile.ReadWithBackup<PolicyDocument>(_storageFilePath, IsValidDocument);
        lock (_gate) _current = document is null ? Normalize(new TrafficHistoryPolicy()) : Normalize(document.Policy);
    }

    public TrafficHistoryPolicy SwitchStorage(string filePath, string source)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Storage file path is required.", nameof(filePath));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var resolved = System.IO.Path.GetFullPath(filePath);
        lock (_gate)
        {
            _policySource = source;
            if (_storageFilePath.Equals(resolved, StringComparison.OrdinalIgnoreCase)) return _current;
            _storageFilePath = resolved;
            var document = VersionedJsonFile.ReadWithBackup<PolicyDocument>(resolved, IsValidDocument);
            _current = document is null ? Normalize(new TrafficHistoryPolicy()) : Normalize(document.Policy);
            return _current;
        }
    }

    public static TrafficHistoryPolicy Normalize(TrafficHistoryPolicy policy)
    {
        var quotas = (policy.SiteQuotas ?? [])
            .Where(value => value is not null && IsValidHostPattern(value.HostPattern))
            .Select(value => value with
            {
                HostPattern = value.HostPattern.Trim().ToLowerInvariant(),
                MaxEntries = Math.Clamp(value.MaxEntries, 1, 100_000),
                MaxStorageBytes = Math.Clamp(value.MaxStorageBytes, 1024L * 1024, 10L * 1024 * 1024 * 1024)
            })
            .GroupBy(value => value.HostPattern, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).OrderBy(value => value.HostPattern, StringComparer.Ordinal).Take(100).ToArray();
        return policy with
        {
            MaxEntries = Math.Clamp(policy.MaxEntries, 100, 100_000),
            MaxStorageBytes = Math.Clamp(policy.MaxStorageBytes, 16L * 1024 * 1024, 10L * 1024 * 1024 * 1024),
            RetentionDays = Math.Clamp(policy.RetentionDays, 1, 3650),
            SiteQuotas = quotas
        };
    }

    private static bool IsValidHostPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253) return false;
        var host = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static bool IsValidDocument(PolicyDocument document) =>
        document.SchemaVersion == SchemaVersion && document.Policy is not null;

    private sealed record PolicyDocument(int SchemaVersion, TrafficHistoryPolicy Policy);
}
