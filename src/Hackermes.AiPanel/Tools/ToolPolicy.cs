using System;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
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

/// <summary>
/// One durable approval-audit fact: who (session/page/scope) asked for what, and how the
/// policy/operated decided. Emitted by the dispatcher and appended to the agent session log
/// so approval history survives restarts instead of living only in session grants.
/// </summary>
public sealed record AiToolAuditRecord(
    DateTimeOffset Time,
    string Tool,
    string? SessionId,
    string? PageId,
    string Decision,
    string Reason);

/// <summary>Safe headless default. A UI confirmation service replaces this at application composition time.</summary>
public sealed class RejectingToolConfirmationService : IToolConfirmationService
{
    public ValueTask<ToolConfirmation> ConfirmAsync(
        ToolInvocation invocation, string reason, CancellationToken ct) =>
        ValueTask.FromResult(new ToolConfirmation(false));
}

public sealed class AiToolDispatcher
{
    public static readonly TimeSpan DefaultSessionGrantLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultToolCallTimeout = TimeSpan.FromSeconds(120);
    public const int DefaultMaxToolResultCharacters = 12_000;
    /// <summary>Bounded memory for the duplicate read-only call detector; cleared wholesale when full.</summary>
    private const int MaximumTrackedReadonlyCalls = 256;
    private const string DuplicateCallHint =
        "\n[提示] 这次只读调用的工具与参数和之前完全一致，结果不会变化。" +
        " 请调整参数（例如分页 offset/limit、过滤条件、chunk 范围）或改用其他工具推进任务。";
    private readonly IAiToolRegistry _registry;
    private readonly IToolPolicyGate _policy;
    private readonly IToolConfirmationService _confirmation;
    private readonly IAgentSpillStore? _spill;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sessionGrantLifetime;
    private readonly TimeSpan _toolCallTimeout;
    private readonly int _maxToolResultCharacters;
    private readonly ConcurrentDictionary<SessionGrantKey, DateTimeOffset> _sessionGrants = new();
    private readonly ConcurrentDictionary<ReadonlyCallKey, byte> _recentReadOnlyCalls = new();

    /// <summary>Raised for every approval-relevant decision (confirmations, denials, session grants).</summary>
    public event Action<AiToolAuditRecord>? Audited;

    public AiToolDispatcher(IAiToolRegistry registry, IToolPolicyGate policy, IToolConfirmationService confirmation)
        : this(registry, policy, confirmation, TimeProvider.System, DefaultSessionGrantLifetime)
    {
    }

    public AiToolDispatcher(
        IAiToolRegistry registry,
        IToolPolicyGate policy,
        IToolConfirmationService confirmation,
        TimeProvider timeProvider,
        TimeSpan sessionGrantLifetime)
        : this(registry, policy, confirmation, timeProvider, sessionGrantLifetime,
              DefaultMaxToolResultCharacters, DefaultToolCallTimeout)
    {
    }

    public AiToolDispatcher(
        IAiToolRegistry registry,
        IToolPolicyGate policy,
        IToolConfirmationService confirmation,
        TimeProvider timeProvider,
        TimeSpan sessionGrantLifetime,
        int maxToolResultCharacters,
        TimeSpan toolCallTimeout)
        : this(registry, policy, confirmation, timeProvider, sessionGrantLifetime,
              maxToolResultCharacters, toolCallTimeout, spillStore: null)
    {
    }

    public AiToolDispatcher(
        IAiToolRegistry registry,
        IToolPolicyGate policy,
        IToolConfirmationService confirmation,
        TimeProvider timeProvider,
        TimeSpan sessionGrantLifetime,
        int maxToolResultCharacters,
        TimeSpan toolCallTimeout,
        IAgentSpillStore? spillStore)
    {
        _registry = registry;
        _policy = policy;
        _confirmation = confirmation;
        _spill = spillStore;
        _timeProvider = timeProvider;
        if (sessionGrantLifetime <= TimeSpan.Zero || sessionGrantLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(sessionGrantLifetime),
                "Session grant lifetime must be greater than zero and no more than 24 hours.");
        _sessionGrantLifetime = sessionGrantLifetime;
        if (maxToolResultCharacters < 256)
            throw new ArgumentOutOfRangeException(nameof(maxToolResultCharacters),
                "Tool result budget must be at least 256 characters.");
        _maxToolResultCharacters = maxToolResultCharacters;
        if (toolCallTimeout <= TimeSpan.Zero || toolCallTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(toolCallTimeout),
                "Tool call timeout must be greater than zero and no more than one hour.");
        _toolCallTimeout = toolCallTimeout;
    }

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_registry.TryGet(invocation.ToolName, out var tool) || tool is null)
            return ToolResult.Fail(
                $"Unknown AI tool: {invocation.ToolName}。请改用工具列表中的确切名称，不要自行发明工具名。");

        if (tool.Prepare is not null)
        {
            try { invocation = await tool.Prepare(invocation, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                return Limit(ToolResult.Fail($"{invocation.ToolName} 参数准备失败: {exception.Message}"), invocation);
            }
        }

        if (invocation.Arguments.ValueKind != JsonValueKind.Object)
            return ToolResult.Fail("工具参数必须是 JSON 对象，且包含 schema 声明的必填字段。");

        var inputViolation = ToolOutputValidator.Validate(invocation.Arguments.GetRawText(), tool.InputSchema);
        if (inputViolation is not null && !AcceptsLegacyArguments(invocation.Arguments, inputViolation))
            return ToolResult.Fail(
                $"工具 '{tool.Name}' 参数不符合模式：{inputViolation} 请按 schema 补齐必填字段后重试，不要改用未声明的参数名。");

        var grantKey = CreateGrantKey(invocation, tool.Name);
        var now = _timeProvider.GetUtcNow();
        var hasGrant = grantKey is { } key
            && _sessionGrants.TryGetValue(key, out var expiresAt)
            && expiresAt > now;
        if (!hasGrant && grantKey is { } expiredKey)
            _sessionGrants.TryRemove(expiredKey, out _);
        var decision = hasGrant
            ? ToolPolicyDecision.Allow()
            : await _policy.EvaluateAsync(tool, invocation, ct).ConfigureAwait(false);

        if (decision.Kind == ToolPolicyDecisionKind.Deny)
        {
            Audit(invocation, "Denied", decision.Reason ?? "操作被策略拒绝。");
            return ToolResult.Fail(
                (decision.Reason ?? "操作被策略拒绝。") +
                " 不要尝试绕过或用变体参数重试；如确属授权评估必需，请向操作者说明目的，由操作者调整权限模式或手动执行。");
        }

        if (decision.Kind == ToolPolicyDecisionKind.RequireConfirmation)
        {
            var answer = await _confirmation.ConfirmAsync(
                invocation, decision.Reason ?? "Confirmation required.", ct).ConfigureAwait(false);
            if (!answer.Approved)
            {
                Audit(invocation, "RejectedByOperator", decision.Reason ?? "Confirmation required.");
                return ToolResult.Fail(
                    "操作者未批准该操作。可先降低风险（缩小范围、改为只读查询）后重新请求，" +
                    "或向操作者解释该步骤对当前评估的必要性；不要在未获批准时重复发起同一操作。");
            }
            if (answer.RememberForSession && grantKey is { } approvedKey)
            {
                _sessionGrants[approvedKey] = _timeProvider.GetUtcNow() + _sessionGrantLifetime;
                Audit(invocation, "ApprovedWithSessionGrant", decision.Reason ?? string.Empty);
            }
            else
            {
                Audit(invocation, "ApprovedOnce", decision.Reason ?? string.Empty);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = tool.Timeout is { } requested && requested > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Clamp(requested.TotalSeconds, 5, 3_600))
            : _toolCallTimeout;
        timeoutCts.CancelAfter(timeout);
        ToolResult result;
        try
        {
            result = await tool.Handler(invocation, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ToolResult.Fail(
                $"工具 '{invocation.ToolName}' 超过 {timeout.TotalSeconds:0} 秒未完成，已按超时取消。" +
                " 可把步骤拆小（缩小过滤/分页/缩短等待）后重试。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{invocation.ToolName} 执行失败: {ex.Message}");
        }

        // Declared-output contract (dsh INVALID_TOOL_OUTPUT): a schema-declaring tool that
        // returns non-conforming JSON fails here so the model self-corrects immediately
        // instead of building evidence on malformed output.
        if (result.Success && tool.OutputSchema is { } outputSchema)
        {
            var violation = ToolOutputValidator.Validate(result.Content, outputSchema);
            if (violation is not null)
                result = ToolResult.Fail(
                    $"[{ToolOutputValidator.InvalidOutputCode}] 工具 '{invocation.ToolName}' 的输出不符合其声明模式：{violation}" +
                    " 请修正参数后重试，或改用其他工具获取该信息。");
        }
        // Annotate after limiting: the hint must survive truncation of oversized results.
        return AnnotateDuplicateReadOnlyCall(tool, invocation, Limit(result, invocation));
    }

    /// <summary>
    /// Repeating an identical read-only call cannot change anything; the corrective hint
    /// steers the model toward paging, filtering or a different tool instead of looping.
    /// Mutating/Dangerous calls are never annotated (re-execution may be legitimate).
    /// </summary>
    private ToolResult AnnotateDuplicateReadOnlyCall(AiToolDefinition tool, ToolInvocation invocation, ToolResult result)
    {
        if (tool.Risk != AiToolRisk.ReadOnly || string.IsNullOrEmpty(invocation.SessionId))
            return result;

        var key = new ReadonlyCallKey(
            invocation.SessionId!, tool.Name, invocation.PageId ?? string.Empty,
            invocation.Arguments.GetRawText());
        var repeated = !_recentReadOnlyCalls.TryAdd(key, 0);
        if (_recentReadOnlyCalls.Count > MaximumTrackedReadonlyCalls) _recentReadOnlyCalls.Clear();
        return repeated ? result with { Content = result.Content + DuplicateCallHint } : result;
    }

    /// <summary>
    /// Single exit funnel so one chatty tool cannot crowd the shared context window.
    /// Two-stage policy (dsh spill-policy + tool-result-pruner lineage): beyond the spill
    /// threshold the full text is stored off-context and the model gets a head/tail preview
    /// with a <c>read_spill</c> locator; otherwise the middle is elided with explicit markers.
    /// Evidence lives in both the opening summary and the closing totals/errors, so the tail
    /// is always retained. The paging notice length is reserved inside the budget.
    /// </summary>
    private ToolResult Limit(ToolResult result, ToolInvocation invocation)
    {
        var length = result.Content.Length;
        if (length <= _maxToolResultCharacters) return result;

        if (_spill is { } spill && length > DefaultSpillThresholdCharacters &&
            invocation.SessionId is { Length: > 0 })
        {
            try
            {
                var locator = spill.Save(invocation.SessionId, invocation.ToolName, result.Content);
                var head = result.Content[..Math.Min(DefaultSpillPreviewHeadCharacters, length)];
                var tailStart = Math.Max(head.Length, length - DefaultSpillPreviewTailCharacters);
                var tail = result.Content[tailStart..];
                return result with
                {
                    Content = head +
                        $"\n\n[…完整结果共 {length:N0} 字符已外存：{locator}。" +
                        " 用 read_spill 工具按 offset/limit 分页读取，不要一次读完。]\n\n" +
                        tail +
                        "\n…[已截断：完整内容见上方 locator。]"
                };
            }
            catch (IOException)
            {
                // Storage failed: fall through to destructive truncation (best-effort spill).
            }
        }

        var notice =
            $"\n…[已截断：仅保留首尾片段 / {length} 字符。" +
            " 请改用分页、chunk 或过滤参数获取剩余部分，不要凭空补全被截断的内容。]";
        var tailKeep = Math.Clamp(_maxToolResultCharacters / 6, 64, 4_096);
        var headKeep = Math.Max(64, _maxToolResultCharacters - tailKeep - notice.Length - 32);
        if (headKeep + tailKeep >= length)
        {
            // Degenerate budgets: fall back to a plain head cut so output still shrinks.
            return result with
            {
                Content = result.Content[..Math.Max(1, _maxToolResultCharacters - notice.Length)] + notice
            };
        }
        var omitted = length - headKeep - tailKeep;
        return result with
        {
            Content = result.Content[..headKeep] +
                $"\n\n[…中间已省略约 {omitted:N0} 字符…]\n\n" +
                result.Content[^tailKeep..] + notice
        };
    }

    /// <summary>Above this size a successful result is spilled instead of destructively truncated.</summary>
    public const int DefaultSpillThresholdCharacters = 24_000;
    public const int DefaultSpillPreviewHeadCharacters = 4_000;
    public const int DefaultSpillPreviewTailCharacters = 1_000;

    /// <summary>Contained fan-out: audit listeners must never break tool execution.</summary>
    private void Audit(ToolInvocation invocation, string decision, string reason)
    {
        var handlers = Audited;
        if (handlers is null) return;
        var record = new AiToolAuditRecord(
            _timeProvider.GetUtcNow(), invocation.ToolName, invocation.SessionId,
            invocation.PageId, decision, reason);
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<AiToolAuditRecord>)handler)(record); }
            catch { /* listener failures never propagate */ }
        }
    }

    public void ClearSessionGrants(string sessionId)
    {
        foreach (var key in _sessionGrants.Keys)
            if (string.Equals(key.Session, sessionId, StringComparison.Ordinal)) _sessionGrants.TryRemove(key, out _);
    }

    private static bool AcceptsLegacyArguments(JsonElement arguments, string violation) =>
        violation.Contains("缺少必填属性", StringComparison.Ordinal) &&
        arguments.TryGetProperty("arguments", out var legacy) &&
        legacy.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(legacy.GetString());

    private static SessionGrantKey? CreateGrantKey(ToolInvocation invocation, string toolName)
    {
        if (invocation.SessionId is not { Length: > 0 } sessionId) return null;

        return new SessionGrantKey(
            sessionId,
            toolName,
            invocation.PageId ?? string.Empty,
            AuthorizationScope(invocation.Arguments));
    }

    private static string AuthorizationScope(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return string.Empty;
        if (arguments.TryGetProperty("__hackermesPageBinding", out var binding) &&
            binding.ValueKind == JsonValueKind.Object)
        {
            if (binding.TryGetProperty("Target", out var target) && target.ValueKind == JsonValueKind.String)
                return target.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            if (binding.TryGetProperty("Origin", out var origin) && origin.ValueKind == JsonValueKind.String)
                return origin.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        }
        return arguments.TryGetProperty("target", out var explicitTarget) &&
               explicitTarget.ValueKind == JsonValueKind.String
            ? explicitTarget.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;
    }

    private readonly record struct SessionGrantKey(
        string Session,
        string Tool,
        string PageId,
        string AuthorizationScope);

    private readonly record struct ReadonlyCallKey(
        string Session,
        string Tool,
        string PageId,
        string ArgumentsJson);
}
