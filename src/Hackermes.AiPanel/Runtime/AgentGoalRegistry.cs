using Hackermes.AiPanel.Tools;
using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Same-session objective (dsh goal lineage): the model records what it is working toward
/// and the runner automatically continues with synthetic round messages until the objective
/// is cleared or the round cap is hit. Deliberately session-transient like todos — a durable
/// objective belongs in skills/memory, not in this registry.
/// </summary>
public sealed class AgentGoalRegistry
{
    /// <summary>Hard continuation cap per set-goal; safety valve mirroring maxRounds philosophy.</summary>
    public const int MaxRoundsPerGoal = 8;

    private readonly object _gate = new();
    private string? _goal;
    private int _roundsStarted;
    private bool _exhausted;

    public string? CurrentGoal { get { lock (_gate) return _goal; } }

    public int RoundsStarted { get { lock (_gate) return _roundsStarted; } }

    /// <summary>Sets (or restates) the active objective; resets its round counter.</summary>
    public void Set(string goal)
    {
        var trimmed = goal.Trim();
        if (trimmed.Length == 0) return;
        if (trimmed.Length > 2_000) trimmed = trimmed[..2_000];
        lock (_gate)
        {
            var same = SameGoal(_goal, trimmed);
            _goal = trimmed;
            if (!same)
            {
                _roundsStarted = 0;
                _exhausted = false;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _goal = null;
            _roundsStarted = 0;
            _exhausted = false;
        }
    }

    /// <summary>
    /// Called at turn-end continuation points: returns the next synthetic round message when
    /// an active goal still has rounds left, advancing the counter. False ends continuation.
    /// </summary>
    public bool TryBeginRound(out string roundMessage)
    {
        lock (_gate)
        {
            if (_goal is null || _exhausted || _roundsStarted >= MaxRoundsPerGoal)
            {
                if (_roundsStarted >= MaxRoundsPerGoal) _exhausted = true;
                roundMessage = string.Empty;
                return false;
            }
            _roundsStarted++;
            roundMessage =
                $"<goal_round>\n目标：{_goal}\n本轮：第 {_roundsStarted}/{MaxRoundsPerGoal} 轮\n" +
                "继续推进该目标；若已达成或确认无法推进，请调用 goal_clear 结束续跑。</goal_round>";
            return true;
        }
    }

    private static bool SameGoal(string? left, string right)
    {
        if (left is null) return false;
        return string.Equals(Collapse(left), Collapse(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Model-facing goal_set / goal_clear tools.</summary>
public sealed class AgentGoalToolAdapter(AgentGoalRegistry registry)
{
    public void RegisterAll(IAiToolRegistry toolRegistry)
    {
        toolRegistry.Register(new AiToolDefinition(
            "goal_set",
            "Record (or restate) the current session objective. While a goal is active the runtime " +
            "automatically continues working toward it across rounds until goal_clear or the round cap. " +
            "State one concrete, verifiable objective.",
            Schema(new { @goal = new { type = "string", description = "single concrete objective" } }),
            AiToolRisk.ReadOnly,
            (call, _) =>
            {
                var goal = Text(call.Arguments, "goal");
                if (goal.Length == 0) return ValueTask.FromResult(ToolResult.Fail("goal 不能为空。"));
                registry.Set(goal);
                return ValueTask.FromResult(ToolResult.Ok(
                    $"目标已记录，最多自动续跑 {AgentGoalRegistry.MaxRoundsPerGoal} 轮。达成后请调用 goal_clear。"));
            }));

        toolRegistry.Register(new AiToolDefinition(
            "goal_clear",
            "Mark the active objective as finished (or abandoned) and stop automatic continuation rounds.",
            Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) =>
            {
                var had = registry.CurrentGoal is not null;
                registry.Clear();
                return ValueTask.FromResult(ToolResult.Ok(had ? "目标已清除，自动续跑停止。" : "当前没有活动目标。"));
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
}
