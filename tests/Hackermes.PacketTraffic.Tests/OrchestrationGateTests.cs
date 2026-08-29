using Hackermes.AiPanel.Agent;
using Hackermes.Assessment;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Batch-C orchestration gate: exploitation adapters require detection-stage evidence for
/// the same target (earlier in the plan, or prior evidence from an active scope).
/// </summary>
[Collection("ToolHost serial")]
public sealed class OrchestrationGateTests
{
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

    private static (string Root, AssessmentControlPlane Plane) CreatePlane()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, new AssessmentControlPlane(new SimulatedAssessmentExecutionHost(),
            new TestSettings(Path.Combine(root, "settings.json")), new NullLogger()));
    }

    private static IDisposable VcenterFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hackermes-gate-vcenter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var killer = Path.Combine(dir, "main.exe");
        var shell = Path.Combine(dir, "shell-verify.jsp");
        File.WriteAllText(killer, string.Empty);
        File.WriteAllText(shell, string.Empty);
        var oldKiller = Environment.GetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH");
        var oldShell = Environment.GetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH");
        Environment.SetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH", killer);
        Environment.SetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH", shell);
        return new ScopeRestore(() =>
        {
            Environment.SetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH", oldKiller);
            Environment.SetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH", oldShell);
            try { Directory.Delete(dir, recursive: true); } catch { }
        });
    }

    private sealed class ScopeRestore(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private static (AssessmentScope Scope, AssessmentPlan Plan, AssessmentApproval Approval)
        RunDetection(AssessmentControlPlane plane, string scopeName, string[] targets)
    {
        var scope = plane.CreateScope(scopeName, "test", "tester", targets, DateTimeOffset.UtcNow.AddHours(1));
        // ContinueOnError so the simulated host's miss lands as evidence and the job completes.
        var plan = plane.CreatePlan(scope.Id, "detection",
            [new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe,
                "{\"target\":\"" + targets[0] + "\",\"scheme\":\"http\",\"port\":80}", 30, ContinueOnError: true)],
            "tester");
        var approval = plane.Approve(plan.Id, "tester", DateTimeOffset.UtcNow.AddHours(1));
        var job = plane.StartAsync(plan.Id, approval.Id, "tester").GetAwaiter().GetResult();
        Assert.Equal(AssessmentJobStatus.CompletedWithWarnings, job.Status);
        return (scope, plan, approval);
    }

    private static AssessmentStep VcenterStep() => new(
        AuthorizedToolCatalog.ExploitVcenterVerify,
        "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":443,\"mode\":\"21985\",\"command\":\"whoami\"}", 60);

    [Fact]
    public void ExploitStepWithoutDetectionEvidenceIsRefused()
    {
        using var files = VcenterFiles();
        var (root, plane) = CreatePlane();
        try
        {
            var scope = plane.CreateScope("loopback", "test", "tester", ["127.0.0.1"], DateTimeOffset.UtcNow.AddHours(1));
            var exception = Assert.Throws<InvalidOperationException>(() => plane.CreatePlan(
                scope.Id, "exploit-first", [VcenterStep()], "tester"));
            Assert.Contains("detection-stage evidence", exception.Message);
            Assert.Contains("127.0.0.1", exception.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExploitStepIsAllowedAfterDetectionStepInsideSamePlan()
    {
        using var files = VcenterFiles();
        var (root, plane) = CreatePlane();
        try
        {
            var scope = plane.CreateScope("loopback", "test", "tester", ["127.0.0.1"], DateTimeOffset.UtcNow.AddHours(1));
            var plan = plane.CreatePlan(scope.Id, "detect-then-exploit",
            [
                new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80}", 30),
                VcenterStep()
            ], "tester");
            Assert.Equal(2, plan.Steps.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExploitStepIsAllowedWithPriorDetectionEvidenceForSameTarget()
    {
        using var files = VcenterFiles();
        var (root, plane) = CreatePlane();
        try
        {
            var (scope, _, _) = RunDetection(plane, "loopback", ["127.0.0.1"]);
            var plan = plane.CreatePlan(scope.Id, "exploit-after-detection", [VcenterStep()], "tester");
            Assert.Single(plan.Steps);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExploitStepIsRefusedWhenDetectionEvidenceCoversAnotherTarget()
    {
        using var files = VcenterFiles();
        var (root, plane) = CreatePlane();
        try
        {
            RunDetection(plane, "other-target", ["127.0.0.2"]);
            var scope = plane.CreateScope("loopback", "test", "tester", ["127.0.0.1"], DateTimeOffset.UtcNow.AddHours(1));
            Assert.Throws<InvalidOperationException>(() => plane.CreatePlan(
                scope.Id, "exploit-wrong-target", [VcenterStep()], "tester"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExploitStepIsRefusedWhenEvidenceScopeIsExpired()
    {
        using var files = VcenterFiles();
        var (root, plane) = CreatePlane();
        try
        {
            var (scope, plan, approval) = RunDetection(plane, "loopback-short", ["127.0.0.1"]);
            // Expire the evidence scope: the gate only trusts active authorizations.
            Assert.True(plane.RevokeScope(scope.Id, "tester", "expired for gate test"));
            var freshScope = plane.CreateScope("loopback", "test", "tester", ["127.0.0.1"], DateTimeOffset.UtcNow.AddHours(1));
            Assert.Throws<InvalidOperationException>(() => plane.CreatePlan(
                freshScope.Id, "exploit-after-revoke", [VcenterStep()], "tester"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BuiltInSkillsSeedOnceAndStayDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AgentSkillStore(new TestSettings(Path.Combine(root, "settings.json")), new NullLogger());
            Assert.Equal(3, BuiltInSkillCatalog.SeedOnce(store));
            Assert.Equal(0, BuiltInSkillCatalog.SeedOnce(store));

            var seeded = store.Snapshot().Where(skill => skill.Id.StartsWith("builtin.", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, seeded.Length);
            Assert.All(seeded, skill => Assert.False(skill.Enabled));
            Assert.Contains(seeded, skill => skill.Id == BuiltInSkillCatalog.OaPocChain);
            // User edits win: changing a seeded skill then re-seeding must not overwrite it.
            store.Upsert(new AgentSkill
            {
                Id = BuiltInSkillCatalog.OaPocChain, Name = "renamed", Enabled = true,
                Instructions = "custom operator workflow"
            });
            BuiltInSkillCatalog.SeedOnce(store);
            Assert.Equal("renamed", store.Snapshot().Single(skill => skill.Id == BuiltInSkillCatalog.OaPocChain).Name);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
