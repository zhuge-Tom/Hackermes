using Hackermes.AiPanel.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

public enum AgentTodoStatus { Pending, InProgress, Completed }

/// <summary>One durable checklist entry; the whole list is replaced on every write (dsh todo lineage).</summary>
public sealed record AgentTodoItem(string Content, AgentTodoStatus Status);

/// <summary>
/// Holds the model-authored task checklist. Deliberately session-transient like dsh's
/// todo/write: incomplete items survive the next turn; completed items drop, giving the
/// model a compact durable plan instead of re-narrating tasks in messages.
/// </summary>
public sealed class AgentTodoRegistry
{
    private const int MaxItems = 64;
    private const int MaxContentLength = 500;
    private readonly object _gate = new();
    private List<AgentTodoItem> _items = [];

    /// <summary>Raised after every accepted write or turn rollover.</summary>
    public event Action<IReadOnlyList<AgentTodoItem>>? Changed;

    public IReadOnlyList<AgentTodoItem> Current
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    /// <summary>Drops completed items; pending and in-progress items survive into the next turn.</summary>
    public void BeginTurn()
    {
        lock (_gate)
        {
            var kept = _items.FindAll(item => item.Status is not AgentTodoStatus.Completed);
            if (kept.Count == _items.Count) return;
            _items = kept;
        }
        Publish();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return;
            _items = [];
        }
        Publish();
    }

    /// <summary>Validates and replaces the whole list. Returns a model-facing result.</summary>
    public ToolResult Write(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("todos", out var todos))
            return ToolResult.Fail("todos 必须是数组，每项包含 content 与 status（pending|in_progress|completed）。每次调用都要提交完整列表。");

        if (todos.ValueKind == JsonValueKind.String)
        {
            // 非 strict 提供方偶尔把数组编码成字符串 —— 接受并解析。
            try
            {
                using var parsed = JsonDocument.Parse(todos.GetString() ?? string.Empty);
                todos = parsed.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                return ToolResult.Fail($"todos 不是有效 JSON: {exception.Message}");
            }
        }

        if (todos.ValueKind != JsonValueKind.Array)
            return ToolResult.Fail("todos 必须是数组；传空数组表示清空清单。");

        var parsedItems = new List<AgentTodoItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in todos.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
                return ToolResult.Fail("每个 todo 都需要字符串 content 字段。");
            var content = (contentElement.GetString() ?? string.Empty).Trim();
            if (content.Length == 0) return ToolResult.Fail("todo 内容不能为空。");
            if (content.Length > MaxContentLength)
                return ToolResult.Fail($"todo 内容过长（>{MaxContentLength} 字符）；请拆分为多条或精简。");
            if (!seen.Add(content))
                return ToolResult.Fail($"重复的 todo 内容：\"{content}\"。请合并或删除重复项。");

            var status = AgentTodoStatus.Pending;
            if (item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            {
                var rawStatus = (statusElement.GetString() ?? "pending").ToLowerInvariant();
                status = rawStatus switch
                {
                    "pending" => AgentTodoStatus.Pending,
                    "in_progress" => AgentTodoStatus.InProgress,
                    "inprogress" => AgentTodoStatus.InProgress,
                    "completed" => AgentTodoStatus.Completed,
                    _ => throw new FormatException($"未知的 todo 状态 \"{rawStatus}\"；可用 pending|in_progress|completed。"),
                };
            }
            parsedItems.Add(new AgentTodoItem(content, status));
        }

        if (parsedItems.Count > MaxItems)
            return ToolResult.Fail($"清单最多 {MaxItems} 项；请合并细粒度条目。");
        var inProgress = parsedItems.Count(item => item.Status == AgentTodoStatus.InProgress);
        if (inProgress > 1)
            return ToolResult.Fail("同一时刻只允许一个 in_progress 条目；请把其余改为 pending 或 completed。");

        lock (_gate) _items = parsedItems;
        Publish();
        return ToolResult.Ok(
            $"已更新任务清单：{parsedItems.Count(item => item.Status == AgentTodoStatus.Pending)} 待办、" +
            $"{inProgress} 进行中、{parsedItems.Count(item => item.Status == AgentTodoStatus.Completed)} 已完成。" +
            (inProgress == 0 && parsedItems.Count > 0 ? " 请把当前正在执行的条目标记为 in_progress。" : string.Empty));
    }

    private void Publish()
    {
        var handlers = Changed;
        if (handlers is null) return;
        var snapshot = Current;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<IReadOnlyList<AgentTodoItem>>)handler)(snapshot); }
            catch { /* listener failures never break the tool */ }
        }
    }
}

/// <summary>Model-facing todo_write tool: whole-list snapshot writes only.</summary>
public sealed class AgentTodoToolAdapter(AgentTodoRegistry registry)
{
    public void RegisterAll(IAiToolRegistry toolRegistry)
    {
        toolRegistry.Register(new AiToolDefinition(
            "todo_write",
            "Maintain the session task checklist. Submit the ENTIRE list every call (no partial updates); " +
            "pass an empty array to clear it. Keep at most one item in_progress. Use it to plan multi-step " +
            "work instead of narrating the plan in chat text.",
            Schema(new
            {
                todos = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            content = new { type = "string", description = "imperative, specific task line" },
                            status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } }
                        },
                        required = new[] { "content" },
                        additionalProperties = false
                    }
                }
            }), AiToolRisk.ReadOnly,
            (call, _) =>
            {
                try { return ValueTask.FromResult(registry.Write(call.Arguments)); }
                catch (FormatException ex) { return ValueTask.FromResult(ToolResult.Fail(ex.Message)); }
            }));
    }

    private static JsonElement Schema(object properties) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            additionalProperties = false
        });
}
