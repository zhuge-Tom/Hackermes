using Hackermes.Automation.Packet;
using Hackermes.Base.Cryptography;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>End-to-end parameter surface: listing and structured edits through HttpPacketParameters.</summary>
public sealed class MultipartParameterSurfaceTests
{
    private const string Raw =
        "POST /upload HTTP/1.1\r\n" +
        "Host: authorized.example\r\n" +
        "Content-Type: multipart/form-data; boundary=X-BOUND\r\n" +
        "\r\n" +
        "--X-BOUND\r\n" +
        "Content-Disposition: form-data; name=\"title\"\r\n" +
        "\r\n" +
        "draft\r\n" +
        "--X-BOUND\r\n" +
        "Content-Disposition: form-data; name=\"payload\"\r\n" +
        "\r\n" +
        "abc\r\n" +
        "--X-BOUND--\r\n";

    [Fact]
    public void Read_surfaces_multipart_parts_alongside_headers()
    {
        var parameters = HttpPacketParameters.Read(HttpPacketCodec.Parse(Raw));

        var parts = parameters.Where(p => p.Location == HttpParameterLocation.Multipart).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal(("title", "draft", 0), (parts[0].Name, parts[0].Value, parts[0].Occurrence));
        Assert.Equal(("payload", "abc", 0), (parts[1].Name, parts[1].Value, parts[1].Occurrence));
        Assert.Contains(parameters, p => p.Location == HttpParameterLocation.Header && p.Name == "Host");
    }

    [Fact]
    public void Set_round_trips_and_keeps_sibling_parts_intact()
    {
        var packet = HttpPacketCodec.Parse(Raw);

        var updated = HttpPacketParameters.Set(packet, HttpParameterLocation.Multipart, "payload", 0, "xyz");

        var listed = HttpPacketParameters.Read(updated)
            .Where(p => p.Location == HttpParameterLocation.Multipart).ToArray();
        Assert.Equal("xyz", listed.Single(p => p.Name == "payload").Value);
        Assert.Equal("draft", listed.Single(p => p.Name == "title").Value);
    }

    [Fact]
    public void Set_requires_multipart_content_type()
    {
        var json = """
            POST /api HTTP/1.1
            Content-Type: application/json

            {"a":1}
            """;
        Assert.ThrowsAny<Exception>(() => HttpPacketParameters.Set(
            HttpPacketCodec.Parse(json), HttpParameterLocation.Multipart, "a", 0, "b"));
    }
}

/// <summary>Identity-keyed memoization must never return a stale digest.</summary>
public sealed class BodySha256Tests
{
    [Fact]
    public void Same_array_is_memoized_new_array_recomputes()
    {
        var body = "payload-v1"u8.ToArray();
        var first = BodySha256.Of(body);

        Assert.Equal(first, BodySha256.Of(body));
        Assert.Equal(Sha256Hex(body), first);

        var edited = "payload-v2"u8.ToArray();
        Assert.NotEqual(first, BodySha256.Of(edited));
        Assert.Equal(Sha256Hex(edited), BodySha256.Of(edited));
    }

    [Fact]
    public void Empty_and_large_bodies_match_direct_hashing()
    {
        Assert.Equal(Sha256Hex([]), BodySha256.Of([]));

        var large = new byte[300_000];
        Random.Shared.NextBytes(large);
        Assert.Equal(Sha256Hex(large), BodySha256.Of(large));
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
