using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
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
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sessionGrantLifetime;
    private readonly TimeSpan _toolCallTimeout;
    private readonly int _maxToolResultCharacters;
    private readonly ConcurrentDictionary<SessionGrantKey, DateTimeOffset> _sessionGrants = new();
    private readonly ConcurrentDictionary<SessionGrantKey, byte> _recentReadOnlyCalls = new();

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
    {
        _registry = registry;
        _policy = policy;
        _confirmation = confirmation;
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
                return Limit(ToolResult.Fail($"{invocation.ToolName} 参数准备失败: {exception.Message}"));
            }
        }

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
            return ToolResult.Fail(
                (decision.Reason ?? "操作被策略拒绝。") +
                " 不要尝试绕过或用变体参数重试；如确属授权评估必需，请向操作者说明目的，由操作者调整权限模式或手动执行。");

        if (decision.Kind == ToolPolicyDecisionKind.RequireConfirmation)
        {
            var answer = await _confirmation.ConfirmAsync(
                invocation, decision.Reason ?? "Confirmation required.", ct).ConfigureAwait(false);
            if (!answer.Approved)
                return ToolResult.Fail(
                    "操作者未批准该操作。可先降低风险（缩小范围、改为只读查询）后重新请求，" +
                    "或向操作者解释该步骤对当前评估的必要性；不要在未获批准时重复发起同一操作。");
            if (answer.RememberForSession && grantKey is { } approvedKey)
                _sessionGrants[approvedKey] = _timeProvider.GetUtcNow() + _sessionGrantLifetime;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_toolCallTimeout);
        ToolResult result;
        try
        {
            result = await tool.Handler(invocation, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ToolResult.Fail(
                $"工具 '{invocation.ToolName}' 超过 {_toolCallTimeout.TotalSeconds:0} 秒未完成，已按超时取消。" +
                " 可把步骤拆小（缩小过滤/分页/缩短等待）后重试。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{invocation.ToolName} 执行失败: {ex.Message}");
        }
        // Annotate after limiting: the hint must survive truncation of oversized results.
        return AnnotateDuplicateReadOnlyCall(tool, invocation, Limit(result));
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

        var key = CreateGrantKey(invocation, tool.Name);
        if (key is null) return result;
        var repeated = !_recentReadOnlyCalls.TryAdd(key.GetValueOrDefault(), 0);
        if (_recentReadOnlyCalls.Count > MaximumTrackedReadonlyCalls) _recentReadOnlyCalls.Clear();
        return repeated ? result with { Content = result.Content + DuplicateCallHint } : result;
    }

    /// <summary>
    /// Single exit funnel so one chatty tool cannot crowd the shared context window;
    /// the model sees an explicit marker and can page through bounded reads instead.
    /// </summary>
    private ToolResult Limit(ToolResult result)
    {
        if (result.Content.Length <= _maxToolResultCharacters) return result;
        return result with
        {
            Content = result.Content[.._maxToolResultCharacters] +
                $"\n…[已截断：仅保留前 {_maxToolResultCharacters} / {result.Content.Length} 字符。" +
                " 请改用分页、chunk 或过滤参数获取剩余部分，不要凭空补全被截断的内容。]"
        };
    }

    public void ClearSessionGrants(string sessionId)
    {
        foreach (var key in _sessionGrants.Keys)
            if (string.Equals(key.Session, sessionId, StringComparison.Ordinal)) _sessionGrants.TryRemove(key, out _);
    }

    private static SessionGrantKey? CreateGrantKey(ToolInvocation invocation, string toolName)
    {
        if (invocation.SessionId is not { Length: > 0 } sessionId) return null;

        return new SessionGrantKey(
            sessionId,
            toolName,
            invocation.PageId ?? string.Empty,
            ComputeArgumentsFingerprint(invocation.Arguments));
    }

    private static string ComputeArgumentsFingerprint(JsonElement arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonicalJson(writer, arguments);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                var properties = new List<JsonProperty>();
                foreach (var property in element.EnumerateObject()) properties.Add(property);
                properties.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, element);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(element), element.ValueKind, "Unsupported JSON value kind.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out var signed))
        {
            writer.WriteNumberValue(signed);
            return;
        }

        if (element.TryGetUInt64(out var unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(decimalValue.ToString("G29", CultureInfo.InvariantCulture));
            return;
        }

        writer.WriteRawValue(element.GetDouble().ToString("R", CultureInfo.InvariantCulture));
    }

    private readonly record struct SessionGrantKey(
        string Session,
        string Tool,
        string PageId,
        string ArgumentsFingerprint);
}
