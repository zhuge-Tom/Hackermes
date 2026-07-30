using Hookmes.Inspector.ViewModels;
using Hookmes.Traffic.Comparison;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.App;

public sealed class TrafficComparisonAdapter(ITrafficComparisonService comparisons) : ITrafficComparerWorkbenchService
{
    public Task<string> CompareAsync(string leftPacketId, string rightPacketId, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = side.Equals("response", StringComparison.OrdinalIgnoreCase)
            ? ComparisonSourceKind.TrafficResponse : ComparisonSourceKind.TrafficRequest;
        var result = comparisons.Compare(new ComparisonSource(kind, PacketId: leftPacketId),
            new ComparisonSource(kind, PacketId: rightPacketId));
        return Task.FromResult(Format(result));
    }

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
