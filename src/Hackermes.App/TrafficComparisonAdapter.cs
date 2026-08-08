using Hackermes.Automation.Traffic;
using Hackermes.Inspector.ViewModels;
using Hackermes.Traffic.Comparison;
using Hackermes.Traffic.Repeater;
using Hackermes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

public sealed class TrafficComparisonAdapter : ITrafficComparerWorkbenchService
{
    private readonly ITrafficComparisonService _comparisons;
    private readonly ITrafficStore _traffic;
    private readonly IRepeaterService _repeater;

    public TrafficComparisonAdapter(
        ITrafficComparisonService comparisons,
        ITrafficStore traffic,
        IRepeaterService repeater)
    {
        _comparisons = comparisons;
        _traffic = traffic;
        _repeater = repeater;
        _comparisons.Changed += _ => Changed?.Invoke();
    }

    public event Action? Changed;

    public IReadOnlyList<TrafficComparerSessionItem> Sessions =>
        _comparisons.GetAll().OrderByDescending(item => item.UpdatedAt).Select(ToItem).ToArray();

    public IReadOnlyList<TrafficComparerSourceItem> Sources
    {
        get
        {
            var sources = new List<TrafficComparerSourceItem>();
            foreach (var packet in _traffic.Read(5000))
            {
                sources.Add(new TrafficComparerSourceItem($"traffic-request:{packet.Id}",
                    $"Traffic request · {packet.Method} {packet.Url}", "Traffic request"));
                if (packet.ResponseStatus is not null)
                    sources.Add(new TrafficComparerSourceItem($"traffic-response:{packet.Id}",
                        $"Traffic response · {packet.ResponseStatus} · {packet.Method} {packet.Url}", "Traffic response"));
            }
            foreach (var draft in _repeater.GetAll())
            foreach (var send in draft.History.OrderByDescending(item => item.Sequence))
            {
                sources.Add(new TrafficComparerSourceItem($"repeater-request:{draft.Id}:{send.Id}",
                    $"Repeater request · {draft.Name} · send {send.Sequence}", "Repeater request"));
                if (send.Status == RepeaterSendStatus.Completed && send.ResponseStatus is not null)
                    sources.Add(new TrafficComparerSourceItem($"repeater-response:{draft.Id}:{send.Id}",
                        $"Repeater response · {draft.Name} · send {send.Sequence} · {send.ResponseStatus}", "Repeater response"));
            }
            return sources;
        }
    }

    public Task<string> CompareAsync(string leftPacketId, string rightPacketId, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = side.Equals("response", StringComparison.OrdinalIgnoreCase)
            ? ComparisonSourceKind.TrafficResponse : ComparisonSourceKind.TrafficRequest;
        var result = _comparisons.Compare(new ComparisonSource(kind, PacketId: leftPacketId),
            new ComparisonSource(kind, PacketId: rightPacketId));
        return Task.FromResult(Format(result));
    }

    public Task<string> CompareSourcesAsync(string leftReference, string rightReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Format(_comparisons.Compare(
            ComparisonSessionCommandRegistrar.ParseSource(leftReference),
            ComparisonSessionCommandRegistrar.ParseSource(rightReference))));
    }

    public Task<TrafficComparerSessionItem> CreateSessionAsync(
        string name, string leftReference, string rightReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToItem(_comparisons.Create(name,
            ComparisonSessionCommandRegistrar.ParseSource(leftReference),
            ComparisonSessionCommandRegistrar.ParseSource(rightReference))));
    }

    public Task<TrafficComparerSessionItem> RenameSessionAsync(string id, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToItem(_comparisons.Rename(id, name)));
    }

    public Task<TrafficComparerSessionItem> RecalculateSessionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToItem(_comparisons.Recalculate(id)));
    }

    public Task DeleteSessionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _comparisons.Delete(id);
        return Task.CompletedTask;
    }

    private static TrafficComparerSessionItem ToItem(TrafficComparisonSession session) => new(
        session.Id, session.Name, FormatSource(session.Left), FormatSource(session.Right),
        session.Result.Equal ? "Equal" : "Different", Format(session.Result), session.Revision, session.UpdatedAt);

    private static string FormatSource(ComparisonSource source) => source.Kind switch
    {
        ComparisonSourceKind.TrafficRequest => $"traffic-request:{source.PacketId}",
        ComparisonSourceKind.TrafficResponse => $"traffic-response:{source.PacketId}",
        ComparisonSourceKind.RepeaterRequest => $"repeater-request:{source.DraftId}:{source.SendResultId}",
        ComparisonSourceKind.RepeaterResponse => $"repeater-response:{source.DraftId}:{source.SendResultId}",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    internal static string Format(TrafficComparisonResult result)
    {
        var output = new StringBuilder(result.Equal ? "Packets are equal." : "Packets differ.");
        foreach (var item in result.StartLine.Where(x => x.Kind != DifferenceKind.Unchanged))
            output.AppendLine().Append($"START {item.Field}: {item.Left ?? "<none>"} -> {item.Right ?? "<none>"}");
        foreach (var item in result.Headers.Where(x => x.Kind != DifferenceKind.Unchanged))
            output.AppendLine().Append($"HEADER {item.Name}: [{string.Join(" | ", item.LeftValues)}] -> [{string.Join(" | ", item.RightValues)}]");
        if (!result.Body.Equal)
            output.AppendLine().Append($"BODY: {result.Body.Left.Length} B/{result.Body.Left.Sha256} -> " +
                $"{result.Body.Right.Length} B/{result.Body.Right.Sha256}; first byte {result.Body.FirstDifferentByteOffset}");
        return output.ToString();
    }
}
