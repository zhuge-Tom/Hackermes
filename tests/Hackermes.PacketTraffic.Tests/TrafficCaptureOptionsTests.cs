using Hackermes.Traffic.Models;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficCaptureOptionsTests
{
    [Fact]
    public void Normalize_ClampsInvalidResponseBodyLimit()
    {
        var belowMinimum = new TrafficCaptureOptions(MaxResponseBodyBytes: 1).Normalize();
        var aboveMaximum = new TrafficCaptureOptions(MaxResponseBodyBytes: int.MaxValue).Normalize();

        Assert.Equal(TrafficCaptureOptions.MinResponseBodyBytes, belowMinimum.MaxResponseBodyBytes);
        Assert.Equal(TrafficCaptureOptions.MaxAllowedResponseBodyBytes, aboveMaximum.MaxResponseBodyBytes);
    }

    [Fact]
    public void Normalize_PreservesCaptureModesAndValidLimit()
    {
        var options = new TrafficCaptureOptions(true, true, false, 512 * 1024).Normalize();

        Assert.True(options.PauseRequests);
        Assert.True(options.PauseResponses);
        Assert.False(options.CaptureResponseBodies);
        Assert.Equal(512 * 1024, options.MaxResponseBodyBytes);
    }
}
