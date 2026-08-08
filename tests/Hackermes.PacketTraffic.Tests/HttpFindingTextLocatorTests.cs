using Hackermes.Inspector.ViewModels;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class HttpFindingTextLocatorTests
{
    [Fact]
    public void Header_LocatesCaseInsensitiveOccurrenceWithoutEnteringBody()
    {
        const string raw = "POST / HTTP/1.1\r\nX-Test: first\r\nx-test: second\r\nContent-Type: text/plain\r\n\r\nX-Test: body";
        var first = HttpFindingTextLocator.Locate(raw, "Header", "x-test", 0)!;
        var second = HttpFindingTextLocator.Locate(raw, "Header", "X-TEST", 1)!;
        var body = HttpFindingTextLocator.Locate(raw, "Header", "X-Test", 2);

        Assert.Equal("X-Test: first", raw[first.Start..first.End]);
        Assert.Equal("x-test: second", raw[second.Start..second.End]);
        Assert.Null(body);
    }

    [Theory]
    [InlineData("GET /x HTTP/1.1\r\nHost: example\r\n\r\nbody", "GET /x HTTP/1.1")]
    [InlineData("HTTP/1.1 200 OK\nContent-Length: 0\n\n", "HTTP/1.1 200 OK")]
    [InlineData("GET / HTTP/1.1", "GET / HTTP/1.1")]
    public void StartLine_LocatesOnlyFirstLine(string raw, string expected)
    {
        var selection = HttpFindingTextLocator.Locate(raw, "StartLine")!;
        Assert.Equal(expected, raw[selection.Start..selection.End]);
    }

    [Fact]
    public void Header_DoesNotPrefixMatchOrAcceptInvalidOccurrence()
    {
        const string raw = "GET / HTTP/1.1\r\nX-Token-Extra: wrong\r\nX-Token: right\r\n\r\n";
        var selection = HttpFindingTextLocator.Locate(raw, "Header", "X-Token", 0)!;
        Assert.Equal("X-Token: right", raw[selection.Start..selection.End]);
        Assert.Null(HttpFindingTextLocator.Locate(raw, "Header", "X-Token", -1));
    }
}
