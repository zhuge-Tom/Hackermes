using Hackermes.AiPanel.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Filesystem-backed spill store: locators are random tokens resolved strictly inside the
/// spill root under the application data directory, so the model can neither address
/// arbitrary files nor escape the store.
/// </summary>
public sealed class AgentSpillStore : IAgentSpillStore
{
    private readonly Func<string> _rootDirectoryFactory;
    private readonly object _gate = new();

    public AgentSpillStore(Func<string> rootDirectoryFactory)
    {
        _rootDirectoryFactory = rootDirectoryFactory;
    }

    private string Root => Path.Combine(_rootDirectoryFactory(), "agent-spills");

    public string Save(string sessionId, string toolName, string content)
    {
        var token = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Root, Sanitize(sessionId is { Length: > 0 } ? sessionId : "shared"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, token + ".txt"), content ?? string.Empty);
        return $"spill:{token}";
    }

    public string? Read(string locator, int offset, int limit)
    {
        var token = ParseToken(locator);
        if (token is null) return null;
        string? path;
        lock (_gate)
        {
            path = Directory.EnumerateFiles(Root, token + ".txt", SearchOption.AllDirectories).FirstOrDefault();
        }
        if (path is null) return null;
        try
        {
            var content = File.ReadAllText(path);
            if (offset < 0) offset = 0;
            if (offset >= content.Length) return "（偏移超出结果长度。）";
            limit = Math.Clamp(limit <= 0 ? 12_000 : limit, 1, 48_000);
            var slice = content[offset..Math.Min(content.Length, offset + limit)];
            var remaining = content.Length - offset - slice.Length;
            return remaining > 0
                ? slice + $"\n…[剩余约 {remaining:N0} 字符；用更大的 offset 继续读取。]"
                : slice;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ParseToken(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return null;
        var trimmed = locator.Trim();
        const string prefix = "spill:";
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = trimmed[prefix.Length..];
        if (token.Length != 32 || token.Any(character => !Uri.IsHexDigit(character))) return null;
        return token;
    }

    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(segment.Length);
        foreach (var character in segment)
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        return builder.ToString();
    }
}

/// <summary>Model-facing reader for spilled tool results.</summary>
public sealed class AgentSpillToolAdapter(IAgentSpillStore store)
{
    public void RegisterAll(IAiToolRegistry toolRegistry)
    {
        toolRegistry.Register(new AiToolDefinition(
            "read_spill",
            "Read a bounded slice of a full tool result that was stored off-context (locators appear in " +
            "truncated results as 'spill:<id>'). Page through large evidence with offset/limit instead of " +
            "requesting everything at once.",
            Schema(new
            {
                @locator = new { type = "string", description = "locator from a truncated result, e.g. spill:ab12…" },
                offset = new { type = "integer", description = "optional, default 0" },
                limit = new { type = "integer", description = "optional characters per read, default 12000" }
            }), AiToolRisk.ReadOnly,
            (call, _) =>
            {
                var locator = Text(call.Arguments, "locator");
                if (locator.Length == 0)
                    return ValueTask.FromResult(ToolResult.Fail("需要 spill: 格式的 locator 参数。"));
                var content = store.Read(locator, Number(call.Arguments, "offset", 0), Number(call.Arguments, "limit", 12_000));
                return ValueTask.FromResult(content is null
                    ? ToolResult.Fail($"locator 无效或已过期：{locator}。外存内容随应用数据目录保留，重启后仍可读取。")
                    : ToolResult.Ok(content));
            }));
    }

    private static JsonElement Schema(object properties) =>
        JsonSerializer.SerializeToElement(new { type = "object", properties, additionalProperties = false });

    private static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement arguments, string name, int fallback) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : fallback;
}
