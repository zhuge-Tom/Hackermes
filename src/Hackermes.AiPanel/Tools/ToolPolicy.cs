using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Hackermes.Platform.Models;

namespace Hackermes.AiPanel.Tools;

public enum ToolPolicyDecisionKind { Allow, RequireConfirmation, Deny }

public sealed record ToolPolicyDecision(ToolPolicyDecisionKind Kind, string? Reason = null)
{
    public static ToolPolicyDecision Allow() => new(ToolPolicyDecisionKind.Allow);
    public static ToolPolicyDecision Confirm(string reason) => new(ToolPolicyDecisionKind.RequireConfirmation, reason);
    public static ToolPolicyDecision Deny(string reason) => new(ToolPolicyDecisionKind.Deny, reason);
}

public interface IToolPolicyGate
{
    ValueTask<ToolPolicyDecision> EvaluateAsync(
        AiToolDefinition tool, ToolInvocation invocation, CancellationToken ct);
}

/// <summary>
/// One policy point for every Agent tool. The default mirrors Codex's request-approval
/// posture; the UI and CLI only select a mode, they do not bypass this gate.
/// </summary>
public sealed class DefaultToolPolicyGate : IToolPolicyGate
{
    public AiPermissionMode Mode { get; private set; } = AiPermissionMode.RequestApproval;

    public void SetMode(AiPermissionMode mode) => Mode = mode;

    public ValueTask<ToolPolicyDecision> EvaluateAsync(
        AiToolDefinition tool, ToolInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = invocation.Arguments.ToString();
        if (LooksIrrecoverablyDestructive(tool.Name, payload))
            return ValueTask.FromResult(ToolPolicyDecision.Deny("操作命中不可恢复的系统破坏模式，已拒绝执行。"));

        var decision = Mode switch
        {
            AiPermissionMode.RequestApproval => tool.Risk == AiToolRisk.ReadOnly
                ? ToolPolicyDecision.Allow()
                : ToolPolicyDecision.Confirm($"'{tool.Name}' may change external state or use the network."),
            AiPermissionMode.HelpApproval => tool.Risk == AiToolRisk.Dangerous
                ? ToolPolicyDecision.Confirm($"'{tool.Name}' is classified as high risk." )
                : ToolPolicyDecision.Allow(),
            AiPermissionMode.FullAccess => ToolPolicyDecision.Allow(),
            _ => ToolPolicyDecision.Confirm("Unknown Agent permission mode.")
        };

        return ValueTask.FromResult(decision);
    }

    private static bool LooksIrrecoverablyDestructive(string toolName, string payload)
    {
        var text = (toolName + " " + payload).ToLowerInvariant();
        string[] patterns =
        [
            "rm -rf", "remove-item", "format ", "format.com", "diskpart",
            "reg delete", "cipher /w", "clear-disk", "remove-partition"
        ];
        return Array.Exists(patterns, text.Contains);
    }
}

public sealed record ToolConfirmation(bool Approved, bool RememberForSession = false);

public interface IToolConfirmationService
{
    ValueTask<ToolConfirmation> ConfirmAsync(
        ToolInvocation invocation, string reason, CancellationToken ct);
}

/// <summary>Safe headless default. A UI confirmation service replaces this at application composition time.</summary>
public sealed class RejectingToolConfirmationService : IToolConfirmationService
{
    public ValueTask<ToolConfirmation> ConfirmAsync(
        ToolInvocation invocation, string reason, CancellationToken ct) =>
        ValueTask.FromResult(new ToolConfirmation(false));
}

public sealed class AiToolDispatcher
{
    private readonly IAiToolRegistry _registry;
    private readonly IToolPolicyGate _policy;
    private readonly IToolConfirmationService _confirmation;
    private readonly ConcurrentDictionary<(string Session, string Tool), byte> _sessionGrants = new();

    public AiToolDispatcher(IAiToolRegistry registry, IToolPolicyGate policy, IToolConfirmationService confirmation)
    {
        _registry = registry;
        _policy = policy;
        _confirmation = confirmation;
    }

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_registry.TryGet(invocation.ToolName, out var tool) || tool is null)
            return ToolResult.Fail($"Unknown AI tool: {invocation.ToolName}");

        var hasGrant = invocation.SessionId is { Length: > 0 } session
            && _sessionGrants.ContainsKey((session, tool.Name));
        var decision = hasGrant
            ? ToolPolicyDecision.Allow()
            : await _policy.EvaluateAsync(tool, invocation, ct).ConfigureAwait(false);

        if (decision.Kind == ToolPolicyDecisionKind.Deny)
            return ToolResult.Fail(decision.Reason ?? "Tool invocation denied by policy.");

        if (decision.Kind == ToolPolicyDecisionKind.RequireConfirmation)
        {
            var answer = await _confirmation.ConfirmAsync(
                invocation, decision.Reason ?? "Confirmation required.", ct).ConfigureAwait(false);
            if (!answer.Approved) return ToolResult.Fail("Tool invocation was not approved.");
            if (answer.RememberForSession && invocation.SessionId is { Length: > 0 } id)
                _sessionGrants.TryAdd((id, tool.Name), 0);
        }

        try { return await tool.Handler(invocation, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ToolResult.Fail(ex.Message); }
    }

    public void ClearSessionGrants(string sessionId)
    {
        foreach (var key in _sessionGrants.Keys)
            if (string.Equals(key.Session, sessionId, StringComparison.Ordinal)) _sessionGrants.TryRemove(key, out _);
    }
}
