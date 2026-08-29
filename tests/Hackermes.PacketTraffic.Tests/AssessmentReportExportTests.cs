using Hackermes.Assessment;
using Hackermes.App;
using Hackermes.Base.Diagnostics;
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

public sealed class AssessmentReportExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hackermes-report-export-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecrets _secrets = new();

    public AssessmentReportExportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Export_is_offline_verifiable_and_supports_trusted_key_pin()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "report evidence");
        using var key = new TestKey();
        var exports = new AssessmentReportExportService(plane, key);

        var content = exports.Export(job.Id);
        var verification = exports.Verify(content);
        Assert.True(verification.Valid);
        Assert.Equal(key.KeyId, verification.KeyId);
        Assert.Equal(job.Id, verification.JobId);
        Assert.NotNull(verification.ExportedAt);

        var pinned = exports.Verify(content, key.KeyId);
        Assert.True(pinned.Valid);
        Assert.Null(pinned.ErrorCode);
    }

    [Fact]
    public async Task Verify_rejects_tampering_untrusted_keys_and_bad_input()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "tamper target");
        using var key = new TestKey();
        var exports = new AssessmentReportExportService(plane, key);
        var content = exports.Export(job.Id);

        var tampered = content.Replace("tamper target", "tampered target");
        Assert.NotEqual(content, tampered);
        Assert.Equal("invalid_signature", exports.Verify(tampered).ErrorCode);

        using var otherKey = new TestKey();
        Assert.Equal("untrusted_key", exports.Verify(content, otherKey.KeyId).ErrorCode);

        Assert.Equal("empty_content", exports.Verify("   ").ErrorCode);
        Assert.Equal("content_too_large", exports.Verify(new string('x', AssessmentReportExportService.MaximumContentBytes + 1)).ErrorCode);
        Assert.Equal("unsupported_version", exports.Verify("{\"Version\":2}").ErrorCode);
        Assert.Equal("invalid_document", exports.Verify(
            "{\"version\":1,\"algorithm\":\"ECDSA_P256_SHA256\",\"publicKey\":\"aaa\",\"signature\":\"bbb\"," +
            "\"payload\":{\"version\":1,\"keyId\":\"k\",\"exportedAt\":\"2026-01-01T00:00:00Z\",\"report\":{\"job\":null}}}").ErrorCode);
    }

    [Fact]
    public void Static_verification_needs_no_control_plane_or_private_key()
    {
        Assert.Equal("empty_content", AssessmentReportExportService.VerifyDocument("").ErrorCode);
    }

    [Fact]
    public async Task Archive_writes_human_report_with_poc_evidence_and_audit_under_data_root()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "hackermes-archive-" + Guid.NewGuid().ToString("N"));
        var oldRoot = Environment.GetEnvironmentVariable("HACKERMES_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_DATA_ROOT", dataRoot);
            var plane = CreatePlane();
            var job = await RunEchoAsync(plane, "archive evidence body");
            var evidence = plane.Evidence(job.Id).Single();
            plane.CreateFinding(job.Id, evidence.Id, "An issue", "Reproduced on authorized host",
                "High", "High", "analyst", "curl -sSI 'https://host/'");
            var archive = new AssessmentReportArchive(plane);

            var folder = archive.Archive(job.Id);

            Assert.Equal(Path.Combine(dataRoot, "reports", job.Id), folder);
            var report = File.ReadAllText(Path.Combine(folder, "report.md"));
            Assert.Contains("An issue", report, StringComparison.Ordinal);
            Assert.Contains("curl -sSI", report, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, "case.json")));
            Assert.True(File.Exists(Path.Combine(folder, "audit.json")));
            Assert.True(File.Exists(Path.Combine(folder, "evidence", "index.md")));
            var evidenceFile = Path.Combine(folder, "evidence", "01_simulation_echo.txt");
            Assert.True(File.Exists(evidenceFile));
            Assert.Contains("archive evidence body", File.ReadAllText(evidenceFile), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_DATA_ROOT", oldRoot);
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
    }

    private AssessmentControlPlane CreatePlane() => new(new SimulatedAssessmentExecutionHost(),
        new TestSettings(Path.Combine(_root, "settings.json")), new NullLogger(), _secrets);

    private static async Task<AssessmentJob> RunEchoAsync(AssessmentControlPlane plane, string content)
    {
        var scope = plane.CreateScope("loopback", "unit-test", "owner", ["127.0.0.1"], DateTimeOffset.UtcNow.AddMinutes(5));
        var plan = plane.CreatePlan(scope.Id, "evidence", [new AssessmentStep(AuthorizedToolCatalog.SimulationEcho, content)], "author");
        var approval = plane.Approve(plan.Id, "approver", DateTimeOffset.UtcNow.AddMinutes(5));
        return await plane.StartAsync(plan.Id, approval.Id, "runner");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }

    private sealed class TestSettings(string path) : ISettingsService
    {
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null) => true;
        public string SettingsFilePath => path;
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public void Set(string key, string? value) { if (value is null) _values.Remove(key); else _values[key] = value; }
        public bool Contains(string key) => _values.ContainsKey(key);
        public void Remove(string key) => _values.Remove(key);
    }

    private sealed class TestKey : IAssessmentReportSigningKey, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _publicKey;

        public TestKey() => _publicKey = _key.ExportSubjectPublicKeyInfo();

        public string Algorithm => AssessmentReportExportService.EcdsaP256Sha256;
        public string KeyId => AssessmentReportExportService.Fingerprint(_publicKey);
        public string PublicKey => Convert.ToBase64String(_publicKey);

        public byte[] Sign(byte[] canonicalPayload) =>
            _key.SignData(canonicalPayload, HashAlgorithmName.SHA256);

        public void Dispose() => _key.Dispose();
    }
}
