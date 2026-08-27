using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Tools;

public enum AiToolRisk { ReadOnly, Mutating, Dangerous }

public sealed record ChatImage(string MimeType, string Base64);

public sealed record AiToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    AiToolRisk Risk,
    Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> Handler,
    Func<ToolInvocation, CancellationToken, ValueTask<ToolInvocation>>? Prepare = null,
    JsonElement? OutputSchema = null,
    TimeSpan? Timeout = null);

public sealed record ToolInvocation(
    string ToolName,
    JsonElement Arguments,
    string? PageId = null,
    string? SessionId = null,
    string? CallId = null);

/// <summary>
/// Result of one tool invocation. <see cref="AdditionalContexts"/> carries follow-up context
/// the runner injects into the next step's request (dsh additionalContexts lineage, kept out
/// of the tool output itself); <see cref="ConcludesTurn"/> ends the turn once this call and
/// its siblings settle (dsh concludeTurn).
/// </summary>
public sealed record ToolResult(
    bool Success,
    string Content,
    IReadOnlyList<string>? AdditionalContexts = null,
    bool ConcludesTurn = false,
    IReadOnlyList<ChatImage>? Images = null)
{
    public static ToolResult Ok(string content = "") => new(true, content);
    public static ToolResult Fail(string error) => new(false, error);
}

public interface IAiToolRegistry
{
    IReadOnlyList<AiToolDefinition> All { get; }
    void Register(AiToolDefinition definition);
    bool TryGet(string name, out AiToolDefinition? definition);
}

/// <summary>
/// Off-log storage for oversized tool results (dsh spill lineage): the full text survives on
/// disk while the model receives a bounded preview plus an opaque locator token. Locators are
/// random hex tokens resolvable strictly inside the store root.
/// </summary>
public interface IAgentSpillStore
{
    /// <summary>Persists the full content and returns a locator token for the model.</summary>
    string Save(string sessionId, string toolName, string content);

    /// <summary>Reads back a bounded slice; null when the locator is unknown/expired.</summary>
    string? Read(string locator, int offset, int limit);
}

public sealed class AiToolRegistry : IAiToolRegistry
{
    private readonly Dictionary<string, AiToolDefinition> _tools = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyList<AiToolDefinition> All
    {
        get { lock (_gate) return [.. _tools.Values]; }
    }

    public void Register(AiToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (!_tools.TryAdd(definition.Name, definition))
                throw new InvalidOperationException($"AI tool '{definition.Name}' is already registered.");
        }
    }

    public bool TryGet(string name, out AiToolDefinition? definition)
    {
        lock (_gate) return _tools.TryGetValue(name, out definition);
    }
}
