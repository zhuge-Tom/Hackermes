using Hackermes.Automation.Packet;
using System;
using System.IO;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BinaryBodyEditorTests
{
    [Theory]
    [InlineData(BinaryTextEncoding.Hex, "00 ff 10", "00ff10")]
    [InlineData(BinaryTextEncoding.Base64, "AP8Q", "00ff10")]
    public void Codec_ParsesAndFormatsWithoutLoss(BinaryTextEncoding encoding, string input, string expectedHex)
    {
        var bytes = BinaryBodyCodec.Parse(input, encoding);
        Assert.Equal(expectedHex, BinaryBodyCodec.Format(bytes, BinaryTextEncoding.Hex));
        Assert.Equal(bytes, BinaryBodyCodec.Parse(BinaryBodyCodec.Format(bytes, encoding), encoding));
    }

    [Theory]
    [InlineData(BinaryTextEncoding.Hex, "abc")]
    [InlineData(BinaryTextEncoding.Hex, "zz")]
    [InlineData(BinaryTextEncoding.Base64, "%%%")]
    public void Codec_RejectsMalformedInput(BinaryTextEncoding encoding, string input) =>
        Assert.Throws<InvalidDataException>(() => BinaryBodyCodec.Parse(input, encoding));

    [Fact]
    public void Editor_ReplaceInsertDeleteHaveByteExactSemantics()
    {
        byte[] original = [0, 1, 2, 3];
        Assert.Equal([0, 9, 8, 3], BinaryBodyEditor.Replace(original, 1, 2, [9, 8]));
        Assert.Equal([0, 1, 7, 2, 3], BinaryBodyEditor.Insert(original, 2, [7]));
        Assert.Equal([0, 3], BinaryBodyEditor.Delete(original, 1, 2));
        Assert.Equal([0, 1, 2, 3, 4], BinaryBodyEditor.Insert(original, 4, [4]));
    }

    [Fact]
    public void Editor_AppliesEncodedOperationAndReturnsBase64PacketBody()
    {
        var body = PacketBody.FromBytes([1, 2, 3], "application/octet-stream");
        var edited = BinaryBodyEditor.Apply(body,
            new BinaryBodyEdit(BinaryEditKind.Replace, 1, 1, "ff", BinaryTextEncoding.Hex));

        Assert.Equal([1, 255, 3], edited.GetBytes());
        Assert.Equal(PacketBodyEncoding.Base64, edited.Encoding);
        Assert.Equal("application/octet-stream", edited.ContentType);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0)]
    [InlineData(3, 2)]
    [InlineData(0, -1)]
    public void Editor_RejectsInvalidRanges(long offset, long count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBodyEditor.Replace(new byte[4], offset, count, []));

    [Fact]
    public void Editor_EnforcesEditAndResultLimits()
    {
        var oversizedEdit = new byte[BinaryBodyEditor.MaximumEditDataSize + 1];
        Assert.Throws<InvalidDataException>(() => BinaryBodyEditor.Insert([], 0, oversizedEdit));

        var maximumBody = new byte[BinaryBodyEditor.MaximumBodySize];
        Assert.Throws<InvalidDataException>(() => BinaryBodyEditor.Insert(maximumBody, maximumBody.Length, [1]));
    }

    [Fact]
    public void Editor_RejectsContradictoryOperationFields()
    {
        Assert.Throws<InvalidDataException>(() => BinaryBodyEditor.Apply([1, 2],
            new BinaryBodyEdit(BinaryEditKind.Insert, 1, 1, "ff")));
        Assert.Throws<InvalidDataException>(() => BinaryBodyEditor.Apply([1, 2],
            new BinaryBodyEdit(BinaryEditKind.Delete, 0, 1, "ff")));
        Assert.Throws<InvalidDataException>(() => BinaryBodyEditor.Apply([1, 2],
            new BinaryBodyEdit(BinaryEditKind.Replace, 0, 1)));
    }

    [Fact]
    public void ContentLength_ReplacesDuplicatesOrAppendsCanonicalHeader()
    {
        var packet = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Length: 1\r\nX-Test: yes\r\ncontent-length: 2\r\n\r\nx");
        var updated = BinaryBodyEditor.UpdateContentLength(packet, 42);
        Assert.Equal(["42"], updated.HeaderValues("Content-Length"));
        Assert.Equal("yes", Assert.Single(updated.HeaderValues("X-Test")));

        var withoutHeader = packet with { Headers = [new HttpHeader("X-Test", "yes")] };
        Assert.Equal(["0"], BinaryBodyEditor.UpdateContentLength(withoutHeader, 0).HeaderValues("Content-Length"));
    }
}
