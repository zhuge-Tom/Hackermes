using Hookmes.Traffic.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hookmes.Traffic.Services;

public interface ITrafficStore
{
    event Action<TrafficMessage>? Changed;
    TrafficMessage? Get(string id);
    IReadOnlyList<TrafficMessage> Read(int last = 100, string? pageId = null);
    void Clear(string? pageId = null);
    void MarkPausedContinued(string pageId);
    void Import(TrafficMessage message);
    TrafficQueryResult Query(TrafficQuery query);
}

public sealed class TrafficStore : ITrafficStore
{
    private const int MaxEntries = 5000;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrafficMessage> _byId = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = [];
    private readonly ITrafficHistoryPersistence? _persistence;
    public event Action<TrafficMessage>? Changed;

    public TrafficStore() { }

    public TrafficStore(ITrafficHistoryPersistence persistence)
    {
        _persistence = persistence;
        foreach (var message in persistence.Load().TakeLast(MaxEntries))
        {
            if (!_byId.ContainsKey(message.Id)) _order.AddLast(message.Id);
            _byId[message.Id] = message;
        }
    }

    public TrafficMessage? Get(string id) { lock (_gate) return _byId.GetValueOrDefault(id); }
    public IReadOnlyList<TrafficMessage> Read(int last = 100, string? pageId = null)
    {
        lock (_gate) return _order.Reverse().Select(id => _byId[id])
            .Where(x => pageId is null || x.PageId == pageId).Take(Math.Clamp(last, 1, MaxEntries)).ToArray();
    }

    public void Clear(string? pageId = null)
    {
        lock (_gate)
        {
            if (pageId is null) { _byId.Clear(); _order.Clear(); }
            else foreach (var id in _order.Where(id => _byId[id].PageId == pageId).ToArray()) { _byId.Remove(id); _order.Remove(id); }
        }
        ScheduleSave();
    }

    public void MarkPausedContinued(string pageId)
    {
        TrafficMessage[] changed;
        lock (_gate)
        {
            changed = _byId.Values.Where(x => x.PageId == pageId && x.State == TrafficState.Paused).ToArray();
            foreach (var item in changed) _byId[item.Id] = item with { State = TrafficState.Continued };
        }
        foreach (var item in changed) Changed?.Invoke(item with { State = TrafficState.Continued });
        if (changed.Length > 0) ScheduleSave();
    }

    public void Import(TrafficMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Put(message);
    }

    public TrafficQueryResult Query(TrafficQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 1000);
        lock (_gate)
        {
            IEnumerable<TrafficMessage> items = _order.Reverse().Select(id => _byId[id]);
            if (!string.IsNullOrWhiteSpace(query.PageId)) items = items.Where(x => x.PageId == query.PageId);
            if (!string.IsNullOrWhiteSpace(query.Text)) items = items.Where(x =>
                x.Url.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ||
                x.Method.Contains(query.Text, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.Method)) items = items.Where(x => x.Method.Equals(query.Method, StringComparison.OrdinalIgnoreCase));
            if (query.Status is { } status) items = items.Where(x => x.ResponseStatus == status);
            if (!string.IsNullOrWhiteSpace(query.ResourceType)) items = items.Where(x => x.ResourceType.Equals(query.ResourceType, StringComparison.OrdinalIgnoreCase));
            if (query.State is { } state) items = items.Where(x => x.State == state);
            if (!string.IsNullOrWhiteSpace(query.RuleId)) items = items.Where(x => x.AppliedRuleId?.Equals(query.RuleId, StringComparison.Ordinal) == true);
            if (query.From is { } from) items = items.Where(x => x.CapturedAt >= from);
            if (query.To is { } to) items = items.Where(x => x.CapturedAt <= to);
            var materialized = items.ToArray();
            return new TrafficQueryResult(materialized.Skip(offset).Take(limit).ToArray(), materialized.Length, offset, limit);
        }
    }

    internal void Put(TrafficMessage message)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(message.Id)) _order.AddLast(message.Id);
            _byId[message.Id] = message;
            while (_order.Count > MaxEntries) { var id = _order.First!.Value; _order.RemoveFirst(); _byId.Remove(id); }
        }
        Changed?.Invoke(message);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_persistence is null) return;
        TrafficMessage[] snapshot;
        lock (_gate) snapshot = _order.Select(id => _byId[id]).ToArray();
        _persistence.ScheduleSave(snapshot);
    }
}
