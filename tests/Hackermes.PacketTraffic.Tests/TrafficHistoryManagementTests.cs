using Hackermes.Automation.Commands;
using Hackermes.Automation.Traffic;
using Hackermes.Traffic.History;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficHistoryManagementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hackermes-history-management-tests", Guid.NewGuid().ToString("N"));
    private string PolicyPath => Path.Combine(_directory, "policy.json");
    private string HistoryPath => Path.Combine(_directory, "history.json.gz");

    [Fact]
    public void Policy_is_bounded_atomic_and_recovers_from_backup()
    {
        var policies = new TrafficHistoryPolicyStore(PolicyPath);
        var safe = policies.Update(new TrafficHistoryPolicy(1, 1, 0, false));
        Assert.Equal(100, safe.MaxEntries);
        Assert.Equal(16L * 1024 * 1024, safe.MaxStorageBytes);
        Assert.Equal(1, safe.RetentionDays);
        policies.Update(new TrafficHistoryPolicy(250, 32L * 1024 * 1024, 7, true));
        var before = File.ReadAllBytes(PolicyPath);

        using (var locked = new FileStream(PolicyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => policies.Update(new TrafficHistoryPolicy(500, 64L * 1024 * 1024, 14, true)));
        Assert.Equal(250, policies.Current.MaxEntries);
        Assert.Equal(before, File.ReadAllBytes(PolicyPath));

        File.WriteAllText(PolicyPath, "{broken");
        var recovered = new TrafficHistoryPolicyStore(PolicyPath);
        Assert.Equal(100, recovered.Current.MaxEntries);
        Assert.False(recovered.Current.AutoPrune);
    }

    [Fact]
    public void Cleanup_and_statistics_apply_same_policy_used_after_restart()
    {
        using var persistence = new TrafficHistoryPersistence(HistoryPath);
        var policies = new TrafficHistoryPolicyStore(PolicyPath);
        policies.Update(new TrafficHistoryPolicy(100, 64L * 1024 * 1024, 365, false));
        var store = new TrafficStore(persistence, policies);
        for (var index = 0; index < 101; index++) store.Import(Message(index));
        var manager = new TrafficHistoryManagementService(store, policies, persistence);

        var preview = manager.PreviewCleanup();
        var cleaned = manager.Cleanup();
        var statistics = manager.GetStatistics();

        Assert.Equal(1, preview.RemovedEntries);
        Assert.Equal(preview, cleaned);
        Assert.Equal(100, statistics.EntryCount);
        Assert.True(statistics.EstimatedContentBytes > 0);
        Assert.Equal(100, new TrafficStore(persistence, policies).Read(1000).Count);
    }

    [Fact]
    public async Task Cli_exposes_stats_policy_preview_cleanup_and_clear()
    {
        using var persistence = new TrafficHistoryPersistence(HistoryPath);
        var policies = new TrafficHistoryPolicyStore(PolicyPath);
        var store = new TrafficStore(persistence, policies);
        store.Import(Message(1));
        var manager = new TrafficHistoryManagementService(store, policies, persistence);

        Assert.True((await Execute(manager, "stats")).Success);
        Assert.True((await Execute(manager, "set 100 16777216 10 false")).Success);
        Assert.Contains("removed=", (await Execute(manager, "preview")).Output);
        Assert.True((await Execute(manager, "cleanup")).Success);
        Assert.True((await Execute(manager, "clear")).Success);
        Assert.Equal(0, manager.GetStatistics().EntryCount);
    }

    [Fact]
    public async Task Site_quota_prunes_oldest_matching_host_and_round_trips_through_cli()
    {
        using var persistence = new TrafficHistoryPersistence(HistoryPath);
        var policies = new TrafficHistoryPolicyStore(PolicyPath);
        var store = new TrafficStore(persistence, policies);
        for (var index = 0; index < 5; index++) store.Import(Message(index) with
        {
            Id = "api-" + index, Url = "https://api.example.test/" + index
        });
        store.Import(Message(20) with { Id = "other", Url = "https://other.test/" });
        var manager = new TrafficHistoryManagementService(store, policies, persistence);

        var configured = await Execute(manager, "site-set *.example.test 2 1048576");
        Assert.True(configured.Success);
        Assert.Contains("site=*.example.test", configured.Output);
        Assert.Equal(3, manager.GetStatistics().EntryCount);
        Assert.NotNull(store.Get("api-0"));
        Assert.NotNull(store.Get("api-1"));
        Assert.NotNull(store.Get("other"));

        Assert.True((await Execute(manager, "site-remove *.example.test")).Success);
        Assert.Empty(manager.Policy.SiteQuotas ?? []);
        Assert.Empty(new TrafficHistoryPolicyStore(PolicyPath).Current.SiteQuotas ?? []);
    }

    private static Task<CommandResult> Execute(ITrafficHistoryManagementService service, string arguments) =>
        HistoryManagementCommandRegistrar.ExecuteAsync(service, new CommandContext
        {
            Args = CommandLineParser.Tokenize(arguments), PageId = null,
            RawInput = "traffic-history " + arguments, RawArguments = arguments
        });

    private static TrafficMessage Message(int index) => new(
        "packet-" + index, "page", TrafficStage.Request, TrafficState.Continued, "GET",
        "https://example.test/" + index, [], [1, 2, 3], null, null, [], null,
        "Document", DateTimeOffset.UtcNow.AddMinutes(-index));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
