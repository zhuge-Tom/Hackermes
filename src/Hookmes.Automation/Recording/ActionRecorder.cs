using Hookmes.Automation.Execution;
using Hookmes.Automation.Model;
using Hookmes.Automation.Timeline;
using Hookmes.Base.Events;
using Hookmes.Platform.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Recording;

/// <summary>把 Page Agent 捕获的人工操作转换为可回放的统一动作。</summary>
public sealed class ActionRecorder
{
    private readonly ActionExecutor _executor;
    private readonly ActionTimelineStore _timeline;
    private readonly List<ActionDescriptor> _actions = [];
    private readonly object _gate = new();
    private string? _pageId;

    public ActionRecorder(IEventBus eventBus, ActionExecutor executor, ActionTimelineStore timeline)
    {
        _executor = executor;
        _timeline = timeline;
        eventBus.Subscribe<PageAgentMessageEvent>(OnAgentMessage);
    }

    public bool IsRecording { get { lock (_gate) return _pageId is not null; } }
    public int Count { get { lock (_gate) return _actions.Count; } }

    public void Start(string pageId)
    {
        lock (_gate) { _actions.Clear(); _pageId = pageId; }
    }

    public IReadOnlyList<ActionDescriptor> Stop()
    {
        lock (_gate) { _pageId = null; return _actions.ToArray(); }
    }

    public void Clear() { lock (_gate) _actions.Clear(); }

    public IReadOnlyList<ActionDescriptor> Snapshot()
    {
        lock (_gate) return _actions.ToArray();
    }

    public void Replace(IEnumerable<ActionDescriptor> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        lock (_gate)
        {
            _pageId = null;
            _actions.Clear();
            _actions.AddRange(actions);
        }
    }

    public async Task<(int Completed, ActionResult? Failure)> ReplayAsync(string pageId, CancellationToken ct)
    {
        ActionDescriptor[] snapshot;
        lock (_gate) snapshot = _actions.Select(a => a with { Origin = ActionOrigin.Script }).ToArray();

        for (var i = 0; i < snapshot.Length; i++)
        {
            var result = await _executor.ExecuteAsync(pageId, snapshot[i], ct).ConfigureAwait(false);
            if (!result.Success) return (i, result);
        }
        return (snapshot.Length, null);
    }

    private void OnAgentMessage(PageAgentMessageEvent message)
    {
        if (message.Kind != "action") return;

        ActionDescriptor? action;
        try
        {
            using var json = JsonDocument.Parse(message.PayloadJson);
            action = ParseHumanAction(message.SubKind, json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return;
        }

        if (action is null) return;
        _timeline.Append(message.PageId, action, ActionResult.Ok(), observed: true);

        lock (_gate)
        {
            if (_pageId is not null && string.Equals(_pageId, message.PageId, StringComparison.Ordinal))
            {
                // input 对人工逐字输入会连续上报；同一输入框只保留最终值，脚本仍是一条稳定动作。
                if (action.Kind == ActionKind.Type && _actions.Count > 0
                    && _actions[^1].Kind == ActionKind.Type
                    && string.Equals(_actions[^1].Target?.Primary, action.Target?.Primary, StringComparison.Ordinal))
                    _actions[^1] = action;
                else
                    _actions.Add(action);
            }
        }
    }

    private static ActionDescriptor? ParseHumanAction(string? kind, JsonElement root)
    {
        if (kind == "press")
        {
            return new ActionDescriptor
            {
                Kind = ActionKind.Press, Origin = ActionOrigin.Human,
                Args = new Dictionary<string, string?> { ["key"] = root.GetProperty("key").GetString() }
            };
        }

        if (!root.TryGetProperty("candidates", out var items)) return null;
        var candidates = new List<SelectorCandidate>();
        foreach (var item in items.EnumerateArray())
        {
            if (!Enum.TryParse<SelectorStrategy>(item.GetProperty("strategy").GetString(), out var strategy)) continue;
            candidates.Add(new(item.GetProperty("value").GetString()!, strategy, item.GetProperty("score").GetInt32()));
        }
        if (candidates.Count == 0) return null;
        var target = TargetSelector.FromCandidates(candidates);
        return kind switch
        {
            "click" => ActionDescriptor.Click(target, ActionOrigin.Human),
            "type" => ActionDescriptor.Type(target, root.GetProperty("value").GetString() ?? string.Empty, true, ActionOrigin.Human),
            "select" => new ActionDescriptor { Kind = ActionKind.Select, Target = target, Origin = ActionOrigin.Human,
                Args = new Dictionary<string, string?> { ["value"] = root.GetProperty("value").GetString() } },
            _ => null
        };
    }
}
