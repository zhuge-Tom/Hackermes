using Hackermes.Automation.Traffic;
using Hackermes.Traffic.History;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Services;
using System;
using System.IO;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficHistoryWorkspaceIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hackermes-history-workspace-tests", Guid.NewGuid().ToString("N"));
    private string GlobalPolicyPath => Path.Combine(_directory, "global-policy.json");
    private string WorkspacePolicyPath => Path.Combine(_directory, "workspace", ".hackermes", "traffic-history-policy.json");

    public TrafficHistoryWorkspaceIsolationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Switch_storage_loads_workspace_policy_and_routes_updates()
    {
        var global = new TrafficHistoryPolicyStore(GlobalPolicyPath);
        global.Update(new TrafficHistoryPolicy(5000, 64L * 1024 * 1024, 30, true));

        SeedWorkspacePolicy(new TrafficHistoryPolicy(100, 16L * 1024 * 1024, 7, false));

        Assert.Equal(TrafficHistoryPolicyStore.GlobalSource, global.PolicySource);
        var switched = global.SwitchStorage(WorkspacePolicyPath, "workspace");
        Assert.Equal("workspace", global.PolicySource);
        Assert.Equal(Path.GetFullPath(WorkspacePolicyPath), global.StorageFilePath);
        Assert.Equal(100, switched.MaxEntries);
        Assert.False(switched.AutoPrune);

        global.Update(new TrafficHistoryPolicy(200, 32L * 1024 * 1024, 14, true));
        Assert.Equal(200, new TrafficHistoryPolicyStore(WorkspacePolicyPath).Current.MaxEntries);
        Assert.Equal(5000, new TrafficHistoryPolicyStore(GlobalPolicyPath).Current.MaxEntries);

        var restored = global.SwitchStorage(GlobalPolicyPath, TrafficHistoryPolicyStore.GlobalSource);
        Assert.Equal(TrafficHistoryPolicyStore.GlobalSource, global.PolicySource);
        Assert.Equal(5000, restored.MaxEntries);
    }

    [Fact]
    public void Switch_storage_to_missing_file_falls_back_to_defaults()
    {
        var store = new TrafficHistoryPolicyStore(GlobalPolicyPath);
        store.Update(new TrafficHistoryPolicy(9000, 64L * 1024 * 1024, 90, true));

        var switched = store.SwitchStorage(Path.Combine(_directory, "absent", "policy.json"), "workspace");

        Assert.Equal(2000, switched.MaxEntries);
        Assert.Equal(30, switched.RetentionDays);
        Assert.True(switched.AutoPrune);
        Assert.Equal("workspace", store.PolicySource);
    }

    [Fact]
    public void Statistics_and_cleanup_apply_the_active_workspace_policy()
    {
        using var persistence = new TrafficHistoryPersistence(Path.Combine(_directory, "history.json.gz"));
        var policies = new TrafficHistoryPolicyStore(GlobalPolicyPath);
        var store = new TrafficStore(persistence, policies);
        for (var index = 0; index < 5; index++) store.Import(Message(index));
        var manager = new TrafficHistoryManagementService(store, policies, persistence);

        Assert.Equal(TrafficHistoryPolicyStore.GlobalSource, manager.GetStatistics().PolicySource);
        Assert.Contains($"policySource={TrafficHistoryPolicyStore.GlobalSource}",
            HistoryManagementCommandRegistrar.Format(manager.GetStatistics()));

        SeedWorkspacePolicy(new TrafficHistoryPolicy(2000, 64L * 1024 * 1024, 365, true,
            [new TrafficSiteQuota("example.test", 1, 10L * 1024 * 1024 * 1024)]));
        policies.SwitchStorage(WorkspacePolicyPath, "workspace");

        var cleaned = manager.Cleanup();
        var statistics = manager.GetStatistics();

        Assert.Equal(4, cleaned.RemovedEntries);
        Assert.Equal(1, cleaned.RemainingEntries);
        Assert.Equal(1, statistics.EntryCount);
        Assert.Equal("workspace", statistics.PolicySource);
        Assert.Contains("policySource=workspace", HistoryManagementCommandRegistrar.Format(statistics));
    }

    private void SeedWorkspacePolicy(TrafficHistoryPolicy policy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WorkspacePolicyPath)!);
        var seed = new TrafficHistoryPolicyStore(WorkspacePolicyPath);
        seed.Update(policy);
    }

    private static TrafficMessage Message(int index) => new(
        "packet-" + index, "page", TrafficStage.Request, TrafficState.Continued, "GET",
        "https://example.test/" + index, [], [1, 2, 3], null, null, [], null,
        "Document", DateTimeOffset.UtcNow.AddMinutes(-index));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
