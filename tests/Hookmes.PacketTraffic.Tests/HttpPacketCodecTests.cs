using Hookmes.Automation.Packet;
using System.Linq;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class HttpPacketCodecTests
{
    [Fact]
    public void Parse_and_format_preserve_duplicate_headers_and_body()
    {
        const string raw = "POST /submit HTTP/1.1\r\nHost: example.test\r\nX-Tag: first\r\nX-Tag: second\r\nContent-Length: 3\r\n\r\na=1";

        var packet = HttpPacketCodec.Parse(raw);

        Assert.Equal(HttpPacketKind.Request, packet.Kind);
        Assert.Equal(["first", "second"], packet.HeaderValues("x-tag").ToArray());
        Assert.Equal("a=1", packet.Body);
        Assert.Equal(raw, HttpPacketCodec.Format(packet));
    }

    [Fact]
    public void Format_normalizes_lf_input_to_crlf()
    {
        var packet = HttpPacketCodec.Parse("HTTP/1.1 200 OK\nContent-Type: text/plain\n\nhello");

        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nhello", HttpPacketCodec.Format(packet));
    }

    [Fact]
    public void Pretty_body_formats_json_without_changing_packet_body()
    {
        var packet = HttpPacketCodec.Parse("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"value\":1}");

        var pretty = HttpPacketCodec.PrettyBody(packet);

        Assert.Contains("\n", pretty);
        Assert.Contains("\"value\": 1", pretty);
        Assert.Equal("{\"value\":1}", packet.Body);
    }

    [Fact]
    public void Parse_rejects_obsolete_folded_headers()
    {
        var error = Assert.Throws<HttpPacketParseException>(() =>
            HttpPacketCodec.Parse("GET / HTTP/1.1\r\nHost: example.test\r\n injected\r\n\r\n"));

        Assert.Equal(3, error.Line);
    }
}
