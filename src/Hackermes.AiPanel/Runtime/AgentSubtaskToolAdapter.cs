using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Bounded in-process subtask: same policy gate, at most 8 tool rounds, no nesting, cannot widen tools.
/// </summary>
public sealed class AgentSubtaskToolAdapter(
    IOpenAiChatClient client,
    AiToolDispatcher dispatcher,
    Func<AiSettings> settings,
    Func<IReadOnlyList<AiToolDefinition>> tools,
    Func<string> model,
    IAppLogger? logger = null)
{
    public const string ToolName = "agent_subtask";
    private const int MaxNestedRounds = 8;
    private static readonly AsyncLocal<int> Depth = new();

    public void RegisterAll(IAiToolRegistry registry)
    {
        registry.Register(new AiToolDefinition(
            ToolName,
            "Run a focused nested investigation with the same policy gate. Max 8 tool rounds. Cannot nest. Optional tools list only narrows the parent surface.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    goal = new { type = "string", description = "Single concrete sub-goal" },
                    tools = new { type = "array", items = new { type = "string" }, description = "Optional allow-list; cannot add tools the parent does not have" }
                },
                required = new[] { "goal" },
                additionalProperties = false
            }),
            AiToolRisk.Mutating,
            RunAsync,
            Timeout: TimeSpan.FromSeconds(180)));
    }

    private async ValueTask<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (Depth.Value > 0)
            return ToolResult.Fail("已在子任务中，禁止再嵌套 agent_subtask。请直接使用当前工具推进。");
        var goal = invocation.Arguments.TryGetProperty("goal", out var goalElement)
            ? goalElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (goal.Length == 0) return ToolResult.Fail("goal 不能为空。");

        var requested = ReadToolFilter(invocation.Arguments);
        var parent = tools().Where(tool => !string.Equals(tool.Name, ToolName, StringComparison.Ordinal)).ToArray();
        var selected = requested.Count == 0
            ? parent
            : parent.Where(tool => requested.Contains(tool.Name)).ToArray();
        if (selected.Length == 0)
            return ToolResult.Fail("子任务没有可用工具。tools 只能收窄父级工具列表。");

        var ai = settings();
        var nestedSettings = new AiSettings
        {
            PermissionMode = ai.PermissionMode,
            MaxToolRounds = MaxNestedRounds,
            MaxContextCharacters = Math.Min(ai.MaxContextCharacters, 60_000),
            MaxContextTokens = ai.MaxContextTokens,
            MaxParallelReadOnlyTools = ai.MaxParallelReadOnlyTools,
            AcpEnabled = false,
            AutoCompactRatio = 0
        };

        Depth.Value = 1;
        try
        {
            var runner = new AgentTurnRunner(
                client,
                dispatcher,
                () => nestedSettings,
                () => new AgentMemoryDocument(),
                () => [],
                new AgentTurnRunnerOptions
                {
                    ToolSelector = () => selected,
                    MaxRequestRetries = 1
                },
                logger);
            var reason = await runner.RunTurnAsync(
                goal,
                string.IsNullOrWhiteSpace(model()) ? "gpt-4.1-mini" : model(),
                invocation.PageId,
                invocation.SessionId,
                ct).ConfigureAwait(false);
            return ToolResult.Ok(Summarize(runner.History, reason));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return ToolResult.Fail($"子任务失败：{exception.Message}");
        }
        finally
        {
            Depth.Value = 0;
        }
    }

    private static HashSet<string> ReadToolFilter(JsonElement arguments)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!arguments.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var item in toolsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var name = item.GetString();
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }

    private static string Summarize(IReadOnlyList<ChatMessage> history, AgentTurnEndReason reason)
    {
        var builder = new StringBuilder();
        builder.Append("子任务结束：").Append(reason).AppendLine();
        var last = history.LastOrDefault(message => message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content));
        if (last?.Content is { Length: > 0 } text)
            builder.Append(text.Length <= 2_000 ? text : text[..2_000] + "…");
        else
            builder.Append("子任务没有产生助手结论。");
        var toolError = history.LastOrDefault(message =>
            message.Role == "tool" && message.Content is { Length: > 0 } content &&
            (content.Contains("禁止", StringComparison.Ordinal) || content.Contains("失败", StringComparison.Ordinal)));
        if (toolError?.Content is { Length: > 0 } error)
            builder.AppendLine().Append(error.Length <= 400 ? error : error[..400] + "…");
        return builder.ToString();
    }
}
