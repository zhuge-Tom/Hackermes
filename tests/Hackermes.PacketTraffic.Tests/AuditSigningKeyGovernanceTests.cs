using Hackermes.App;
using Hackermes.Assessment;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Execution;
using Hackermes.Automation.Recording;
using Hackermes.Automation.Timeline;
using Hackermes.Automation.Packet;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Governance of the shared ECDSA signing identity: adoption switches verification into
/// allowlist mode, rotation retires (but keeps verifiable) the previous generation, and
/// revocation rejects historical documents even with valid signatures.
/// </summary>
public sealed class AuditSigningKeyGovernanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hackermes-key-governance-" + Guid.NewGuid().ToString("N"));

    public AuditSigningKeyGovernanceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Legacy_mode_allows_any_key_until_adopted()
    {
        var trust = new AuditKeyTrustFile(Path.Combine(_directory, "trust.json"));
        var policy = new PacketAuditTrustPolicy(trust);

        Assert.False(trust.TrustFileExists);
        Assert.True(policy.IsTrusted("any-key-id-whatsoever"));
    }

    [Fact]
    public void Adopt_creates_allowlist_and_rejects_unknown_keys()
    {
        using var key = NewKey();
        var trust = AdoptedTrust(key);

        Assert.True(trust.TrustFileExists);
        Assert.True(trust.IsAllowed(key.KeyId));
        Assert.False(trust.IsAllowed(new string('a', 64)));
        // Reload from disk: the allowlist survives restarts.
        var reloaded = new AuditKeyTrustFile(Path.Combine(_directory, "trust.json"));
        Assert.True(reloaded.IsAllowed(key.KeyId));
    }

    [Fact]
    public void Rotate_switches_signing_and_keeps_retired_documents_verifiable()
    {
        using var key = NewKey();
        var trust = AdoptedTrust(key);
        var trail = NewTrail();
        SeedAudit(trail);
        var exports = new PacketAuditExportService(trail, key, new PacketAuditTrustPolicy(trust));

        var before = exports.Export(new PacketAuditQuery(Limit: 10));
        var beforeKeyId = key.KeyId;
        key.Rotate(trust, "scheduled rotation");

        Assert.NotEqual(beforeKeyId, key.KeyId);
        Assert.Equal(beforeKeyId, ParseKeyId(before));
        var after = exports.Export(new PacketAuditQuery(Limit: 10));
        Assert.Equal(key.KeyId, ParseKeyId(after));

        Assert.True(exports.Verify(before).Valid, "retired generation must stay verifiable");
        Assert.True(exports.Verify(after).Valid);

        var generations = trust.Generations;
        Assert.Equal(2, generations.Count);
        Assert.Equal(AuditKeyStatus.Retired, generations.Single(entry => entry.KeyId == beforeKeyId).Status);
        Assert.Equal(AuditKeyStatus.Trusted, generations.Single(entry => entry.KeyId == key.KeyId).Status);
    }

    [Fact]
    public void Revoke_rejects_historical_documents_but_not_new_ones()
    {
        using var key = NewKey();
        var trust = AdoptedTrust(key);
        var trail = NewTrail();
        SeedAudit(trail);
        var exports = new PacketAuditExportService(trail, key, new PacketAuditTrustPolicy(trust));

        var historical = exports.Export(new PacketAuditQuery(Limit: 10));
        var historicalKeyId = key.KeyId;
        key.Rotate(trust, null);
        Assert.True(trust.Revoke(historicalKeyId, "suspected compromise"));

        var revoked = exports.Verify(historical);
        Assert.False(revoked.Valid);
        Assert.Equal("untrusted_key", revoked.ErrorCode);

        var fresh = exports.Export(new PacketAuditQuery(Limit: 10));
        Assert.True(exports.Verify(fresh).Valid);
    }

    [Fact]
    public void Foreign_signed_document_rejected_under_allowlist_policy()
    {
        using var key = NewKey();
        using var foreign = NewKey();
        var trust = AdoptedTrust(key);
        var policy = new PacketAuditTrustPolicy(trust);

        var foreignExports = new PacketAuditExportService(NewTrail(), foreign);
        var foreignDocument = foreignExports.Export(new PacketAuditQuery(Limit: 10));

        var localExports = new PacketAuditExportService(NewTrail(), key, policy);
        var verification = localExports.Verify(foreignDocument);

        Assert.False(verification.Valid);
        Assert.Equal("untrusted_key", verification.ErrorCode);
        // Without the policy the same document stays cryptographically verifiable.
        Assert.True(foreignExports.Verify(foreignDocument).Valid);
    }

    [Fact]
    public void Rotate_requires_prior_adoption()
    {
        using var key = NewKey();
        var trust = new AuditKeyTrustFile(Path.Combine(_directory, "absent-trust.json"));

        Assert.Throws<InvalidOperationException>(() => key.Rotate(trust, null));
        Assert.Equal(0, trust.Generations.Count);
    }

    [Fact]
    public async Task Assessment_report_verify_applies_trust_policy_overlay()
    {
        var plane = NewPlane();
        var job = await RunEchoJobAsync(plane);
        using var key = NewKey();
        var trust = AdoptedTrust(key);
        var policy = new PacketAuditTrustPolicy(trust);
        var exports = new AssessmentReportExportService(plane, key, policy);

        var content = exports.Export(job.Id);
        Assert.True(exports.Verify(content).Valid);
        // The offline static path is unaffected by the local policy.
        Assert.True(AssessmentReportExportService.VerifyDocument(content).Valid);

        Assert.True(trust.Revoke(key.KeyId, "rotate immediately"));
        var revoked = exports.Verify(content);
        Assert.False(revoked.Valid);
        Assert.Equal("untrusted_key", revoked.ErrorCode);
    }

    [Fact]
    public async Task Cli_governance_commands_drive_the_full_lifecycle()
    {
        using var key = NewKey();
        var trust = new AuditKeyTrustFile(Path.Combine(_directory, "cli-trust.json"));
        var logger = new NullLogger();
        var timeline = new ActionTimelineStore();
        var executor = new ActionExecutor(new CdpSessionRegistry(logger), logger, timeline);
        var registry = new CommandRegistry(executor, new ActionRecorder(new EventBus(logger), executor, timeline), logger,
            timeline, new ActionPersistence());
        SigningKeysCommandRegistrar.Register(registry, key, trust);

        var legacyList = await registry.ExecuteAsync("signing-keys list", null);
        Assert.True(legacyList.Success);
        Assert.Contains("policy=legacy-pinning", legacyList.Output);

        Assert.True((await registry.ExecuteAsync("signing-keys adopt initial", null)).Success);
        Assert.Contains("policy=allowlist", (await registry.ExecuteAsync("signing-keys list", null)).Output);

        Assert.True((await registry.ExecuteAsync("signing-keys rotate scheduled", null)).Success);
        var listing = (await registry.ExecuteAsync("signing-keys list", null)).Output;
        Assert.Contains("status=retired", listing);
        Assert.Contains("status=trusted", listing);

        var previousKeyId = trust.Generations.Single(entry => entry.Status == AuditKeyStatus.Retired).KeyId;
        Assert.True((await registry.ExecuteAsync($"signing-keys revoke {previousKeyId} compromised", null)).Success);
        Assert.Contains("status=revoked", (await registry.ExecuteAsync("signing-keys list", null)).Output);

        Assert.False((await registry.ExecuteAsync("signing-keys bogus", null)).Success);
    }

    private PacketAuditSigningKey NewKey() => new(new MemorySecrets());

    private AuditKeyTrustFile AdoptedTrust(PacketAuditSigningKey key)
    {
        var trust = new AuditKeyTrustFile(Path.Combine(_directory, $"trust-{Guid.NewGuid():N}.json"));
        trust.RecordInitialGeneration(key.KeyId, key.PublicKey, "test adoption");
        return trust;
    }

    private PacketAuditTrail NewTrail() =>
        new(Path.Combine(_directory, $"audit-{Guid.NewGuid():N}.json"), () => "tester");

    private static void SeedAudit(PacketAuditTrail trail) => trail.Record(new PacketAuditEntry(
        Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "test", PacketAuditOperation.Replay,
        "packet-1", "request", Version(11), Version(22), PacketAuditResult.Succeeded));

    private static PacketEditVersion Version(long length) =>
        new(length, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })).ToLowerInvariant(), null);

    private static string ParseKeyId(string document)
    {
        using var json = System.Text.Json.JsonDocument.Parse(document);
        return json.RootElement.GetProperty("payload").GetProperty("keyId").GetString()!;
    }

    private AssessmentControlPlane NewPlane() =>
        new(new SimulatedAssessmentExecutionHost(),
            new StaticSettings(Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json")), new NullLogger());

    private static async Task<AssessmentJob> RunEchoJobAsync(AssessmentControlPlane plane)
    {
        var scope = plane.CreateScope("loopback", "test", "tester", ["127.0.0.1"], DateTimeOffset.UtcNow.AddMinutes(5));
        var plan = plane.CreatePlan(scope.Id, "echo", [new AssessmentStep(AuthorizedToolCatalog.SimulationEcho, "ok")], "tester");
        var approval = plane.Approve(plan.Id, "approver", DateTimeOffset.UtcNow.AddMinutes(5));
        return await plane.StartAsync(plan.Id, approval.Id, "tester");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public void Set(string key, string? value) { if (value is null) _values.Remove(key); else _values[key] = value; }
        public bool Contains(string key) => _values.ContainsKey(key);
        public void Remove(string key) => _values.Remove(key);
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }

    private sealed class StaticSettings(string path) : ISettingsService
    {
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null)
        {
            mutate(Load());
            return true;
        }
        public string SettingsFilePath => path;
    }
}
