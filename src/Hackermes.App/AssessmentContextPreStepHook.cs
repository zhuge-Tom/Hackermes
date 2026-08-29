using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.Assessment;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>
/// Injects a bounded snapshot of open assessment cases and findings into each model step.
/// Lives in App so AiPanel does not reference Assessment.
/// </summary>
public sealed class AssessmentContextPreStepHook(
    IAssessmentControlPlane plane,
    IAgentWorkspaceContext? workspace = null) : IAgentPreStepHook
{
    public ValueTask<PreStepDecision> BeforeStepAsync(PreStepInput input, CancellationToken ct)
    {
        IReadOnlyList<AssessmentCaseSummary> cases;
        try { cases = plane.ReadCasesInWorkspace(workspace?.CurrentId ?? string.Empty, 8); }
        catch { return ValueTask.FromResult(PreStepDecision.Proceed); }
        if (cases.Count == 0) return ValueTask.FromResult(PreStepDecision.Proceed);

        var builder = new StringBuilder("【上下文注入·评估案件】\n");
        foreach (var item in cases.Take(8))
        {
            var adapter = item.Plan.Steps.Count > 0 ? item.Plan.Steps[0].AdapterId : "-";
            IReadOnlyList<AssessmentFinding> findings;
            IReadOnlyList<AssessmentEvidence> evidence;
            try
            {
                findings = plane.Findings(item.Job.Id);
                evidence = plane.Evidence(item.Job.Id);
            }
            catch
            {
                continue;
            }
            var targets = string.Join(',', item.Scope.Targets.Take(4));
            builder.Append("job=").Append(item.Job.Id)
                .Append(' ').Append(item.Job.Status)
                .Append(" scope=").Append(item.Scope.Name)
                .Append(" targets=").Append(targets)
                .Append(" adapter=").Append(adapter)
                .Append(" evidence=").Append(evidence.Count)
                .Append(" findings=").Append(findings.Count)
                .AppendLine();
            if (item.Scope.Targets.Contains("*"))
                builder.AppendLine("  全部授权：可自行探测精确主机（dns/httpx/headers/get），再对发现的主机做有界扫描；不要因为没有附着页面或只有通配而拒绝。");
            else if (item.Scope.Targets.Any(value => value.StartsWith("*.", StringComparison.Ordinal)))
                builder.AppendLine("  通配授权 " + string.Join(',', item.Scope.Targets.Where(value => value.StartsWith("*.", StringComparison.Ordinal))) +
                                  "：对该后缀下的主机做有界探测，不要要求再给单条 URL。");
            foreach (var finding in findings.Take(4))
                builder.Append("  [").Append(finding.Severity).Append("] ")
                    .Append(finding.Title)
                    .Append(" (").Append(finding.Status).Append(") evidence=")
                    .Append(finding.EvidenceId).AppendLine();
            if (builder.Length > 1_800) break;
        }

        var text = builder.ToString().TrimEnd();
        if (text.Length > 2_000) text = text[..1_999] + "…";
        return ValueTask.FromResult(PreStepDecision.AppendEphemeral(
        [
            new ChatMessage("user", text)
        ]));
    }
}
