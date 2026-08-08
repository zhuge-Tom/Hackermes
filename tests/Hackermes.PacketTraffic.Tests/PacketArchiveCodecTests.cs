using Hackermes.Automation.Packet;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PacketArchiveCodecTests
{
    private static readonly PacketArchiveEntry[] Entries = [new(
        "exchange-1",
        new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero),
        "POST https://example.test/api HTTP/1.1\r\nContent-Type: application/json\r\nX-Tag: one\r\nX-Tag: two\r\n\r\n{\"a\":1}",
        "HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\n\r\n{\"ok\":true}")];

    [Theory]
    [InlineData(PacketArchiveFormat.HackermesJson)]
    [InlineData(PacketArchiveFormat.Har)]
    public void Round_trip_preserves_packet_semantics(PacketArchiveFormat format)
    {
        var text = PacketArchiveCodec.Serialize(Entries, format);

        var restored = Assert.Single(PacketArchiveCodec.Deserialize(text, format));

        Assert.Equal(Entries[0].Id, restored.Id);
        Assert.Equal(Entries[0].CapturedAt, restored.CapturedAt);
        Assert.Empty(HttpPacketAnalyzer.Diff(
            HttpPacketCodec.Parse(Entries[0].Request), HttpPacketCodec.Parse(restored.Request)));
        Assert.Empty(HttpPacketAnalyzer.Diff(
            HttpPacketCodec.Parse(Entries[0].Response!), HttpPacketCodec.Parse(restored.Response!)));
    }

    [Theory]
    [InlineData("capture.HAR", PacketArchiveFormat.Har)]
    [InlineData("capture.json", PacketArchiveFormat.HackermesJson)]
    [InlineData("capture", PacketArchiveFormat.HackermesJson)]
    public void Detect_format_uses_har_extension_only(string path, PacketArchiveFormat expected) =>
        Assert.Equal(expected, PacketArchiveCodec.DetectFormat(path));

    [Fact]
    public void Hackermes_import_rejects_unknown_version_and_wrong_packet_kind()
    {
        Assert.Throws<InvalidDataException>(() => PacketArchiveCodec.Deserialize(
            "{\"version\":999,\"entries\":[]}", PacketArchiveFormat.HackermesJson));
        Assert.Throws<InvalidDataException>(() => PacketArchiveCodec.Deserialize(
            "{\"version\":1,\"entries\":[{\"id\":\"bad\",\"capturedAt\":\"2026-01-01T00:00:00Z\",\"request\":\"HTTP/1.1 200 OK\\r\\n\\r\\n\"}]}",
            PacketArchiveFormat.HackermesJson));
    }
}
