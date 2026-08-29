using Hackermes.Assessment;
using Hackermes.App;
using Hackermes.App.Views;
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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AssessmentStage7CTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hackermes-stage7c-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecrets _secrets = new();

    public AssessmentStage7CTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("*", "anything.example", true)]
    [InlineData("*.ys7.com", "ys7.com", true)]
    [InlineData("*.ys7.com", "www.ys7.com", true)]
    [InlineData("*.ys7.com", "api.cloud.ys7.com", true)]
    [InlineData("*.ys7.com", "ys7.com.evil.test", false)]
    [InlineData("*.ys7.com", "notys7.com", false)]
    [InlineData("www.ys7.com", "www.ys7.com", true)]
    [InlineData("www.ys7.com", "api.ys7.com", false)]
    public void Wildcard_and_exact_scopes_authorize_only_in_range_hosts(string allowed, string host, bool expected)
    {
        Assert.Equal(expected, AuthorizedToolCatalog.IsTargetInScope(host, [allowed]));
    }

    [Fact]
    public void CreateScope_accepts_a_platform_wildcard_domain()
    {
        var plane = CreatePlane();
        var scope = plane.CreateScope("萤石", "platform", "owner", ["*.ys7.com"], DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(["*.ys7.com"], scope.Targets);
    }

    [Fact]
    public void Unrestricted_scope_can_be_created_without_entering_a_domain()
    {
        var selection = AssessmentWorkspaceView.PrepareQuickAuthorization(true, string.Empty);

        Assert.Equal(["*"], selection.ScopeTargets);
        Assert.Empty(selection.ExecutionEndpoints);
        Assert.Throws<ArgumentException>(() =>
            AssessmentWorkspaceView.PrepareQuickAuthorization(false, string.Empty));
    }

    [Fact]
    public void Authorize_all_without_a_target_defers_execution_to_the_agent()
    {
        var selection = AssessmentWorkspaceView.PrepareQuickAuthorization(true, string.Empty);

        Assert.False(AssessmentWorkspaceView.HasExecutionTarget(selection));
        Assert.Equal(["*"], selection.ScopeTargets);
    }

    [Fact]
    public async Task Workspace_isolates_jobs_until_they_are_moved()
    {
        var plane = CreatePlane();
        var workspace = plane.CreateWorkspace("补天");
        var first = await RunEchoAsync(plane, "default-job");
        var second = await RunEchoAsync(plane, "moved-job");

        Assert.True(plane.AssignJobWorkspace(second.Id, workspace.Id));
        var isolated = plane.ReadCasesInWorkspace(workspace.Id);
        var defaults = plane.ReadCasesInWorkspace(string.Empty);

        Assert.Equal(second.Id, Assert.Single(isolated).Job.Id);
        Assert.Contains(defaults, value => value.Job.Id == first.Id);
        Assert.DoesNotContain(defaults, value => value.Job.Id == second.Id);
        Assert.True(plane.VerifyAudit().Valid);
    }

    [Fact]
    public void Complete_grant_creates_a_completed_job_without_running_tools()
    {
        var plane = CreatePlane();
        var scope = plane.CreateScope(string.Empty, string.Empty, string.Empty, ["*"],
            DateTimeOffset.UtcNow.AddMinutes(5));

        var job = plane.CompleteGrant(scope.Id, $"{scope.Name} · 授权确认", scope.OperatorId, "授权已确认。");

        Assert.Equal(AssessmentJobStatus.Completed, job.Status);
        Assert.Equal(job.Id, Assert.Single(plane.ReadCases()).Job.Id);
        Assert.Equal("授权已确认。", Assert.Single(plane.Evidence(job.Id)).Content);
        Assert.True(plane.VerifyAudit().Valid);
    }

    [Fact]
    public void Confirm_and_run_accepts_a_concrete_target_with_unrestricted_scope()
    {
        var selection = AssessmentWorkspaceView.PrepareQuickAuthorization(true, "https://Example.COM:8443/path");

        Assert.True(AssessmentWorkspaceView.HasExecutionTarget(selection));
        Assert.Equal(["*"], selection.ScopeTargets);
        var endpoint = Assert.Single(selection.ExecutionEndpoints);
        Assert.Equal("example.com", endpoint.Target);
        Assert.Equal("https", endpoint.Scheme);
        Assert.Equal(8443, endpoint.Port);
    }

    [Fact]
    public async Task Begin_surfaces_the_job_before_toolhost_finishes()
    {
        var host = new GateHost();
        var plane = new AssessmentControlPlane(host, new TestSettings(Path.Combine(_root, "settings.json")),
            new NullLogger(), _secrets);
        var scope = plane.CreateScope("loopback", "unit-test", "owner", ["127.0.0.1"], DateTimeOffset.UtcNow.AddMinutes(5));
        var plan = plane.CreatePlan(scope.Id, "gated",
            [new AssessmentStep(AuthorizedToolCatalog.SimulationEcho, "pending")], "author");
        var approval = plane.Approve(plan.Id, "approver", DateTimeOffset.UtcNow.AddMinutes(5));

        var run = plane.Begin(plan.Id, approval.Id, "runner");
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(run.Completion.IsCompleted);
        Assert.Contains(run.Job.Id, plane.ReadCases().Select(value => value.Job.Id));
        Assert.Equal(AssessmentJobStatus.Running, plane.Jobs.Single(value => value.Id == run.Job.Id).Status);

        host.Gate.SetResult(new AssessmentExecutionResult(true, "pending"));
        var finished = await run.Completion;

        Assert.Equal(AssessmentJobStatus.Completed, finished.Status);
        Assert.Equal("pending", Assert.Single(plane.Evidence(finished.Id)).Content);
    }

    [Fact]
    public async Task Hide_job_removes_it_from_read_cases_and_keeps_the_audit_chain()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "hide-me");

        Assert.True(plane.HideJob(job.Id, "owner"));
        Assert.False(plane.HideJob(job.Id, "owner"));
        Assert.DoesNotContain(plane.ReadCases(), value => value.Job.Id == job.Id);
        Assert.True(plane.Jobs.Single(value => value.Id == job.Id).Hidden);
        Assert.True(plane.VerifyAudit().Valid);
        Assert.Contains(plane.AuditForEntity(job.Id), entry => entry.Action == "job.hide");
    }

    [Fact]
    public async Task Hide_finished_jobs_clears_completed_cases_from_the_workbench()
    {
        var plane = CreatePlane();
        var first = await RunEchoAsync(plane, "one");
        var second = await RunEchoAsync(plane, "two");

        Assert.Equal(2, plane.HideFinishedJobs("owner"));
        Assert.Equal(0, plane.HideFinishedJobs("owner"));
        Assert.Empty(plane.ReadCases());
        Assert.True(plane.Jobs.Single(value => value.Id == first.Id).Hidden);
        Assert.True(plane.Jobs.Single(value => value.Id == second.Id).Hidden);
        Assert.True(plane.VerifyAudit().Valid);
    }

    [Fact]
    public void All_authorized_scope_can_omit_name_authorization_reference_and_operator()
    {
        var plane = CreatePlane();

        var scope = plane.CreateScope(string.Empty, string.Empty, string.Empty, ["*"],
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal("全部范围", scope.Name);
        Assert.Equal("系统确认", scope.AuthorizationReference);
        Assert.Equal("system", scope.OperatorId);
        Assert.Equal(["*"], scope.Targets);
        Assert.Contains(plane.AuditForEntity(scope.Id), entry =>
            entry.Action == "scope.create" && entry.Actor == "system");
    }

    [Fact]
    public void Exact_scope_still_requires_a_name_but_not_authorization_reference_or_operator()
    {
        var plane = CreatePlane();

        var scope = plane.CreateScope("loopback", string.Empty, string.Empty, ["127.0.0.1"],
            DateTimeOffset.UtcNow.AddMinutes(5));

        var error = Assert.Throws<ArgumentException>(() => plane.CreateScope(
            string.Empty, string.Empty, string.Empty, ["127.0.0.1"], DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal("系统确认", scope.AuthorizationReference);
        Assert.Equal("system", scope.OperatorId);
        Assert.Contains("Scope name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://Example.COM:8443/path?q=1", "example.com", "https", 8443)]
    [InlineData("example.com", "example.com", "http", 80)]
    public void Quick_run_target_accepts_a_domain_or_url_without_falling_back_to_loopback(
        string input, string target, string scheme, int port)
    {
        var endpoint = AssessmentWorkspaceView.AssessmentTargetEndpoint.Parse(input);

        Assert.Equal(target, endpoint.Target);
        Assert.Equal(scheme, endpoint.Scheme);
        Assert.Equal(port, endpoint.Port);
    }

    [Fact]
    public async Task Best_effort_plan_records_a_failed_tool_and_continues_remaining_tools()
    {
        var plane = new AssessmentControlPlane(new BestEffortHost(),
            new TestSettings(Path.Combine(_root, "settings.json")), new NullLogger(), _secrets);
        var scope = plane.CreateScope(string.Empty, string.Empty, string.Empty, ["*"],
            DateTimeOffset.UtcNow.AddMinutes(5));
        var plan = plane.CreatePlan(scope.Id, "全部工具",
        [
            new AssessmentStep(AuthorizedToolCatalog.SimulationEcho, "fail", ContinueOnError: true),
            new AssessmentStep(AuthorizedToolCatalog.SimulationEcho, "pass", ContinueOnError: true)
        ], "system");
        var approval = plane.Approve(plan.Id, "system", DateTimeOffset.UtcNow.AddMinutes(5));

        var job = await plane.StartAsync(plan.Id, approval.Id, "system");

        Assert.Equal(AssessmentJobStatus.CompletedWithWarnings, job.Status);
        var evidence = plane.Evidence(job.Id);
        Assert.Equal(2, evidence.Count);
        Assert.Contains(evidence, item => item.Content.Contains("warning: expected failure", StringComparison.Ordinal));
        Assert.Contains(evidence, item => item.Content == "pass");
    }

    [Fact]
    public async Task Missing_security_headers_recon_completes_and_archives_low_findings()
    {
        var plane = new AssessmentControlPlane(new ReconHeadersHost(),
            new TestSettings(Path.Combine(_root, "settings.json")), new NullLogger(), _secrets);
        var scope = plane.CreateScope("loopback", "unit-test", "owner", ["127.0.0.1"], DateTimeOffset.UtcNow.AddMinutes(5));
        var plan = plane.CreatePlan(scope.Id, "headers",
            [new AssessmentStep(AuthorizedToolCatalog.HttpHeadersProbe, """{"target":"127.0.0.1","scheme":"http","port":80,"path":"/"}""")],
            "author");
        var approval = plane.Approve(plan.Id, "approver", DateTimeOffset.UtcNow.AddMinutes(5));

        var job = await plane.StartAsync(plan.Id, approval.Id, "runner");

        Assert.Equal(AssessmentJobStatus.Completed, job.Status);
        var findings = plane.Findings(job.Id);
        Assert.NotEmpty(findings);
        var allowed = new[] { "Critical", "High", "Medium", "Low", "Info" };
        Assert.All(findings, value => Assert.True(allowed.Contains(value.Severity), $"Unexpected severity: {value.Severity}"));
    }

    [Fact]
    public async Task Finding_accepts_a_poc_and_the_report_renders_it()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "poc evidence");
        var evidence = Assert.Single(plane.Evidence(job.Id));
        var finding = plane.CreateFinding(job.Id, evidence.Id, "An issue", "Reproduced on authorized host",
            "High", "High", "analyst", "curl -sSI 'https://host/' -> Location: http://host/");

        Assert.Equal("curl -sSI 'https://host/' -> Location: http://host/", finding.PoC);
        var markdown = plane.ExportReport(job.Id, "markdown");
        Assert.Contains("curl -sSI", markdown, StringComparison.Ordinal);
        var html = plane.ExportReport(job.Id, "html");
        Assert.Contains("curl -sSI", html, StringComparison.Ordinal);
        var json = plane.ExportReport(job.Id, "json");
        Assert.Contains("curl -sSI", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_finding_review_and_reports_share_one_audited_job()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "bounded simulation output");
        var evidence = Assert.Single(plane.Evidence(job.Id));

        Assert.True(plane.VerifyEvidence(evidence.Id).Valid);
        Assert.Empty(plane.Findings(job.Id));
        var finding = plane.CreateFinding(job.Id, evidence.Id, "Observed behavior", "Needs human review", "High", "Medium", "analyst");
        var reviewed = plane.ReviewFinding(finding.Id, AssessmentFindingStatus.Confirmed, "reviewer-1", "Reproduced in loopback evidence.");

        Assert.Equal("Confirmed", reviewed.Status);
        Assert.Equal("reviewer-1", reviewed.ReviewedBy);
        Assert.True(plane.VerifyAudit().Valid);
        Assert.Contains(plane.AuditForEntity(job.Id, 100), value => value.Action == "finding.review" && value.EntityId == finding.Id);
        Assert.Contains("Observed behavior", plane.ExportReport(job.Id, "json"));
        Assert.Contains("# Hackermes authorized assessment report", plane.ExportReport(job.Id, "markdown"));
        Assert.Contains("<!doctype html>", plane.ExportReport(job.Id, "html"));
        Assert.Equal(AssessmentFindingStatus.Confirmed.ToString(), Assert.Single(plane.Findings(job.Id)).Status);
    }

    [Fact]
    public async Task Case_snapshot_exposes_one_coherent_lifecycle_and_only_currently_available_actions()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "case evidence");
        var evidence = Assert.Single(plane.Evidence(job.Id));
        var generated = plane.CreateFinding(job.Id, evidence.Id, "Case finding", "coherent case",
            "Info", "High", "analyst");

        var snapshot = plane.ReadCase(job.Id);

        Assert.Equal(job.Id, snapshot.Job.Id);
        Assert.Equal(snapshot.Job.ScopeId, snapshot.Scope.Id);
        Assert.Equal(snapshot.Job.PlanId, snapshot.Plan.Id);
        Assert.Equal(snapshot.Job.ApprovalId, snapshot.Approval.Id);
        Assert.All(snapshot.Evidence, value => Assert.Equal(job.Id, value.JobId));
        Assert.All(snapshot.Findings, value => Assert.Equal(job.Id, value.JobId));
        Assert.Contains(snapshot.Audit, value => value.EntityId == job.Id || value.EntityId == generated.Id);
        Assert.True(snapshot.AuditVerification.Valid);
        Assert.False(snapshot.AvailableActions.CanCancelJob);
        Assert.True(snapshot.AvailableActions.CanRevokeScope);
        Assert.False(snapshot.AvailableActions.CanRevokeApproval);
        Assert.True(snapshot.AvailableActions.CanVerifyEvidence);
        Assert.True(snapshot.AvailableActions.CanCreateFinding);
        Assert.True(snapshot.AvailableActions.CanReviewFinding);
        Assert.True(snapshot.AvailableActions.CanExportReport);
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
        var cases = await registry.ExecuteAsync("assessment cases", null);
        Assert.True(cases.Success);
        Assert.Contains(job.Id, cases.Output);
        Assert.Contains("canCancel=False", cases.Output);
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
        var cases = await InvokeAgent(registry, "assessment_cases", new { });
        Assert.True(cases.Success);
        Assert.Contains(job.Id, cases.Content);
        Assert.Contains("AvailableActions", cases.Content);
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

    [Fact]
    public async Task Agent_can_authorize_and_run_an_attached_page_in_one_tool_call()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        var pages = new MutablePageContexts(new PageContextObservation(
            "page-selected", "https://Example.COM:8443/path?q=1", "Selected", true, true));
        AssessmentIntegrationModule.RegisterAgent(registry, plane, pages);
        var confirmation = new RecordingConfirmation();
        var policy = new DefaultToolPolicyGate();
        policy.SetMode(AiPermissionMode.FullAccess);
        var dispatcher = new AiToolDispatcher(registry, policy, confirmation);

        var result = await dispatcher.InvokeAsync(new ToolInvocation("assessment_authorize_and_run",
            JsonSerializer.SerializeToElement(new
            {
                name = "one-click", authorization = "operator-confirmed", operatorId = "operator",
                adapterId = AuthorizedToolCatalog.SimulationEcho, input = "one-call-evidence",
                scopeMinutes = 60, timeoutSeconds = 10
            }), "page-selected", "assessment-test"));

        Assert.True(result.Success, result.Content);
        Assert.Equal(0, confirmation.Count);
        Assert.Equal(["example.com"], Assert.Single(plane.Scopes).Targets);
        var job = Assert.Single(plane.Jobs);
        Assert.Equal(AssessmentJobStatus.Completed, job.Status);
        var stored = Assert.Single(plane.Evidence(job.Id));
        Assert.Equal("one-call-evidence", stored.Content);
        using var json = JsonDocument.Parse(result.Content);
        var evidence = json.RootElement.GetProperty("Evidence");
        Assert.True(evidence.GetArrayLength() >= 1);
        Assert.Contains(evidence.EnumerateArray(), item => item.GetProperty("Id").GetString() == stored.Id);
    }

    [Fact]
    public async Task Agent_can_authorize_and_run_an_explicit_target_without_a_browser_page()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        AssessmentIntegrationModule.RegisterAgent(registry, plane);
        var policy = new DefaultToolPolicyGate();
        policy.SetMode(AiPermissionMode.FullAccess);
        var confirmation = new RecordingConfirmation();
        var dispatcher = new AiToolDispatcher(registry, policy, confirmation);

        var result = await dispatcher.InvokeAsync(new ToolInvocation("assessment_authorize_and_run",
            JsonSerializer.SerializeToElement(new
            {
                target = "127.0.0.1", authorization = "operator-confirmed",
                adapterId = AuthorizedToolCatalog.SimulationEcho, input = "explicit-target-evidence"
            }), null, "assessment-test"));

        Assert.True(result.Success, result.Content);
        Assert.Equal(0, confirmation.Count);
        Assert.Equal(["127.0.0.1"], Assert.Single(plane.Scopes).Targets);
        var job = Assert.Single(plane.Jobs);
        var stored = Assert.Single(plane.Evidence(job.Id));
        Assert.Equal("explicit-target-evidence", stored.Content);
        using var json = JsonDocument.Parse(result.Content);
        var evidence = json.RootElement.GetProperty("Evidence");
        Assert.True(evidence.GetArrayLength() >= 1);
        Assert.Contains(evidence.EnumerateArray(), item => item.GetProperty("Id").GetString() == stored.Id);
    }

    [Fact]
    public void Observation_finding_can_be_recorded_without_a_toolhost_job()
    {
        var plane = CreatePlane();
        var evidence = plane.AttachObservation("page-snapshot",
            """[{"code":"missing-hsts","severity":"Warning","message":"No HSTS"}]""", "analyst");
        var finding = plane.CreateFinding(evidence.JobId, evidence.Id, "Missing HSTS", "from snapshot",
            "Medium", "Low", "analyst");

        Assert.Equal("page-snapshot", evidence.Source);
        Assert.Equal(AssessmentFindingStatus.Unreviewed.ToString(), finding.Status);
        Assert.Equal(evidence.Id, finding.EvidenceId);
        Assert.Equal(["observation.local"], plane.Scopes.Single(scope => scope.Id == plane.Jobs.Single(job => job.Id == evidence.JobId).ScopeId).Targets);
    }

    [Fact]
    public async Task Agent_can_create_finding_from_page_snapshot_observation()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        AssessmentIntegrationModule.RegisterAgent(registry, plane);
        var result = await InvokeAgent(registry, "assessment_create_finding", new
        {
            source = "page-snapshot",
            observation = """{"code":"missing-csp","severity":"Warning","message":"No CSP"}""",
            title = "Missing CSP", description = "snapshot code", severity = "Medium", confidence = "Low"
        });

        Assert.True(result.Success, result.Content);
        var finding = Assert.Single(plane.Findings(plane.Jobs.Single().Id));
        Assert.Equal("Missing CSP", finding.Title);
        Assert.Equal("page-snapshot", plane.Evidence(finding.JobId).Single().Source);
    }

    [Fact]
    public async Task Warning_substring_does_not_auto_create_a_simulation_finding()
    {
        var plane = CreatePlane();
        var job = await RunEchoAsync(plane, "warning: simulated");
        Assert.Empty(plane.Findings(job.Id));
    }

    [Fact]
    public async Task Browser_bound_scope_derives_exact_target_from_attached_page()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        var pages = new MutablePageContexts(new PageContextObservation(
            "page-selected", "https://Example.COM:8443/path?q=1", "Selected", true, true));
        AssessmentIntegrationModule.RegisterAgent(registry, plane, pages);
        var confirmation = new RecordingConfirmation();

        var result = await InvokeAgentThroughDispatcher(registry, confirmation, "assessment_create_scope_from_page", new
        {
            name = "selected-page", authorization = "ticket-42", operatorId = "operator", minutes = 5,
            targets = new[] { "substituted.invalid" }
        }, "page-selected");

        Assert.True(result.Success, result.Content);
        var scope = Assert.Single(plane.Scopes);
        Assert.Equal(["example.com"], scope.Targets);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal("https://example.com:8443", json.RootElement.GetProperty("Origin").GetString());
        Assert.Equal(8443, json.RootElement.GetProperty("Port").GetInt32());
        Assert.DoesNotContain("substituted.invalid", result.Content, StringComparison.Ordinal);
        var definition = registry.All.Single(value => value.Name == "assessment_create_scope_from_page");
        Assert.Equal(AiToolRisk.Mutating, definition.Risk);
        Assert.False(definition.InputSchema.GetProperty("properties").TryGetProperty("targets", out _));
        Assert.False(definition.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("example.com", confirmation.Invocation!.Arguments
            .GetProperty("__hackermesPageBinding").GetProperty("Target").GetString());
    }

    [Fact]
    public async Task Browser_bound_scope_rejects_generic_target_substitution_and_invalid_page_urls()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        var pages = new MutablePageContexts(new PageContextObservation(
            "page-selected", "https://user:secret@example.com/", "Selected", true, true));
        AssessmentIntegrationModule.RegisterAgent(registry, plane, pages);
        var confirmation = new RecordingConfirmation();

        var generic = await InvokeAgentThroughDispatcher(registry, confirmation, "assessment_create_scope", new
        {
            name = "wrong", authorization = "ticket", operatorId = "operator",
            targets = new[] { "other.invalid" }, minutes = 5
        }, "page-selected");
        var invalidPage = await InvokeAgentThroughDispatcher(registry, confirmation, "assessment_create_scope_from_page", new
        {
            name = "wrong", authorization = "ticket", operatorId = "operator", minutes = 5
        }, "page-selected");
        var missingPage = await InvokeAgentThroughDispatcher(registry, confirmation, "assessment_create_scope_from_page", new
        {
            name = "wrong", authorization = "ticket", operatorId = "operator", minutes = 5
        }, "page-closed");

        Assert.False(generic.Success);
        Assert.Contains("cannot substitute", generic.Content, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidPage.Success);
        Assert.False(missingPage.Success);
        Assert.Empty(plane.Scopes);
    }

    [Fact]
    public async Task Browser_bound_scope_rejects_navigation_after_confirmation_and_rekeys_remembered_grants()
    {
        var plane = CreatePlane();
        var registry = new AiToolRegistry();
        var pages = new MutablePageContexts(new PageContextObservation(
            "page-selected", "https://first.example/path", "First", true, true));
        AssessmentIntegrationModule.RegisterAgent(registry, plane, pages);
        var confirmation = new RecordingConfirmation
        {
            OnConfirm = () => pages.Page = pages.Page with { Url = "https://second.example/" }
        };
        var dispatcher = new AiToolDispatcher(registry, new DefaultToolPolicyGate(), confirmation);
        var arguments = new { name = "frozen", authorization = "ticket", operatorId = "operator", minutes = 5 };

        var navigated = await dispatcher.InvokeAsync(new ToolInvocation("assessment_create_scope_from_page",
            JsonSerializer.SerializeToElement(arguments), "page-selected", "session"));

        Assert.False(navigated.Success);
        Assert.Contains("navigated after authorization", navigated.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plane.Scopes);

        confirmation.OnConfirm = null;
        confirmation.RememberForSession = true;
        var second = await dispatcher.InvokeAsync(new ToolInvocation("assessment_create_scope_from_page",
            JsonSerializer.SerializeToElement(arguments), "page-selected", "session"));
        pages.Page = pages.Page with { Url = "https://third.example/" };
        var third = await dispatcher.InvokeAsync(new ToolInvocation("assessment_create_scope_from_page",
            JsonSerializer.SerializeToElement(arguments), "page-selected", "session"));

        Assert.True(second.Success, second.Content);
        Assert.True(third.Success, third.Content);
        Assert.Equal(3, confirmation.Count);
        Assert.Equal(new[] { "second.example", "third.example" }, plane.Scopes.SelectMany(value => value.Targets));
    }

    private static async ValueTask<ToolResult> InvokeAgent(AiToolRegistry registry, string name, object arguments,
        string? pageId = null)
    {
        Assert.True(registry.TryGet(name, out var definition));
        return await definition!.Handler(new ToolInvocation(name, JsonSerializer.SerializeToElement(arguments), pageId), default);
    }

    private static ValueTask<ToolResult> InvokeAgentThroughDispatcher(AiToolRegistry registry,
        RecordingConfirmation confirmation, string name, object arguments, string? pageId = null) =>
        new AiToolDispatcher(registry, new DefaultToolPolicyGate(), confirmation).InvokeAsync(
            new ToolInvocation(name, JsonSerializer.SerializeToElement(arguments), pageId, "assessment-test"));

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

    private sealed class GateHost : IAssessmentExecutionHost
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AssessmentExecutionResult> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AssessmentExecutionResult> ExecuteAsync(AssessmentStep step,
            AssessmentExecutionAuthorization authorization, CancellationToken ct)
        {
            Started.TrySetResult(true);
            return await Gate.Task.WaitAsync(ct);
        }
    }

    private sealed class BestEffortHost : IAssessmentExecutionHost
    {
        public Task<AssessmentExecutionResult> ExecuteAsync(AssessmentStep step,
            AssessmentExecutionAuthorization authorization, CancellationToken ct) =>
            Task.FromResult(step.Input == "fail"
                ? new AssessmentExecutionResult(false, string.Empty, "expected failure")
                : new AssessmentExecutionResult(true, step.Input));
    }

    private sealed class ReconHeadersHost : IAssessmentExecutionHost
    {
        public Task<AssessmentExecutionResult> ExecuteAsync(AssessmentStep step,
            AssessmentExecutionAuthorization authorization, CancellationToken ct) =>
            Task.FromResult(new AssessmentExecutionResult(true,
                "HTTP/1.1 200 OK\nContent-Type: text/html\n\n<html><body>hello</body></html>"));
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

    private sealed class MutablePageContexts(PageContextObservation page) : IPageContextQueryService
    {
        public PageContextObservation Page { get; set; } = page;
        public PageContextObservation? Read(string pageId) =>
            string.Equals(Page.PageId, pageId, StringComparison.Ordinal) ? Page : null;
    }

    private sealed class RecordingConfirmation : IToolConfirmationService
    {
        public int Count { get; private set; }
        public ToolInvocation? Invocation { get; private set; }
        public bool RememberForSession { get; set; }
        public Action? OnConfirm { get; set; }

        public ValueTask<ToolConfirmation> ConfirmAsync(ToolInvocation invocation, string reason, CancellationToken ct)
        {
            Count++;
            Invocation = invocation;
            OnConfirm?.Invoke();
            return ValueTask.FromResult(new ToolConfirmation(true, RememberForSession));
        }
    }
}
