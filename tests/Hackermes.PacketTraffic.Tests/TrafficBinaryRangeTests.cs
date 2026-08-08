using Hackermes.Inspector.ViewModels;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficBinaryRangeTests
{
    [Theory]
    [InlineData(0, 65536, 0)]
    [InlineData(65536, 65536, 0)]
    [InlineData(70000, 65536, 4464)]
    public void Previous_ClampsAtStart(long offset, int count, long expected) =>
        Assert.Equal(expected, TrafficBinaryRange.Previous(offset, count));

    [Theory]
    [InlineData(200000, 0, 65536, 65536)]
    [InlineData(100000, 65536, 34464, 100000)]
    [InlineData(100000, 100000, 0, 100000)]
    [InlineData(100000, -1, 0, 0)]
    public void Next_UsesActualLoadedCountAndClampsAtEnd(long total, long offset, int loaded, long expected) =>
        Assert.Equal(expected, TrafficBinaryRange.Next(total, offset, loaded));

    [Theory]
    [InlineData(100000, 0, 65536, 65536)]
    [InlineData(100000, 90000, 65536, 10000)]
    [InlineData(0, 0, 65536, 0)]
    [InlineData(100, 101, 10, 0)]
    public void ActualCount_ReportsDisplayedByteRange(long total, long offset, int requested, int expected) =>
        Assert.Equal(expected, TrafficBinaryRange.ActualCount(total, offset, requested));
}
