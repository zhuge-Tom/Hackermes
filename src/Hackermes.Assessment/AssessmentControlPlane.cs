using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Assessment;

public enum AssessmentJobStatus { Draft, AwaitingApproval, Queued, Running, Completed, Failed, Cancelled, Revoked }

public sealed record AssessmentScope(string Id, string Name, string AuthorizationReference, string OperatorId,
    IReadOnlyList<string> Targets, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Revoked = false);

public sealed record AssessmentStep(string AdapterId, string Input, int TimeoutSeconds = 30, int MaxOutputBytes = 262_144);

public sealed record AssessmentPlan(string Id, string ScopeId, string Name, IReadOnlyList<AssessmentStep> Steps,
    string CreatedBy, DateTimeOffset CreatedAt, string VersionHash);

public sealed record AssessmentApproval(string Id, string PlanId, string PlanHash, string ApprovedBy, DateTimeOffset ExpiresAt,
    bool Revoked = false, DateTimeOffset? ConsumedAt = null);

public sealed record AssessmentJob(string Id, string ScopeId, string PlanId, string ApprovalId, AssessmentJobStatus Status,
    string RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null,
    string? Failure = null, string? CancellationReason = null);

public sealed record AssessmentEvidence(string Id, string JobId, DateTimeOffset Timestamp, string Source, string Content,
    string Sha256, bool Redacted = true);

public sealed record AssessmentFinding(string Id, string JobId, string EvidenceId, string Title, string Severity,
    string Status, DateTimeOffset CreatedAt);

public sealed record AssessmentAuditEntry(string Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityId, string Detail);

public sealed record AssessmentExecutionResult(bool Success, string Output, string? Error = null);

public interface IAssessmentControlPlane
{
    IReadOnlyList<AssessmentScope> Scopes { get; }
    IReadOnlyList<AssessmentPlan> Plans { get; }
    IReadOnlyList<AssessmentJob> Jobs { get; }
    AssessmentScope CreateScope(string name, string authorizationReference, string operatorId, IReadOnlyList<string> targets, DateTimeOffset expiresAt);
    AssessmentPlan CreatePlan(string scopeId, string name, IReadOnlyList<AssessmentStep> steps, string actor);
    AssessmentApproval Approve(string planId, string operatorId, DateTimeOffset expiresAt);
    Task<AssessmentJob> StartAsync(string planId, string approvalId, string actor, CancellationToken ct = default);
    bool Cancel(string jobId, string actor, string reason);
    bool RevokeScope(string scopeId, string actor, string reason);
    bool RevokeApproval(string approvalId, string actor, string reason);
    IReadOnlyList<AssessmentEvidence> Evidence(string jobId);
    IReadOnlyList<AssessmentFinding> Findings(string jobId);
    IReadOnlyList<AssessmentAuditEntry> Audit(int limit = 100);
    string ExportReport(string jobId);
}

/// <summary>
/// Minimal, local-only execution host. It accepts only explicitly registered simulation adapters;
/// future external adapters belong behind the separate ToolHost seam.
/// </summary>
public interface IAssessmentExecutionHost
{
    Task<AssessmentExecutionResult> ExecuteAsync(AssessmentStep step, AssessmentExecutionAuthorization authorization, CancellationToken ct);
}

public sealed record AssessmentExecutionAuthorization(string JobId, string PlanId, string ApprovalId, string ScopeId,
    string Actor, IReadOnlyList<string> AllowedTargets, DateTimeOffset ExpiresAt);

public sealed class SimulatedAssessmentExecutionHost : IAssessmentExecutionHost
{
    public async Task<AssessmentExecutionResult> ExecuteAsync(AssessmentStep step, AssessmentExecutionAuthorization authorization, CancellationToken ct)
    {
        if (!string.Equals(step.AdapterId, "simulation.echo", StringComparison.Ordinal))
            return new AssessmentExecutionResult(false, string.Empty, "Only the local simulation.echo adapter is registered.");
        await Task.Delay(Math.Min(100, Math.Max(1, step.TimeoutSeconds) * 2), ct).ConfigureAwait(false);
        var output = step.Input.Length > step.MaxOutputBytes ? step.Input[..step.MaxOutputBytes] : step.Input;
        return new AssessmentExecutionResult(true, output);
    }
}

public sealed class AssessmentControlPlane : IAssessmentControlPlane
{
    private readonly IAssessmentExecutionHost _host;
    private readonly IAppLogger _logger;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private AssessmentDocument _document;

    public AssessmentControlPlane(IAssessmentExecutionHost host, ISettingsService settings, IAppLogger logger)
    {
        _host = host; _logger = logger.ForCategory(nameof(AssessmentControlPlane));
        _path = Path.Combine(Path.GetDirectoryName(settings.SettingsFilePath) ?? AppContext.BaseDirectory, "assessments.json");
        _document = Load();
    }

    public IReadOnlyList<AssessmentScope> Scopes { get { lock (_gate) return [.. _document.Scopes]; } }
    public IReadOnlyList<AssessmentPlan> Plans { get { lock (_gate) return [.. _document.Plans]; } }
    public IReadOnlyList<AssessmentJob> Jobs { get { lock (_gate) return [.. _document.Jobs]; } }

    public AssessmentScope CreateScope(string name, string authorizationReference, string operatorId, IReadOnlyList<string> targets, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(authorizationReference) || string.IsNullOrWhiteSpace(operatorId)) throw new ArgumentException("Scope name, authorization reference and operator are required.");
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Scope expiry must be in the future.");
        var normalized = targets.Where(IsSafeTarget).Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (normalized.Length == 0) throw new ArgumentException("At least one exact hostname, loopback address or localhost target is required.");
        var value = new AssessmentScope(Id(), Limit(name, 120), Limit(authorizationReference, 240), Limit(operatorId, 120), normalized, DateTimeOffset.UtcNow, expiresAt);
        lock (_gate) { _document.Scopes.Add(value); AuditUnsafe(operatorId, "scope.create", value.Id, value.Name); SaveUnsafe(); }
        return value;
    }

    public AssessmentPlan CreatePlan(string scopeId, string name, IReadOnlyList<AssessmentStep> steps, string actor)
    {
        var scope = Scope(scopeId);
        EnsureScopeUsable(scope);
        var clean = steps.Select(step => AuthorizedToolCatalog.NormalizeStep(step, scope!.Targets)).ToArray();
        if (clean.Length == 0 || clean.Length > 32) throw new ArgumentException("A plan must have 1 to 32 bounded steps.");
        var id = Id();
        var hash = Hash(scopeId + "\n" + name + "\n" + string.Join("\n", clean.Select(step => $"{step.AdapterId}|{step.Input}|{step.TimeoutSeconds}|{step.MaxOutputBytes}")));
        var plan = new AssessmentPlan(id, scopeId, Limit(name, 120), clean, Limit(actor, 120), DateTimeOffset.UtcNow, hash);
        lock (_gate) { _document.Plans.Add(plan); AuditUnsafe(actor, "plan.create", id, plan.Name); SaveUnsafe(); }
        return plan;
    }

    public AssessmentApproval Approve(string planId, string operatorId, DateTimeOffset expiresAt)
    {
        var plan = Plan(planId) ?? throw new InvalidOperationException("Plan not found.");
        EnsureScopeUsable(Scope(plan.ScopeId));
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Approval expiry must be in the future.");
        var approval = new AssessmentApproval(Id(), plan.Id, plan.VersionHash, Limit(operatorId, 120), expiresAt);
        lock (_gate) { _document.Approvals.Add(approval); AuditUnsafe(operatorId, "plan.approve", planId, approval.Id); SaveUnsafe(); }
        return approval;
    }

    public async Task<AssessmentJob> StartAsync(string planId, string approvalId, string actor, CancellationToken ct = default)
    {
        AssessmentPlan plan; AssessmentApproval approval; AssessmentJob job; CancellationTokenSource linked;
        lock (_gate)
        {
            plan = Plan(planId) ?? throw new InvalidOperationException("Plan not found.");
            EnsureScopeUsable(Scope(plan.ScopeId));
            approval = _document.Approvals.SingleOrDefault(value => value.Id == approvalId) ?? throw new InvalidOperationException("Approval not found.");
            if (approval.Revoked || approval.ConsumedAt is not null || approval.ExpiresAt <= DateTimeOffset.UtcNow || approval.PlanId != plan.Id || approval.PlanHash != plan.VersionHash) throw new InvalidOperationException("Approval is expired, revoked, already used or no longer matches this plan.");
            var approvalIndex = _document.Approvals.IndexOf(approval);
            approval = approval with { ConsumedAt = DateTimeOffset.UtcNow };
            _document.Approvals[approvalIndex] = approval;
            job = new AssessmentJob(Id(), plan.ScopeId, plan.Id, approval.Id, AssessmentJobStatus.Queued, Limit(actor, 120), DateTimeOffset.UtcNow);
            _document.Jobs.Add(job); AuditUnsafe(actor, "job.queue", job.Id, plan.Id); SaveUnsafe();
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct); _running[job.Id] = linked;
        }

        return await RunAsync(job, plan, linked).ConfigureAwait(false);
    }

    public bool Cancel(string jobId, string actor, string reason)
    {
        lock (_gate)
        {
            var job = _document.Jobs.Find(value => value.Id == jobId);
            if (job is null || job.Status is AssessmentJobStatus.Completed or AssessmentJobStatus.Failed or AssessmentJobStatus.Cancelled) return false;
            if (_running.Remove(jobId, out var cancellation)) cancellation.Cancel();
            ReplaceJobUnsafe(job with { Status = AssessmentJobStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow, CancellationReason = Limit(reason, 240) });
            AuditUnsafe(actor, "job.cancel", jobId, Limit(reason, 240)); SaveUnsafe(); return true;
        }
    }

    public bool RevokeScope(string scopeId, string actor, string reason)
    {
        lock (_gate)
        {
            var scope = _document.Scopes.Find(value => value.Id == scopeId);
            if (scope is null || scope.Revoked) return false;
            _document.Scopes[_document.Scopes.IndexOf(scope)] = scope with { Revoked = true };
            foreach (var job in _document.Jobs.Where(job => job.ScopeId == scopeId && job.Status is AssessmentJobStatus.Queued or AssessmentJobStatus.Running).ToArray()) Cancel(job.Id, actor, reason);
            AuditUnsafe(actor, "scope.revoke", scopeId, Limit(reason, 240)); SaveUnsafe(); return true;
        }
    }

    public bool RevokeApproval(string approvalId, string actor, string reason)
    {
        lock (_gate)
        {
            var approval = _document.Approvals.Find(value => value.Id == approvalId);
            if (approval is null || approval.Revoked || approval.ConsumedAt is not null) return false;
            _document.Approvals[_document.Approvals.IndexOf(approval)] = approval with { Revoked = true };
            AuditUnsafe(actor, "approval.revoke", approvalId, Limit(reason, 240)); SaveUnsafe(); return true;
        }
    }

    public IReadOnlyList<AssessmentEvidence> Evidence(string jobId) { lock (_gate) return _document.Evidence.Where(value => value.JobId == jobId).ToArray(); }
    public IReadOnlyList<AssessmentFinding> Findings(string jobId) { lock (_gate) return _document.Findings.Where(value => value.JobId == jobId).ToArray(); }
    public IReadOnlyList<AssessmentAuditEntry> Audit(int limit = 100) { lock (_gate) return _document.Audit.OrderByDescending(value => value.Timestamp).Take(Math.Clamp(limit, 1, 500)).ToArray(); }
    public string ExportReport(string jobId)
    {
        lock (_gate)
        {
            var job = _document.Jobs.SingleOrDefault(value => value.Id == jobId) ?? throw new InvalidOperationException("Job not found.");
            return JsonSerializer.Serialize(new { job, evidence = Evidence(jobId), findings = Findings(jobId), audit = _document.Audit.Where(value => value.EntityId == jobId).ToArray() }, AssessmentJsonContext.Default.Options);
        }
    }

    private async Task<AssessmentJob> RunAsync(AssessmentJob job, AssessmentPlan plan, CancellationTokenSource cancellation)
    {
        try
        {
            lock (_gate) { ReplaceJobUnsafe(job = job with { Status = AssessmentJobStatus.Running, StartedAt = DateTimeOffset.UtcNow }); AuditUnsafe(job.RequestedBy, "job.start", job.Id, plan.Id); SaveUnsafe(); }
            var scope = Scope(plan.ScopeId) ?? throw new InvalidOperationException("Scope not found.");
            var approval = _document.Approvals.Single(value => value.Id == job.ApprovalId);
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                var authorization = new AssessmentExecutionAuthorization(job.Id, plan.Id, approval.Id, scope.Id,
                    job.RequestedBy, scope.Targets, DateTimeOffset.UtcNow.AddSeconds(Math.Min(step.TimeoutSeconds + 15, 135)));
                var result = await _host.ExecuteAsync(step, authorization, timeout.Token).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException(result.Error ?? "ToolHost execution failed.");
                AddEvidence(job.Id, step.AdapterId, result.Output);
            }
            lock (_gate) { ReplaceJobUnsafe(job = job with { Status = AssessmentJobStatus.Completed, FinishedAt = DateTimeOffset.UtcNow }); AuditUnsafe(job.RequestedBy, "job.complete", job.Id, "ok"); SaveUnsafe(); }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { ReplaceJobUnsafe(job = job with { Status = AssessmentJobStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow, CancellationReason = "cancelled or timed out" }); AuditUnsafe(job.RequestedBy, "job.cancelled", job.Id, "token"); SaveUnsafe(); }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Assessment job {job.Id} failed: {ex.Message}");
            lock (_gate) { ReplaceJobUnsafe(job = job with { Status = AssessmentJobStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, Failure = Limit(ex.Message, 500) }); AuditUnsafe(job.RequestedBy, "job.failed", job.Id, job.Failure!); SaveUnsafe(); }
        }
        finally { lock (_gate) { if (_running.Remove(job.Id, out var source)) source.Dispose(); } }
        return job;
    }

    private void AddEvidence(string jobId, string source, string output)
    {
        var safe = Limit(output, 262_144);
        lock (_gate)
        {
            var evidence = new AssessmentEvidence(Id(), jobId, DateTimeOffset.UtcNow, source, safe, Hash(safe));
            _document.Evidence.Add(evidence);
            if (safe.Contains("warning", StringComparison.OrdinalIgnoreCase)) _document.Findings.Add(new AssessmentFinding(Id(), jobId, evidence.Id, "Simulation warning", "Info", "Unreviewed", DateTimeOffset.UtcNow));
            AuditUnsafe("system", "evidence.append", jobId, evidence.Id); SaveUnsafe();
        }
    }

    private AssessmentScope? Scope(string id) => _document.Scopes.SingleOrDefault(value => value.Id == id);
    private AssessmentPlan? Plan(string id) => _document.Plans.SingleOrDefault(value => value.Id == id);
    private static void EnsureScopeUsable(AssessmentScope? scope)
    {
        if (scope is null || scope.Revoked || scope.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Scope is missing, revoked or expired.");
    }
    private void ReplaceJobUnsafe(AssessmentJob job) { var index = _document.Jobs.FindIndex(value => value.Id == job.Id); if (index >= 0) _document.Jobs[index] = job; }
    private void AuditUnsafe(string actor, string action, string entity, string detail) => _document.Audit.Add(new AssessmentAuditEntry(Id(), DateTimeOffset.UtcNow, Limit(actor, 120), action, entity, Limit(detail, 500)));
    private AssessmentDocument Load()
    {
        try { if (File.Exists(_path)) return JsonSerializer.Deserialize(File.ReadAllText(_path), AssessmentJsonContext.Default.AssessmentDocument) ?? new AssessmentDocument(); }
        catch (Exception ex) { _logger.Warn($"Assessment store could not be loaded: {ex.Message}"); }
        return new AssessmentDocument();
    }
    private void SaveUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_document, AssessmentJsonContext.Default.AssessmentDocument)); File.Move(temporary, _path, true);
    }
    private static bool IsSafeTarget(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 253 && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or ':');
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Limit(string? value, int max) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, max)];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class AssessmentDocument
{
    public int Version { get; set; } = 1;
    public List<AssessmentScope> Scopes { get; set; } = new();
    public List<AssessmentPlan> Plans { get; set; } = new();
    public List<AssessmentApproval> Approvals { get; set; } = new();
    public List<AssessmentJob> Jobs { get; set; } = new();
    public List<AssessmentEvidence> Evidence { get; set; } = new();
    public List<AssessmentFinding> Findings { get; set; } = new();
    public List<AssessmentAuditEntry> Audit { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AssessmentDocument))]
internal partial class AssessmentJsonContext : JsonSerializerContext { }
