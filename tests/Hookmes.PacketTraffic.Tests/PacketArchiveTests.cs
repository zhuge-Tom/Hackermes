using Hookmes.Automation.Packet;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class PacketArchiveTests
{
    private static readonly PacketArchiveEntry Sample = new(
        "entry-1", DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
        "POST https://example.test/api HTTP/1.1\r\nX-Repeat: one\r\nX-Repeat: two\r\nContent-Type: application/json\r\n\r\n{\"ok\":true}",
        "HTTP/1.1 201 Created\r\nX-Repeat: a\r\nX-Repeat: b\r\nContent-Type: application/json\r\n\r\n{\"id\":1}");

    [Theory]
    [InlineData(PacketArchiveFormat.HookmesJson)]
    [InlineData(PacketArchiveFormat.Har)]
    public void RoundTrip_PreservesMessagesAndDuplicateHeaders(PacketArchiveFormat format)
    {
        var text = PacketArchiveCodec.Serialize([Sample], format);
        var result = Assert.Single(PacketArchiveCodec.Deserialize(text, format));

        var request = HttpPacketCodec.Parse(result.Request);
        var response = HttpPacketCodec.Parse(result.Response!);
        Assert.Equal(["one", "two"], request.HeaderValues("X-Repeat"));
        Assert.Equal(["a", "b"], response.HeaderValues("X-Repeat"));
        Assert.Equal("{\"ok\":true}", request.Body);
        Assert.Equal("{\"id\":1}", response.Body);
        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public void HookmesJson_RejectsUnknownSchemaVersion()
    {
        var text = PacketArchiveCodec.Serialize([Sample], PacketArchiveFormat.HookmesJson)
            .Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            PacketArchiveCodec.Deserialize(text, PacketArchiveFormat.HookmesJson));
    }

    [Theory]
    [InlineData(PacketArchiveFormat.HookmesJson)]
    [InlineData(PacketArchiveFormat.Har)]
    public void BinaryBody_RoundTripPreservesExactBytes(PacketArchiveFormat format)
    {
        byte[] bytes = [0, 255, 1, 128, 13, 10, 42];
        var entry = Sample with
        {
            ResponseBody = PacketBody.FromBytes(bytes, "application/octet-stream")
        };

        var serialized = PacketArchiveCodec.Serialize([entry], format);
        var result = Assert.Single(PacketArchiveCodec.Deserialize(serialized, format));

        Assert.Equal(PacketBodyEncoding.Base64, result.ResponseBody?.Encoding);
        Assert.Equal("application/octet-stream", result.ResponseBody?.ContentType);
        Assert.Equal(bytes, result.ResponseBody?.GetBytes());
    }

    [Fact]
    public void HarBinaryResponse_UsesStandardBase64Encoding()
    {
        var entry = Sample with { ResponseBody = PacketBody.FromBytes([0, 255], "image/png") };
        using var document = JsonDocument.Parse(PacketArchiveCodec.Serialize([entry], PacketArchiveFormat.Har));

        var content = document.RootElement.GetProperty("log").GetProperty("entries")[0]
            .GetProperty("response").GetProperty("content");
        Assert.Equal("base64", content.GetProperty("encoding").GetString());
        Assert.Equal(Convert.ToBase64String([0, 255]), content.GetProperty("text").GetString());
        Assert.Equal(2, content.GetProperty("size").GetInt64());
    }

    [Fact]
    public void HookmesJson_OldEntryWithoutBodyMetadataRemainsReadable()
    {
        const string legacy = """
        {
          "version": 1,
          "entries": [{
            "id": "old",
            "capturedAt": "2026-07-30T12:00:00Z",
            "request": "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n",
            "response": "HTTP/1.1 200 OK\r\n\r\nlegacy"
          }]
        }
        """;

        var result = Assert.Single(PacketArchiveCodec.Deserialize(legacy, PacketArchiveFormat.HookmesJson));
        Assert.Null(result.RequestBody);
        Assert.Null(result.ResponseBody);
        Assert.Equal("legacy", HttpPacketCodec.Parse(result.Response!).Body);
    }

    [Fact]
    public void BodyChunks_DescribeAndReadExactRanges()
    {
        byte[] body = [0, 1, 2, 3, 4, 5];
        var descriptor = PacketBodyChunker.Describe(body, "application/octet-stream");
        var chunk = PacketBodyChunker.Read(body, 2, 3);

        Assert.Equal(6, descriptor.Length);
        Assert.Equal("17e88db187afd62c16e5debf3e6527cd006bc012bc90b51a810cd80c2d511f43", descriptor.Sha256);
        Assert.Equal([2, 3, 4], PacketBodyChunker.Decode(chunk));
        Assert.False(chunk.IsEnd);
    }

    [Fact]
    public void BodyChunks_SafeTextFallsBackToBase64ForBinaryOrSplitUtf8()
    {
        var safe = PacketBodyChunker.Read("hello\nworld"u8, 0, 11, PacketBodyChunkEncoding.SafeText);
        var binary = PacketBodyChunker.Read(new byte[] { 0, 255 }, 0, 2, PacketBodyChunkEncoding.SafeText);
        var splitUtf8 = PacketBodyChunker.Read("你"u8, 0, 1, PacketBodyChunkEncoding.SafeText);

        Assert.Equal(PacketBodyChunkEncoding.SafeText, safe.Encoding);
        Assert.Equal("hello\nworld", safe.Data);
        Assert.Equal(PacketBodyChunkEncoding.Base64, binary.Encoding);
        Assert.Equal(PacketBodyChunkEncoding.Base64, splitUtf8.Encoding);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(7, 1)]
    [InlineData(0, 0)]
    [InlineData(0, PacketBodyChunker.MaximumChunkSize + 1)]
    public void BodyChunks_RejectInvalidOrOversizedRanges(long offset, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketBodyChunker.Read(new byte[6], offset, count));
    }

    [Fact]
    public void BodyChunks_AllowsEmptyChunkAtEndAndDetectsTampering()
    {
        var end = PacketBodyChunker.Read([1, 2], 2, 1);
        Assert.True(end.IsEnd);
        Assert.Empty(PacketBodyChunker.Decode(end));

        var tampered = end with { Count = 1 };
        Assert.Throws<InvalidDataException>(() => PacketBodyChunker.Decode(tampered));
    }
}
