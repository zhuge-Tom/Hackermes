using Hackermes.Traffic.Models;
using Hackermes.Traffic.History;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hackermes.Traffic.Services;

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
    private const int AbsoluteMaxEntries = 100_000;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrafficMessage> _byId = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = [];
    private readonly ITrafficHistoryPersistence? _persistence;
    private readonly ITrafficHistoryPolicyStore? _policies;
    private long _estimatedBytes;
    private DateTimeOffset _lastAutoPrune = DateTimeOffset.MinValue;
    public event Action<TrafficMessage>? Changed;

    public TrafficStore() { }

    public TrafficStore(ITrafficHistoryPersistence persistence)
        : this(persistence, null) { }

    public TrafficStore(ITrafficHistoryPersistence persistence, ITrafficHistoryPolicyStore? policies)
    {
        _persistence = persistence;
        _policies = policies;
        foreach (var message in persistence.Load().TakeLast(AbsoluteMaxEntries))
        {
            if (_byId.TryGetValue(message.Id, out var previous))
                _estimatedBytes -= TrafficHistorySizing.Estimate(previous);
            else
                _order.AddLast(message.Id);
            _byId[message.Id] = message;
            _estimatedBytes += TrafficHistorySizing.Estimate(message);
        }
        if (policies?.Current.AutoPrune == true && ApplyRetentionPolicyCore(policies.Current).RemovedEntries > 0)
            persistence.ScheduleSave(_order.Select(id => _byId[id]).ToArray());
    }

    public TrafficMessage? Get(string id) { lock (_gate) return _byId.GetValueOrDefault(id); }
    public IReadOnlyList<TrafficMessage> Read(int last = 100, string? pageId = null)
    {
        lock (_gate) return _order.Reverse().Select(id => _byId[id])
            .Where(x => pageId is null || x.PageId == pageId).Take(Math.Clamp(last, 1, AbsoluteMaxEntries)).ToArray();
    }

    public void Clear(string? pageId = null)
    {
        lock (_gate)
        {
            if (pageId is null) { _byId.Clear(); _order.Clear(); _estimatedBytes = 0; }
            else foreach (var id in _order.Where(id => _byId[id].PageId == pageId).ToArray()) { _byId.Remove(id); _order.Remove(id); }
            if (pageId is not null) _estimatedBytes = _order.Sum(id => TrafficHistorySizing.Estimate(_byId[id]));
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
            // Keep paging bounded: traffic history can hold 100,000 records, and
            // materializing every matching item merely to show one page creates a
            // large transient allocation on every filter keystroke.
            var total = items.Count();
            var page = items.Skip(offset).Take(limit).ToArray();
            return new TrafficQueryResult(page, total, offset, limit);
        }
    }

    internal void Put(TrafficMessage message)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(message.Id, out var previous))
                _estimatedBytes -= TrafficHistorySizing.Estimate(previous);
            else
                _order.AddLast(message.Id);
            _byId[message.Id] = message;
            _estimatedBytes += TrafficHistorySizing.Estimate(message);
            if (_policies?.Current is { AutoPrune: true } policy &&
                (_order.Count > policy.MaxEntries || _estimatedBytes > policy.MaxStorageBytes ||
                 DateTimeOffset.UtcNow - _lastAutoPrune >= TimeSpan.FromMinutes(1)))
            {
                ApplyRetentionPolicyCore(policy);
                _lastAutoPrune = DateTimeOffset.UtcNow;
            }
            while (_order.Count > AbsoluteMaxEntries) RemoveOldest();
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

    internal IReadOnlyList<TrafficMessage> ReadAllChronological()
    {
        lock (_gate) return _order.Select(id => _byId[id]).ToArray();
    }

    internal TrafficCleanupPreview ApplyRetentionPolicy(TrafficHistoryPolicy policy, bool force)
    {
        ArgumentNullException.ThrowIfNull(policy);
        TrafficCleanupPreview result;
        lock (_gate)
        {
            if (!force && !policy.AutoPrune)
                return new TrafficCleanupPreview(0, 0, _order.Count,
                    _order.Sum(id => TrafficHistorySizing.Estimate(_byId[id])));
            result = ApplyRetentionPolicyCore(TrafficHistoryPolicyStore.Normalize(policy));
        }
        if (result.RemovedEntries > 0) ScheduleSave();
        return result;
    }

    private TrafficCleanupPreview ApplyRetentionPolicyCore(TrafficHistoryPolicy policy)
    {
        var items = _order.Select(id => _byId[id]).ToArray();
        var plan = TrafficHistoryRetention.Plan(items, policy, DateTimeOffset.UtcNow);
        foreach (var id in plan.Ids)
        {
            _byId.Remove(id);
            _order.Remove(id);
        }
        _estimatedBytes -= plan.RemovedBytes;
        _lastAutoPrune = DateTimeOffset.UtcNow;
        return new TrafficCleanupPreview(plan.Ids.Count, plan.RemovedBytes, _order.Count, _estimatedBytes);
    }

    private void RemoveOldest()
    {
        var id = _order.First!.Value;
        _order.RemoveFirst();
        if (_byId.Remove(id, out var removed)) _estimatedBytes -= TrafficHistorySizing.Estimate(removed);
    }
}
