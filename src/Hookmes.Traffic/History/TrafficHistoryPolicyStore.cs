using Hookmes.Traffic.Persistence;
using System;

namespace Hookmes.Traffic.History;

public interface ITrafficHistoryPolicyStore
{
    string StorageFilePath { get; }
    TrafficHistoryPolicy Current { get; }
    TrafficHistoryPolicy Update(TrafficHistoryPolicy policy);
    void Reload();
}

public sealed class TrafficHistoryPolicyStore : ITrafficHistoryPolicyStore
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly string _storageFilePath;
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

    public static TrafficHistoryPolicy Normalize(TrafficHistoryPolicy policy) => policy with
    {
        MaxEntries = Math.Clamp(policy.MaxEntries, 100, 100_000),
        MaxStorageBytes = Math.Clamp(policy.MaxStorageBytes, 16L * 1024 * 1024, 10L * 1024 * 1024 * 1024),
        RetentionDays = Math.Clamp(policy.RetentionDays, 1, 3650)
    };

    private static bool IsValidDocument(PolicyDocument document) =>
        document.SchemaVersion == SchemaVersion && document.Policy is not null;

    private sealed record PolicyDocument(int SchemaVersion, TrafficHistoryPolicy Policy);
}
