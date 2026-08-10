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
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AssessmentStage7CTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hackermes-stage7c-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecrets _secrets = new();

    public AssessmentStage7CTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Evidence_finding_review_and_reports_share_one_audited_job()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "warning from bounded simulation");
        var evidence = Assert.Single(plane.Evidence(job.Id));

        Assert.True(plane.VerifyEvidence(evidence.Id).Valid);
        var generated = Assert.Single(plane.Findings(job.Id));
        var finding = plane.CreateFinding(job.Id, evidence.Id, "Observed behavior", "Needs human review", "High", "Medium", "analyst");
        var reviewed = plane.ReviewFinding(finding.Id, AssessmentFindingStatus.Confirmed, "reviewer-1", "Reproduced in loopback evidence.");

        Assert.Equal("Confirmed", reviewed.Status);
        Assert.Equal("reviewer-1", reviewed.ReviewedBy);
        Assert.True(plane.VerifyAudit().Valid);
        Assert.Contains(plane.AuditForEntity(job.Id, 100), value => value.Action == "finding.review" && value.EntityId == finding.Id);
        Assert.Contains("Observed behavior", plane.ExportReport(job.Id, "json"));
        Assert.Contains("# Hackermes authorized assessment report", plane.ExportReport(job.Id, "markdown"));
        Assert.Contains("<!doctype html>", plane.ExportReport(job.Id, "html"));
        Assert.Equal(2, plane.Findings(job.Id).Count);
        Assert.Equal(AssessmentFindingStatus.Unreviewed.ToString(), generated.Status);
    }

    [Fact]
    public async Task Persisted_evidence_and_audit_detect_tampering_after_restart()
    {
        var first = CreatePlane();
        var job = await RunEchoAsync(first, "safe evidence");
        var evidence = Assert.Single(first.Evidence(job.Id));
        Assert.True(first.VerifyAudit().Valid);

        var path = Path.Combine(_root, "assessments.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Evidence"]!.AsArray()[0]!["Content"] = "tampered evidence";
        File.WriteAllText(path, json.ToJsonString());

        var reopened = CreatePlane();
        Assert.False(reopened.VerifyEvidence(evidence.Id).Valid);
        Assert.True(reopened.VerifyAudit().Valid);
    }

    [Fact]
    public async Task Audit_verification_detects_modified_attributed_actor()
    {
        var plane = CreatePlane();
        _ = await RunEchoAsync(plane, "ok");
        var path = Path.Combine(_root, "assessments.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Audit"]!.AsArray()[0]!["Actor"] = "different-actor";
        File.WriteAllText(path, json.ToJsonString());

        var reopened = CreatePlane();
        var verification = reopened.VerifyAudit();
        Assert.False(verification.Valid);
        Assert.Equal("entry_hash_mismatch", verification.ErrorCode);
    }

    [Fact]
    public async Task Audit_chain_cannot_be_recomputed_after_protected_key_rotation()
    {
        var plane = CreatePlane();
        _ = await RunEchoAsync(plane, "ok");
        Assert.True(plane.VerifyAudit().Valid);

        var reopenedWithDifferentKey = new AssessmentControlPlane(new SimulatedAssessmentExecutionHost(),
            new TestSettings(Path.Combine(_root, "settings.json")), new NullLogger(), new MemorySecrets());

        Assert.False(reopenedWithDifferentKey.VerifyAudit().Valid);
        Assert.Equal("entry_hash_mismatch", reopenedWithDifferentKey.VerifyAudit().ErrorCode);
    }

    [Fact]
    public async Task Corrupt_primary_store_recovers_last_known_good_backup()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "recoverable evidence");
        var path = Path.Combine(_root, "assessments.json");
        File.Copy(path, path + ".bak", true);
        File.WriteAllText(path, "{ truncated");

        var reopened = CreatePlane();

        Assert.Contains(reopened.Jobs, value => value.Id == job.Id);
        Assert.True(reopened.VerifyAudit().Valid);
        Assert.NotEmpty(Directory.GetFiles(_root, "assessments.json.corrupt-*"));
        Assert.NotEqual("{ truncated", File.ReadAllText(path));
    }

    [Fact]
    public async Task Interrupted_job_is_failed_and_audited_after_restart()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "completed before simulated crash");
        var path = Path.Combine(_root, "assessments.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var persistedJob = json["Jobs"]!.AsArray().Single(value => value!["Id"]!.GetValue<string>() == job.Id)!;
        persistedJob["Status"] = nameof(AssessmentJobStatus.Running);
        persistedJob["FinishedAt"] = null;
        File.WriteAllText(path, json.ToJsonString());

        var reopened = CreatePlane();
        var recovered = Assert.Single(reopened.Jobs, value => value.Id == job.Id);

        Assert.Equal(AssessmentJobStatus.Failed, recovered.Status);
        Assert.Contains("stopped before", recovered.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(reopened.AuditForEntity(job.Id), value => value.Action == "job.recover");
        Assert.True(reopened.VerifyAudit().Valid);
    }

    [Fact]
    public async Task Cli_entrypoint_completes_scope_plan_approval_evidence_finding_review_and_report()
    {
        var plane = CreatePlane();
        var logger = new NullLogger();
        var timeline = new ActionTimelineStore();
        var executor = new ActionExecutor(new CdpSessionRegistry(logger), logger, timeline);
        var registry = new CommandRegistry(executor, new ActionRecorder(new EventBus(logger), executor, timeline), logger,
            timeline, new ActionPersistence());
        AssessmentIntegrationModule.RegisterCli(registry, plane);

        Assert.True((await registry.ExecuteAsync("assessment scope create loopback ticket owner 127.0.0.1 5", null)).Success);
        var scope = Assert.Single(plane.Scopes);
        Assert.True((await registry.ExecuteAsync($"assessment plan create {scope.Id} cli-plan simulation.echo 10 cli-evidence", null)).Success);
        var plan = Assert.Single(plane.Plans);
        Assert.True((await registry.ExecuteAsync($"assessment approve {plan.Id} approver 5", null)).Success);
        var approval = Assert.Single(plane.Approvals);
        Assert.True((await registry.ExecuteAsync($"assessment run {plan.Id} {approval.Id} runner", null)).Success);
        var job = Assert.Single(plane.Jobs);
        var evidence = Assert.Single(plane.Evidence(job.Id));
        Assert.True((await registry.ExecuteAsync($"assessment evidence-verify {evidence.Id}", null)).Success);
        Assert.True((await registry.ExecuteAsync($"assessment finding create {job.Id} {evidence.Id} cli-finding High High description", null)).Success);
        var finding = Assert.Single(plane.Findings(job.Id));
        Assert.True((await registry.ExecuteAsync($"assessment finding review {finding.Id} Confirmed reviewer verified", null)).Success);
        var report = await registry.ExecuteAsync($"assessment report {job.Id} markdown", null);

        Assert.True(report.Success);
        Assert.Contains("cli-finding", report.Output);
        Assert.True(plane.VerifyAudit().Valid);
    }

    [Fact]
    public async Task Agent_entrypoint_completes_the_same_bounded_control_plane_chain()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        AssessmentIntegrationModule.RegisterAgent(registry, plane);

        Assert.True((await InvokeAgent(registry, "assessment_create_scope", new
        {
            name = "agent-loopback", authorization = "agent-ticket", operatorId = "agent-owner",
            targets = new[] { "127.0.0.1" }, minutes = 5
        })).Success);
        var scope = Assert.Single(plane.Scopes);
        Assert.True((await InvokeAgent(registry, "assessment_create_plan", new
        {
            scopeId = scope.Id, name = "agent-plan", adapterId = AuthorizedToolCatalog.SimulationEcho,
            input = "agent-evidence", timeoutSeconds = 10
        })).Success);
        var plan = Assert.Single(plane.Plans);
        Assert.True((await InvokeAgent(registry, "assessment_approve", new { planId = plan.Id, operatorId = "agent-approver", minutes = 5 })).Success);
        var approval = Assert.Single(plane.Approvals);
        Assert.True((await InvokeAgent(registry, "assessment_run", new { planId = plan.Id, approvalId = approval.Id, operatorId = "agent-runner" })).Success);
        var job = Assert.Single(plane.Jobs);
        var evidence = Assert.Single(plane.Evidence(job.Id));
        Assert.True((await InvokeAgent(registry, "assessment_verify_evidence", new { evidenceId = evidence.Id })).Success);
        Assert.True((await InvokeAgent(registry, "assessment_create_finding", new
        {
            jobId = job.Id, evidenceId = evidence.Id, title = "agent-finding", description = "bounded evidence",
            severity = "Medium", confidence = "High"
        })).Success);
        var finding = Assert.Single(plane.Findings(job.Id));
        Assert.True((await InvokeAgent(registry, "assessment_review_finding", new
        {
            findingId = finding.Id, status = "Confirmed", reviewer = "human-reviewer", note = "reviewed"
        })).Success);
        var report = await InvokeAgent(registry, "assessment_report", new { jobId = job.Id, format = "json" });

        Assert.True(report.Success);
        Assert.Contains("agent-finding", report.Content);
        Assert.True((await InvokeAgent(registry, "assessment_verify_audit", new { })).Success);
    }

    private static async ValueTask<ToolResult> InvokeAgent(AiToolRegistry registry, string name, object arguments)
    {
        Assert.True(registry.TryGet(name, out var definition));
        return await definition!.Handler(new ToolInvocation(name, JsonSerializer.SerializeToElement(arguments)), default);
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
}
