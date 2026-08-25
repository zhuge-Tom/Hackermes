using Hackermes.AiPanel.OpenAI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Projects model-visible history from a session event stream — the read-side of
/// "log is truth" (dsh deriveMessages lineage). Message events fold in order; an assistant
/// message with tool calls groups with its results so the OpenAI protocol stays valid.
///
/// <see cref="MessageProjector.Feed"/> returns zero or more messages to append per event,
/// letting hosts mirror them into their own stores; the static
/// <see cref="Project(IEnumerable{AgentSessionEvent})"/> aggregates into a plain list.
/// Compaction is NOT folded here: ACP compression is replayed by re-executing against the
/// store (see AgentTurnRunner.Replay), which preserves ref numbering exactly. Future
/// surface-replace work only swaps the folding rules in this class.
/// </summary>
public static class AgentHistoryProjector
{
    public static IReadOnlyList<ChatMessage> Project(IEnumerable<AgentSessionEvent> events)
    {
        var projector = new MessageProjector();
        var messages = new List<ChatMessage>();
        foreach (var @event in events)
            messages.AddRange(projector.Feed(@event.Data));
        return messages;
    }

    /// <summary>Incremental folding state over one stream of message events.</summary>
    internal sealed class MessageProjector
    {
        private string? _pendingPreamble;
        private readonly List<(string CallId, string Name, string Arguments)> _calls = [];
        private readonly Dictionary<string, ToolCallCompleted> _results = [];

        /// <summary>Consumes one event payload; returns the messages that become visible.</summary>
        public IReadOnlyList<ChatMessage> Feed(AgentEventData data)
        {
            switch (data)
            {
                case UserMessageReceived user:
                    Flush();
                    return [new ChatMessage("user", user.Text)];

                case AssistantReply reply:
                    Flush();
                    if (!reply.HasToolCalls)
                        return [new ChatMessage("assistant", reply.Content)];
                    _pendingPreamble = reply.Content;
                    return [];

                case ToolCallRequested requested:
                    _calls.Add((requested.CallId, requested.Name, requested.ArgumentsJson));
                    return [];

                case ToolCallCompleted completed:
                {
                    _results[completed.CallId] = completed;
                    if (_results.Count < _calls.Count) return [];
                    var emitted = new List<ChatMessage>(_calls.Count * 2);
                    var resolved = _calls
                        .Where(call => _results.ContainsKey(call.CallId))
                        .Select(call => new AssistantToolCall(call.CallId, call.Name, call.Arguments))
                        .ToArray();
                    if (resolved.Length > 0)
                    {
                        emitted.Add(new ChatMessage("assistant",
                            _pendingPreamble!.Length == 0 ? null : _pendingPreamble, ToolCalls: resolved));
                        foreach (var call in _calls)
                        {
                            if (!_results.TryGetValue(call.CallId, out var done)) continue;
                            emitted.Add(new ChatMessage("tool", done.Content, ToolCallId: call.CallId));
                        }
                    }
                    Reset();
                    return emitted;
                }

                default:
                    return [];
            }
        }

        /// <summary>Drops any incomplete pending block (crash-truncated protocol tail).</summary>
        public void Flush() => Reset();

        private void Reset()
        {
            _pendingPreamble = null;
            _calls.Clear();
            _results.Clear();
        }
    }
}
