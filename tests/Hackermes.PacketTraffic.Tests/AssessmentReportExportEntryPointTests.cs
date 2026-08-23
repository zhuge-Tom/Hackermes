using Hackermes.Assessment;
using Hackermes.App;
using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Execution;
using Hackermes.Automation.Recording;
using Hackermes.Automation.Timeline;
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
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AssessmentReportExportEntryPointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hackermes-report-entry-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecrets _secrets = new();

    public AssessmentReportExportEntryPointTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Cli_exports_to_path_and_verifies_through_same_service()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "cli report evidence");
        using var key = new TestKey();
        var exports = new AssessmentReportExportService(plane, key);
        var registry = CreateCliRegistry(plane, exports);
        var path = Path.Combine(_root, "report.json").Replace('\\', '/');

        var exported = await registry.ExecuteAsync($"assessment report-export {path} {job.Id}", null);
        Assert.True(exported.Success, exported.Output);
        Assert.True(File.Exists(path));
        Assert.Contains(key.PublicKey[..20], File.ReadAllText(path));

        var verified = await registry.ExecuteAsync($"assessment report-verify {path}", null);
        Assert.True(verified.Success, verified.Output);
        Assert.Contains("valid=true", verified.Output);
        Assert.Contains($"job={job.Id}", verified.Output);

        var untrusted = await registry.ExecuteAsync($"assessment report-verify {path} deadbeef", null);
        Assert.False(untrusted.Success);
        Assert.Contains("untrusted_key", untrusted.Output);
    }

    [Fact]
    public async Task Agent_tools_have_expected_risk_and_never_accept_paths()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "agent report evidence");
        using var key = new TestKey();
        var exports = new AssessmentReportExportService(plane, key);
        var registry = new AiToolRegistry();
        AssessmentIntegrationModule.RegisterAgent(registry, plane, reports: exports);

        var export = registry.All.Single(tool => tool.Name == "assessment_report_export");
        var verify = registry.All.Single(tool => tool.Name == "assessment_report_verify");
        Assert.Equal(AiToolRisk.Dangerous, export.Risk);
        Assert.Equal(AiToolRisk.ReadOnly, verify.Risk);
        Assert.DoesNotContain("path", export.InputSchema.GetRawText());
        Assert.DoesNotContain("path", verify.InputSchema.GetRawText());

        var exported = await export.Handler(new ToolInvocation(export.Name,
            JsonSerializer.SerializeToElement(new { jobId = job.Id })), default);
        Assert.True(exported.Success, exported.Content);

        var verified = await verify.Handler(new ToolInvocation(verify.Name,
            JsonSerializer.SerializeToElement(new { content = exported.Content, expectedKeyId = key.KeyId })), default);
        Assert.True(verified.Success, verified.Content);
        Assert.Contains("\"Valid\":true", verified.Content);

        var rejected = await verify.Handler(new ToolInvocation(verify.Name,
            JsonSerializer.SerializeToElement(new { content = exported.Content.Replace("agent report evidence", "agent report evil") })), default);
        Assert.False(rejected.Success);
        Assert.Contains("invalid_signature", rejected.Content);
    }

    private CommandRegistry CreateCliRegistry(IAssessmentControlPlane plane, IAssessmentReportExportService exports)
    {
        var logger = new NullLogger();
        var timeline = new ActionTimelineStore();
        var executor = new ActionExecutor(new CdpSessionRegistry(logger), logger, timeline);
        var registry = new CommandRegistry(executor, new ActionRecorder(new EventBus(logger), executor, timeline), logger,
            timeline, new ActionPersistence());
        AssessmentIntegrationModule.RegisterCli(registry, plane, exports);
        return registry;
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
