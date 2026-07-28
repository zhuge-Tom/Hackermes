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
}

public sealed class TrafficStore : ITrafficStore
{
    private const int MaxEntries = 5000;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrafficMessage> _byId = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = [];
    public event Action<TrafficMessage>? Changed;

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
            if (pageId is null) { _byId.Clear(); _order.Clear(); return; }
            foreach (var id in _order.Where(id => _byId[id].PageId == pageId).ToArray()) { _byId.Remove(id); _order.Remove(id); }
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
    }
}
