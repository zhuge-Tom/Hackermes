using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Tools;

public enum AiToolRisk { ReadOnly, Mutating, Dangerous }

public sealed record AiToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    AiToolRisk Risk,
    Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> Handler,
    Func<ToolInvocation, CancellationToken, ValueTask<ToolInvocation>>? Prepare = null);

public sealed record ToolInvocation(
    string ToolName,
    JsonElement Arguments,
    string? PageId = null,
    string? SessionId = null);

public sealed record ToolResult(bool Success, string Content)
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
