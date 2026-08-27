using Hackermes.AiPanel.Runtime;
using Hackermes.App;
using Hackermes.Assessment;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using Hackermes.Base.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AssessmentContextHookTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hm-hook-").FullName;

    [Fact]
    public async Task Hook_injects_case_and_finding_titles()
    {
        var plane = new AssessmentControlPlane(new SimulatedAssessmentExecutionHost(),
            new PathSettings(Path.Combine(_root, "settings.json")), new NullLogger(), new MemorySecrets());
        var evidence = plane.AttachObservation("packet-analyze",
            """{"code":"missing-csp","severity":"Warning","message":"No CSP"}""", "analyst");
        plane.CreateFinding(evidence.JobId, evidence.Id, "Missing CSP", "obs", "Medium", "Low", "analyst");

        var hook = new AssessmentContextPreStepHook(plane);
        var decision = await hook.BeforeStepAsync(new PreStepInput(1, 1, []), CancellationToken.None);

        var ephemeral = Assert.IsType<PreStepDecision.EphemeralDecision>(decision);
        var text = Assert.Single(ephemeral.Appendix).Content;
        Assert.Contains("【上下文注入·评估案件】", text, StringComparison.Ordinal);
        Assert.Contains(evidence.JobId, text, StringComparison.Ordinal);
        Assert.Contains("Missing CSP", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hook_is_silent_when_there_are_no_cases()
    {
        var plane = new AssessmentControlPlane(new SimulatedAssessmentExecutionHost(),
            new PathSettings(Path.Combine(_root, "settings.json")), new NullLogger(), new MemorySecrets());
        var hook = new AssessmentContextPreStepHook(plane);
        var decision = await hook.BeforeStepAsync(new PreStepInput(1, 1, []), CancellationToken.None);
        Assert.Same(PreStepDecision.Proceed, decision);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class PathSettings(string path) : ISettingsService
    {
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null) => true;
        public string SettingsFilePath => path;
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
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
