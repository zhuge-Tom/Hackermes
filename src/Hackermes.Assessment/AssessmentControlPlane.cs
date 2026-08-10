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

public enum AssessmentFindingStatus { Unreviewed, InReview, Confirmed, FalsePositive, Resolved, AcceptedRisk }

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
    string Sha256, bool Redacted = true, string ContentType = "text/plain");

public sealed record AssessmentFinding(string Id, string JobId, string EvidenceId, string Title, string Severity,
    string Status, DateTimeOffset CreatedAt, string Description = "", string Confidence = "Medium",
    string? ReviewedBy = null, DateTimeOffset? ReviewedAt = null, string? ReviewNote = null);

public sealed record AssessmentAuditEntry(string Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityId,
    string Detail, string PreviousHash = "", string IntegrityHash = "");

public sealed record AssessmentEvidenceVerification(bool Valid, string EvidenceId, string ExpectedSha256,
    string ActualSha256, string? ErrorCode = null);

public sealed record AssessmentAuditVerification(bool Valid, int CheckedEntries, string? ErrorCode = null,
    string? EntryId = null);

public sealed record AssessmentReport(AssessmentJob Job, AssessmentScope? Scope, AssessmentPlan? Plan,
    IReadOnlyList<AssessmentEvidence> Evidence, IReadOnlyList<AssessmentFinding> Findings,
    IReadOnlyList<AssessmentAuditEntry> Audit, AssessmentAuditVerification AuditVerification);

public sealed record AssessmentExecutionResult(bool Success, string Output, string? Error = null);

public interface IAssessmentControlPlane
{
    IReadOnlyList<AssessmentScope> Scopes { get; }
    IReadOnlyList<AssessmentPlan> Plans { get; }
    IReadOnlyList<AssessmentApproval> Approvals { get; }
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
    AssessmentFinding CreateFinding(string jobId, string evidenceId, string title, string description,
        string severity, string confidence, string actor);
    AssessmentFinding ReviewFinding(string findingId, AssessmentFindingStatus status, string actor, string note);
    AssessmentEvidenceVerification VerifyEvidence(string evidenceId);
    IReadOnlyList<AssessmentAuditEntry> Audit(int limit = 100);
    IReadOnlyList<AssessmentAuditEntry> AuditForEntity(string entityId, int limit = 100);
    AssessmentAuditVerification VerifyAudit();
    string ExportReport(string jobId, string format = "json");
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
    private const int CurrentDocumentVersion = 2;
    private const string AuditKeyName = "assessment.audit.hmac.v1";
    private readonly IAssessmentExecutionHost _host;
    private readonly IAppLogger _logger;
    private readonly byte[] _auditKey;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private AssessmentDocument _document;

    public AssessmentControlPlane(IAssessmentExecutionHost host, ISettingsService settings, IAppLogger logger)
        : this(host, settings, logger, SecretStoreFactory.Create(logger,
            Path.Combine(Path.GetDirectoryName(settings.SettingsFilePath) ?? AppContext.BaseDirectory, "assessment-secrets.dat")))
    {
    }

    public AssessmentControlPlane(IAssessmentExecutionHost host, ISettingsService settings, IAppLogger logger, ISecretStore secrets)
    {
        _host = host; _logger = logger.ForCategory(nameof(AssessmentControlPlane));
        _path = Path.Combine(Path.GetDirectoryName(settings.SettingsFilePath) ?? AppContext.BaseDirectory, "assessments.json");
        _auditKey = LoadOrCreateAuditKey(secrets);
        _document = Load(out var needsSave);
        if (RecoverInterruptedJobs()) needsSave = true;
        if (needsSave) SaveUnsafe(createBackup: false);
    }

    public IReadOnlyList<AssessmentScope> Scopes { get { lock (_gate) return [.. _document.Scopes]; } }
    public IReadOnlyList<AssessmentPlan> Plans { get { lock (_gate) return [.. _document.Plans]; } }
    public IReadOnlyList<AssessmentApproval> Approvals { get { lock (_gate) return [.. _document.Approvals]; } }
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
    public AssessmentFinding CreateFinding(string jobId, string evidenceId, string title, string description,
        string severity, string confidence, string actor)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Finding title and actor are required.");
        lock (_gate)
        {
            if (_document.Jobs.All(value => value.Id != jobId)) throw new InvalidOperationException("Job not found.");
            var evidence = _document.Evidence.SingleOrDefault(value => value.Id == evidenceId && value.JobId == jobId)
                ?? throw new InvalidOperationException("Evidence does not belong to this job.");
            var finding = new AssessmentFinding(Id(), jobId, evidence.Id, Limit(title, 160), NormalizeSeverity(severity),
                AssessmentFindingStatus.Unreviewed.ToString(), DateTimeOffset.UtcNow, Limit(description, 4000),
                NormalizeConfidence(confidence));
            _document.Findings.Add(finding);
            AuditUnsafe(actor, "finding.create", finding.Id, $"job={jobId};evidence={evidenceId};severity={finding.Severity}");
            SaveUnsafe();
            return finding;
        }
    }

    public AssessmentFinding ReviewFinding(string findingId, AssessmentFindingStatus status, string actor, string note)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Reviewer identity is required.");
        lock (_gate)
        {
            var finding = _document.Findings.SingleOrDefault(value => value.Id == findingId)
                ?? throw new InvalidOperationException("Finding not found.");
            var updated = finding with
            {
                Status = status.ToString(), ReviewedBy = Limit(actor, 120), ReviewedAt = DateTimeOffset.UtcNow,
                ReviewNote = Limit(note, 2000)
            };
            _document.Findings[_document.Findings.IndexOf(finding)] = updated;
            AuditUnsafe(actor, "finding.review", finding.Id, $"status={updated.Status};job={finding.JobId}");
            SaveUnsafe();
            return updated;
        }
    }

    public AssessmentEvidenceVerification VerifyEvidence(string evidenceId)
    {
        lock (_gate)
        {
            var evidence = _document.Evidence.SingleOrDefault(value => value.Id == evidenceId);
            if (evidence is null) return new AssessmentEvidenceVerification(false, evidenceId, string.Empty, string.Empty, "not_found");
            var actual = Hash(evidence.Content);
            return new AssessmentEvidenceVerification(string.Equals(actual, evidence.Sha256, StringComparison.OrdinalIgnoreCase),
                evidence.Id, evidence.Sha256, actual,
                string.Equals(actual, evidence.Sha256, StringComparison.OrdinalIgnoreCase) ? null : "hash_mismatch");
        }
    }

    public IReadOnlyList<AssessmentAuditEntry> Audit(int limit = 100) { lock (_gate) return _document.Audit.OrderByDescending(value => value.Timestamp).Take(Math.Clamp(limit, 1, 500)).ToArray(); }
    public IReadOnlyList<AssessmentAuditEntry> AuditForEntity(string entityId, int limit = 100)
    {
        lock (_gate)
        {
            var relatedFindingIds = _document.Findings.Where(value => value.JobId == entityId).Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            var relatedEvidenceIds = _document.Evidence.Where(value => value.JobId == entityId).Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            return _document.Audit.Where(value => value.EntityId == entityId || relatedFindingIds.Contains(value.EntityId) ||
                    relatedEvidenceIds.Contains(value.EntityId) || value.Detail.Contains(entityId, StringComparison.Ordinal))
                .OrderByDescending(value => value.Timestamp).Take(Math.Clamp(limit, 1, 500)).ToArray();
        }
    }

    public AssessmentAuditVerification VerifyAudit()
    {
        lock (_gate)
        {
            var previous = string.Empty;
            for (var index = 0; index < _document.Audit.Count; index++)
            {
                var entry = _document.Audit[index];
                if (string.IsNullOrEmpty(entry.IntegrityHash))
                    return new AssessmentAuditVerification(false, index, "legacy_or_unsigned_entry", entry.Id);
                if (!string.Equals(entry.PreviousHash, previous, StringComparison.Ordinal))
                    return new AssessmentAuditVerification(false, index, "previous_hash_mismatch", entry.Id);
                var actual = AuditHash(entry with { IntegrityHash = string.Empty });
                if (!string.Equals(actual, entry.IntegrityHash, StringComparison.OrdinalIgnoreCase))
                    return new AssessmentAuditVerification(false, index, "entry_hash_mismatch", entry.Id);
                previous = entry.IntegrityHash;
            }
            return new AssessmentAuditVerification(true, _document.Audit.Count);
        }
    }

    public string ExportReport(string jobId, string format = "json")
    {
        lock (_gate)
        {
            var job = _document.Jobs.SingleOrDefault(value => value.Id == jobId) ?? throw new InvalidOperationException("Job not found.");
            var scope = _document.Scopes.SingleOrDefault(value => value.Id == job.ScopeId);
            var plan = _document.Plans.SingleOrDefault(value => value.Id == job.PlanId);
            var evidence = Evidence(jobId);
            var findings = Findings(jobId);
            var audit = AuditForEntity(jobId, 500);
            var verification = VerifyAudit();
            return format.Trim().ToLowerInvariant() switch
            {
                "markdown" or "md" => MarkdownReport(job, scope, plan, evidence, findings, audit, verification),
                "html" => HtmlReport(job, scope, plan, evidence, findings, audit, verification),
                "json" or "" => JsonSerializer.Serialize(new AssessmentReport(job, scope, plan, evidence, findings, audit, verification), AssessmentJsonContext.Default.AssessmentReport),
                _ => throw new ArgumentException("Report format must be json, markdown or html.")
            };
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
            if (safe.Contains("warning", StringComparison.OrdinalIgnoreCase))
                _document.Findings.Add(new AssessmentFinding(Id(), jobId, evidence.Id, "Simulation warning", "Info", AssessmentFindingStatus.Unreviewed.ToString(), DateTimeOffset.UtcNow));
            AuditUnsafe("system", "evidence.append", evidence.Id, $"job={jobId};sha256={evidence.Sha256}"); SaveUnsafe();
        }
    }

    private AssessmentScope? Scope(string id) => _document.Scopes.SingleOrDefault(value => value.Id == id);
    private AssessmentPlan? Plan(string id) => _document.Plans.SingleOrDefault(value => value.Id == id);
    private static void EnsureScopeUsable(AssessmentScope? scope)
    {
        if (scope is null || scope.Revoked || scope.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Scope is missing, revoked or expired.");
    }
    private void ReplaceJobUnsafe(AssessmentJob job) { var index = _document.Jobs.FindIndex(value => value.Id == job.Id); if (index >= 0) _document.Jobs[index] = job; }
    private void AuditUnsafe(string actor, string action, string entity, string detail)
    {
        var previous = _document.Audit.LastOrDefault()?.IntegrityHash ?? string.Empty;
        var entry = new AssessmentAuditEntry(Id(), DateTimeOffset.UtcNow, Limit(actor, 120), Limit(action, 120),
            Limit(entity, 160), Limit(detail, 500), previous);
        _document.Audit.Add(entry with { IntegrityHash = AuditHash(entry) });
    }
    private AssessmentDocument Load(out bool needsSave)
    {
        needsSave = false;
        var backup = _path + ".bak";
        try
        {
            if (File.Exists(_path))
            {
                var document = ReadDocument(_path);
                needsSave = Migrate(document);
                return document;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Assessment store could not be loaded: {ex.Message}");
            if (File.Exists(_path))
            {
                try
                {
                    var corrupt = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    File.Copy(_path, corrupt, false);
                }
                catch (Exception archiveError) { _logger.Warn($"Assessment corrupt store could not be archived: {archiveError.Message}"); }
            }
        }
        try
        {
            if (File.Exists(backup))
            {
                var document = ReadDocument(backup);
                Migrate(document);
                needsSave = true;
                _logger.Warn("Assessment store was recovered from the last known-good backup.");
                return document;
            }
        }
        catch (Exception ex) { _logger.Warn($"Assessment backup could not be loaded: {ex.Message}"); }
        needsSave = true;
        return new AssessmentDocument();
    }
    private AssessmentDocument ReadDocument(string path)
    {
        var document = JsonSerializer.Deserialize(File.ReadAllText(path), AssessmentJsonContext.Default.AssessmentDocument)
            ?? throw new InvalidDataException("Assessment store is empty.");
        if (document.Version is < 1 or > CurrentDocumentVersion)
            throw new InvalidDataException($"Unsupported assessment store version {document.Version}.");
        if (document.Scopes is null || document.Plans is null || document.Approvals is null || document.Jobs is null ||
            document.Evidence is null || document.Findings is null || document.Audit is null)
            throw new InvalidDataException("Assessment store contains null collections.");
        if (document.Scopes.Count > 10_000 || document.Plans.Count > 10_000 || document.Approvals.Count > 20_000 ||
            document.Jobs.Count > 50_000 || document.Evidence.Count > 100_000 || document.Findings.Count > 100_000 ||
            document.Audit.Count > 500_000)
            throw new InvalidDataException("Assessment store exceeds bounded collection limits.");
        return document;
    }
    private bool Migrate(AssessmentDocument document)
    {
        var changed = document.Version != CurrentDocumentVersion;
        UpgradeAuditChain(document);
        document.Version = CurrentDocumentVersion;
        return changed;
    }
    private bool RecoverInterruptedJobs()
    {
        var changed = false;
        foreach (var job in _document.Jobs.Where(value => value.Status is AssessmentJobStatus.Queued or AssessmentJobStatus.Running).ToArray())
        {
            ReplaceJobUnsafe(job with
            {
                Status = AssessmentJobStatus.Failed,
                FinishedAt = DateTimeOffset.UtcNow,
                Failure = "The application stopped before this job completed. The one-time approval remains consumed."
            });
            AuditUnsafe("system", "job.recover", job.Id, "application_restart");
            changed = true;
        }
        return changed;
    }
    private void SaveUnsafe(bool createBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _document.Version = CurrentDocumentVersion;
        var temporary = _path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_document, AssessmentJsonContext.Default.AssessmentDocument));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (createBackup && File.Exists(_path)) File.Copy(_path, _path + ".bak", true);
        File.Move(temporary, _path, true);
    }
    private static bool IsSafeTarget(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 253 && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or ':');
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Limit(string? value, int max) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, max)];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private string AuditHash(AssessmentAuditEntry entry)
    {
        var content = Encoding.UTF8.GetBytes(string.Join("\n", entry.Id, entry.Timestamp.ToUniversalTime().ToString("O"),
            entry.Actor, entry.Action, entry.EntityId, entry.Detail, entry.PreviousHash));
        return Convert.ToHexString(HMACSHA256.HashData(_auditKey, content)).ToLowerInvariant();
    }
    private void UpgradeAuditChain(AssessmentDocument document)
    {
        if (document.Audit.Count == 0 || document.Audit.Any(value => !string.IsNullOrEmpty(value.IntegrityHash))) return;
        var previous = string.Empty;
        for (var index = 0; index < document.Audit.Count; index++)
        {
            var upgraded = document.Audit[index] with { PreviousHash = previous, IntegrityHash = string.Empty };
            upgraded = upgraded with { IntegrityHash = AuditHash(upgraded) };
            document.Audit[index] = upgraded;
            previous = upgraded.IntegrityHash;
        }
    }
    private static byte[] LoadOrCreateAuditKey(ISecretStore secrets)
    {
        try
        {
            if (secrets.Get(AuditKeyName) is { Length: > 0 } encoded)
            {
                var existing = Convert.FromBase64String(encoded);
                if (existing.Length == 32) return existing;
            }
        }
        catch (FormatException) { }
        var created = RandomNumberGenerator.GetBytes(32);
        secrets.Set(AuditKeyName, Convert.ToBase64String(created));
        return created;
    }
    private static string NormalizeSeverity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "critical" => "Critical", "high" => "High", "medium" => "Medium", "low" => "Low", "info" or "informational" => "Info",
        _ => throw new ArgumentException("Severity must be Critical, High, Medium, Low or Info.")
    };
    private static string NormalizeConfidence(string value) => value.Trim().ToLowerInvariant() switch
    {
        "high" => "High", "medium" => "Medium", "low" => "Low",
        _ => throw new ArgumentException("Confidence must be High, Medium or Low.")
    };
    private static string MarkdownReport(AssessmentJob job, AssessmentScope? scope, AssessmentPlan? plan,
        IReadOnlyList<AssessmentEvidence> evidence, IReadOnlyList<AssessmentFinding> findings,
        IReadOnlyList<AssessmentAuditEntry> audit, AssessmentAuditVerification verification)
    {
        var builder = new StringBuilder().AppendLine("# Hackermes authorized assessment report").AppendLine()
            .AppendLine($"- Job: `{job.Id}`").AppendLine($"- Status: **{job.Status}**")
            .AppendLine($"- Scope: `{scope?.Name ?? job.ScopeId}`").AppendLine($"- Plan: `{plan?.Name ?? job.PlanId}`")
            .AppendLine($"- Audit chain: {(verification.Valid ? "valid" : "invalid")}").AppendLine()
            .AppendLine("## Findings").AppendLine();
        foreach (var finding in findings) builder.AppendLine($"- **[{finding.Severity}] {finding.Title}** — {finding.Status}; confidence {finding.Confidence}; evidence `{finding.EvidenceId}`");
        builder.AppendLine().AppendLine("## Evidence").AppendLine();
        foreach (var item in evidence) builder.AppendLine($"- `{item.Id}` — {item.Source}; SHA-256 `{item.Sha256}`; redacted={item.Redacted}");
        builder.AppendLine().AppendLine("## Audit timeline").AppendLine();
        foreach (var item in audit.OrderBy(value => value.Timestamp)) builder.AppendLine($"- {item.Timestamp:O} `{item.Actor}` {item.Action} `{item.EntityId}`");
        return builder.ToString();
    }
    private static string HtmlReport(AssessmentJob job, AssessmentScope? scope, AssessmentPlan? plan,
        IReadOnlyList<AssessmentEvidence> evidence, IReadOnlyList<AssessmentFinding> findings,
        IReadOnlyList<AssessmentAuditEntry> audit, AssessmentAuditVerification verification)
    {
        static string E(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        var builder = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Hackermes assessment report</title><body>")
            .Append("<h1>Hackermes authorized assessment report</h1><dl>")
            .Append($"<dt>Job</dt><dd><code>{E(job.Id)}</code></dd><dt>Status</dt><dd>{E(job.Status.ToString())}</dd>")
            .Append($"<dt>Scope</dt><dd>{E(scope?.Name ?? job.ScopeId)}</dd><dt>Plan</dt><dd>{E(plan?.Name ?? job.PlanId)}</dd>")
            .Append($"<dt>Audit chain</dt><dd>{(verification.Valid ? "valid" : "invalid")}</dd></dl><h2>Findings</h2><ul>");
        foreach (var finding in findings) builder.Append($"<li><strong>[{E(finding.Severity)}] {E(finding.Title)}</strong> — {E(finding.Status)}; confidence {E(finding.Confidence)}</li>");
        builder.Append("</ul><h2>Evidence</h2><ul>");
        foreach (var item in evidence) builder.Append($"<li><code>{E(item.Id)}</code> — {E(item.Source)}; SHA-256 <code>{E(item.Sha256)}</code></li>");
        builder.Append("</ul><h2>Audit timeline</h2><ul>");
        foreach (var item in audit.OrderBy(value => value.Timestamp)) builder.Append($"<li>{E(item.Timestamp.ToString("O"))} <code>{E(item.Actor)}</code> {E(item.Action)} <code>{E(item.EntityId)}</code></li>");
        return builder.Append("</ul></body></html>").ToString();
    }
}

public sealed class AssessmentDocument
{
    public int Version { get; set; } = 2;
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
[JsonSerializable(typeof(AssessmentReport))]
internal partial class AssessmentJsonContext : JsonSerializerContext { }
