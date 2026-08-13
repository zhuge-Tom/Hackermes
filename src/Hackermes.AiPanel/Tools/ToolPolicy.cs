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
    private readonly IAiToolRegistry _registry;
    private readonly IToolPolicyGate _policy;
    private readonly IToolConfirmationService _confirmation;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sessionGrantLifetime;
    private readonly ConcurrentDictionary<SessionGrantKey, DateTimeOffset> _sessionGrants = new();

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
    {
        _registry = registry;
        _policy = policy;
        _confirmation = confirmation;
        _timeProvider = timeProvider;
        if (sessionGrantLifetime <= TimeSpan.Zero || sessionGrantLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(sessionGrantLifetime),
                "Session grant lifetime must be greater than zero and no more than 24 hours.");
        _sessionGrantLifetime = sessionGrantLifetime;
    }

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_registry.TryGet(invocation.ToolName, out var tool) || tool is null)
            return ToolResult.Fail($"Unknown AI tool: {invocation.ToolName}");

        if (tool.Prepare is not null)
        {
            try { invocation = await tool.Prepare(invocation, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { return ToolResult.Fail(exception.Message); }
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
            return ToolResult.Fail(decision.Reason ?? "Tool invocation denied by policy.");

        if (decision.Kind == ToolPolicyDecisionKind.RequireConfirmation)
        {
            var answer = await _confirmation.ConfirmAsync(
                invocation, decision.Reason ?? "Confirmation required.", ct).ConfigureAwait(false);
            if (!answer.Approved) return ToolResult.Fail("Tool invocation was not approved.");
            if (answer.RememberForSession && grantKey is { } approvedKey)
                _sessionGrants[approvedKey] = _timeProvider.GetUtcNow() + _sessionGrantLifetime;
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
