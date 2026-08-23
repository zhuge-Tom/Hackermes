using Hackermes.App;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Execution;
using Hackermes.Automation.Packet;
using Hackermes.Automation.Recording;
using Hackermes.Automation.Timeline;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp.Session;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Local operator identity directory: the active profile name is what the traffic audit
/// chain stamps, with a graceful fallback when no profiles exist.
/// </summary>
public sealed class OperatorIdentityGovernanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hackermes-identity-" + Guid.NewGuid().ToString("N"));

    public OperatorIdentityGovernanceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Empty_directory_resolves_null_until_first_adoption()
    {
        var directory = NewDirectory();

        Assert.Null(directory.ResolveActiveName());
        Assert.Empty(directory.Identities);

        var identity = directory.Adopt("  alice ");

        Assert.Equal("alice", identity.Name);
        Assert.Equal("alice", directory.ResolveActiveName());
        Assert.Equal(identity.Id, directory.ActiveId);
    }

    [Fact]
    public void Adopt_is_idempotent_by_name_and_persists_across_reload()
    {
        var path = Path.Combine(_directory, "identities.json");
        var first = new OperatorIdentityDirectory(path);
        var original = first.Adopt("Alice");

        var second = new OperatorIdentityDirectory(path);
        var again = second.Adopt("alice");

        Assert.Equal(original.Id, again.Id);
        Assert.Single(second.Identities);
        Assert.Equal("Alice", second.ResolveActiveName());
    }

    [Fact]
    public void Use_switches_active_by_name_or_id_and_rejects_unknown()
    {
        var directory = NewDirectory();
        var alice = directory.Adopt("alice");
        directory.Adopt("bob");

        Assert.True(directory.Use("bob"));
        Assert.Equal("bob", directory.ResolveActiveName());
        Assert.True(directory.Use(alice.Id));
        Assert.Equal("alice", directory.ResolveActiveName());
        Assert.False(directory.Use("carol"));
        Assert.False(directory.Use("  "));
        Assert.Equal("alice", directory.ResolveActiveName());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\rname")]
    public void Names_follow_audit_identity_rules(string name) =>
        Assert.Throws<ArgumentException>(() => NewDirectory().Adopt(name));

    [Fact]
    public void Overlong_names_are_rejected() =>
        Assert.Throws<ArgumentException>(() => NewDirectory().Adopt(new string('x', OperatorIdentityDirectory.MaximumNameLength + 1)));

    [Fact]
    public void Audit_chain_stamps_active_profile_then_falls_back_to_settings()
    {
        var identities = NewDirectory();
        string? settingsOperator = "settings-operator";
        var trail = new PacketAuditTrail(Path.Combine(_directory, "audit.json"),
            () => identities.ResolveActiveName() ?? settingsOperator);

        trail.Record(Entry());
        Assert.Equal("settings-operator", trail.Query(new PacketAuditQuery(Limit: 1)).Single().Operator);

        identities.Adopt("alice");
        trail.Record(Entry());
        var entries = trail.Query(new PacketAuditQuery(Limit: 2)).ToArray();
        Assert.Equal("alice", entries[0].Operator);
        Assert.Equal("settings-operator", entries[1].Operator);
    }

    [Fact]
    public async Task Cli_lifecycle_lists_adopts_and_switches()
    {
        var registry = CreateRegistry(out var directory);

        var empty = await registry.ExecuteAsync("identity list", null);
        Assert.Contains("(fallback", empty.Output);

        Assert.True((await registry.ExecuteAsync("identity adopt alice", null)).Success);
        Assert.True((await registry.ExecuteAsync("identity adopt bob", null)).Success);
        var listing = (await registry.ExecuteAsync("identity list", null)).Output;
        Assert.Contains("* bob", listing);
        Assert.Contains("  alice", listing);
        Assert.Contains("resolved=bob", listing);

        Assert.True((await registry.ExecuteAsync("identity use alice", null)).Success);
        Assert.Contains("resolved=alice", (await registry.ExecuteAsync("identity list", null)).Output);

        Assert.False((await registry.ExecuteAsync("identity use carol", null)).Success);
        Assert.False((await registry.ExecuteAsync("identity adopt    ", null)).Success);
        Assert.NotNull(directory);
    }

    private CommandRegistry CreateRegistry(out OperatorIdentityDirectory directory)
    {
        var logger = new NullLogger();
        var timeline = new ActionTimelineStore();
        var executor = new ActionExecutor(new CdpSessionRegistry(logger), logger, timeline);
        var registry = new CommandRegistry(executor,
            new ActionRecorder(new EventBus(logger), executor, timeline), logger, timeline, new ActionPersistence());
        directory = NewDirectory();
        IdentityCommandRegistrar.Register(registry, directory);
        return registry;
    }

    private OperatorIdentityDirectory NewDirectory() =>
        new(Path.Combine(_directory, $"identities-{Guid.NewGuid():N}.json"));

    private static PacketAuditEntry Entry() => new(
        Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "test", PacketAuditOperation.Replay,
        "packet-1", "request",
        new PacketEditVersion(1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })).ToLowerInvariant(), null),
        new PacketEditVersion(2, Convert.ToHexString(SHA256.HashData(new byte[] { 2 })).ToLowerInvariant(), null),
        PacketAuditResult.Succeeded);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
