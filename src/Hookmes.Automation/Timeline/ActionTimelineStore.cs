using Hookmes.Automation.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hookmes.Automation.Timeline;

/// <summary>Thread-safe, process-local action history shared by every action origin.</summary>
public sealed class ActionTimelineStore
{
    private readonly object _gate = new();
    private readonly List<ActionTimelineEntry> _entries = [];
    private long _nextSequence;

    public int Count { get { lock (_gate) return _entries.Count; } }

    public ActionTimelineEntry Append(string? pageId, ActionDescriptor action, ActionResult result, bool observed = false)
    {
        lock (_gate)
        {
            var entry = new ActionTimelineEntry
            {
                Sequence = ++_nextSequence,
                Timestamp = DateTimeOffset.UtcNow,
                PageId = pageId,
                Action = action,
                Result = result,
                Observed = observed
            };
            _entries.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<ActionTimelineEntry> Snapshot(int? last = null, ActionOrigin? origin = null, bool failuresOnly = false)
    {
        lock (_gate)
        {
            IEnumerable<ActionTimelineEntry> query = _entries;
            if (origin is not null) query = query.Where(x => x.Action.Origin == origin);
            if (failuresOnly) query = query.Where(x => !x.Result.Success);
            if (last is > 0) query = query.TakeLast(last.Value);
            return query.ToArray();
        }
    }

    public void Replace(IEnumerable<ActionTimelineEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(entries.OrderBy(x => x.Sequence));
            _nextSequence = _entries.Count == 0 ? 0 : _entries.Max(x => x.Sequence);
        }
    }

    public void Clear() { lock (_gate) { _entries.Clear(); _nextSequence = 0; } }
}
