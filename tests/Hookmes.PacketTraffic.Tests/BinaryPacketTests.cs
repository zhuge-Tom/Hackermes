using Hookmes.Automation.Packet;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class BinaryPacketTests
{
    private static readonly byte[] Binary = [0x00, 0xff, 0x80, 0x41, 0x0d, 0x0a, 0x7f];

    [Fact]
    public void Packet_body_base64_is_lossless_and_safe_for_display()
    {
        var body = PacketBody.FromBytes(Binary, "application/octet-stream");

        Assert.Equal(PacketBodyEncoding.Base64, body.Encoding);
        Assert.Equal(Binary, body.GetBytes());
        Assert.Contains("7 bytes", body.GetSafeDisplayText());
        Assert.Contains(Convert.ToBase64String(Binary), body.GetSafeDisplayText());
    }

    [Fact]
    public void Packet_body_honors_declared_text_charset_and_rejects_bad_encoding()
    {
        var latin = PacketBody.FromText("café", "text/plain", "iso-8859-1");

        Assert.Equal([0x63, 0x61, 0x66, 0xe9], latin.GetBytes());
        Assert.Throws<InvalidDataException>(() => new PacketBody("%%%", PacketBodyEncoding.Base64).GetBytes());
        Assert.Throws<InvalidDataException>(() => new PacketBody("x", PacketBodyEncoding.Text, Charset: "missing-charset").GetBytes());
    }

    [Fact]
    public void Chunker_supports_ranges_hash_and_binary_fallback()
    {
        var descriptor = PacketBodyChunker.Describe(Binary, "application/octet-stream");
        var first = PacketBodyChunker.Read(Binary, 0, 3, PacketBodyChunkEncoding.SafeText);
        var last = PacketBodyChunker.Read(Binary, 3, 4, PacketBodyChunkEncoding.Base64);

        Assert.Equal(64, descriptor.Sha256.Length);
        Assert.Equal(PacketBodyChunkEncoding.Base64, first.Encoding);
        Assert.Equal(Binary[..3], PacketBodyChunker.Decode(first));
        Assert.False(first.IsEnd);
        Assert.True(last.IsEnd);
        Assert.Equal(Binary[3..], PacketBodyChunker.Decode(last));
    }

    [Fact]
    public void Chunker_rejects_unsafe_ranges_and_inconsistent_chunks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketBodyChunker.Read(Binary, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketBodyChunker.Read(Binary, Binary.Length + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketBodyChunker.Read(Binary, 0, PacketBodyChunker.MaximumChunkSize + 1));
        Assert.Throws<InvalidDataException>(() => PacketBodyChunker.Decode(
            new PacketBodyChunk(0, 2, 2, Convert.ToBase64String([1]), PacketBodyChunkEncoding.Base64)));
    }

    [Theory]
    [InlineData(PacketArchiveFormat.HookmesJson)]
    [InlineData(PacketArchiveFormat.Har)]
    public void Archive_round_trip_preserves_binary_request_and_response(PacketArchiveFormat format)
    {
        var responseBytes = Binary.Reverse().ToArray();
        var entry = new PacketArchiveEntry("binary", DateTimeOffset.UnixEpoch,
            "POST https://example.test/upload HTTP/1.1\r\nContent-Type: application/octet-stream\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n\r\n",
            PacketBody.FromBytes(Binary, "application/octet-stream"),
            PacketBody.FromBytes(responseBytes, "application/octet-stream"));

        var restored = Assert.Single(PacketArchiveCodec.Deserialize(PacketArchiveCodec.Serialize([entry], format), format));

        Assert.Equal(Binary, restored.RequestBody!.GetBytes());
        Assert.Equal(responseBytes, restored.ResponseBody!.GetBytes());
        Assert.Equal(PacketBodyEncoding.Base64, restored.RequestBody.Encoding);
    }
}
