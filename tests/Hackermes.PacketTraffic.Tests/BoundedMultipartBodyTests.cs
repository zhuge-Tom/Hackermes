using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BoundedMultipartBodyTests
{
    private const string ContentType = "multipart/form-data; boundary=X-BOUND";

    [Fact]
    public void Read_ListsTextAndBinaryPartsWithOccurrences()
    {
        var parts = BoundedMultipartBody.ReadParts(SampleBody(), ContentType);

        Assert.Equal(2, parts.Count);
        Assert.Equal("note", parts[0].Name);
        Assert.True(parts[0].IsText);
        Assert.Equal("old value", parts[0].DisplayValue);
        Assert.Null(parts[0].Filename);
        Assert.Equal("blob", parts[1].Name);
        Assert.False(parts[1].IsText);
        Assert.Equal("<binary 6 bytes>", parts[1].DisplayValue);
        Assert.Equal("a.bin", parts[1].Filename);
        Assert.Equal("application/octet-stream", parts[1].ContentType);

        var duplicated = BoundedMultipartBody.ReadParts(DuplicateBody(), ContentType);
        Assert.Equal([0, 1], duplicated.Select(part => part.Occurrence));
    }

    [Fact]
    public void Set_PreservesUntouchedBinaryBytesVerbatim()
    {
        var body = SampleBody();
        var updated = BoundedMultipartBody.SetPartValue(body, ContentType, "note", 0, Text("new value"));

        var text = Encoding.UTF8.GetString(updated);
        Assert.Contains("name=\"note\"\r\n\r\nnew value\r\n", text);
        Assert.EndsWith("--X-BOUND--\r\n", text);
        var blob = BoundedMultipartBody.ReadParts(updated, ContentType).Single(part => part.Name == "blob");
        Assert.Equal(new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F },
            updated.Skip((int)blob.ValueOffset).Take(blob.ValueLength).ToArray());
        Assert.Equal(body.Length - "old value".Length + "new value".Length, updated.Length);
    }

    [Fact]
    public void Set_SelectsOccurrenceAndRejectsMissingOrOutOfRange()
    {
        var body = DuplicateBody();

        var first = BoundedMultipartBody.SetPartValue(body, ContentType, "tag", 0, Text("one"));
        Assert.Contains("\r\none\r\n--X-BOUND\r\n", Encoding.UTF8.GetString(first));
        Assert.Contains("\r\nsecond\r\n--X-BOUND--", Encoding.UTF8.GetString(first));
        var second = BoundedMultipartBody.SetPartValue(body, ContentType, "tag", 1, Text("two"));
        Assert.Contains("\r\nfirst\r\n--X-BOUND\r\n", Encoding.UTF8.GetString(second));
        Assert.Contains("\r\ntwo\r\n--X-BOUND--", Encoding.UTF8.GetString(second));

        Assert.Throws<KeyNotFoundException>(() =>
            BoundedMultipartBody.SetPartValue(body, ContentType, "tag", 2, Text("nope")));
        Assert.Throws<KeyNotFoundException>(() =>
            BoundedMultipartBody.SetPartValue(body, ContentType, "absent", 0, Text("nope")));
    }

    [Fact]
    public void Set_RejectsInvalidNamesOccurrencesAndOversizedValues()
    {
        var body = SampleBody();
        Assert.Throws<ArgumentException>(() =>
            BoundedMultipartBody.SetPartValue(body, ContentType, " ", 0, Text("v")));
        Assert.Throws<ArgumentException>(() =>
            BoundedMultipartBody.SetPartValue(body, ContentType, new string('n', 257), 0, Text("v")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedMultipartBody.SetPartValue(body, ContentType, "note", -1, Text("v")));
        Assert.Throws<ArgumentException>(() =>
            BoundedMultipartBody.SetPartValue(body, ContentType, "note", 0,
                new byte[HttpPacketParameters.MaximumValueLength + 1]));
    }

    [Fact]
    public void Boundary_Validation_RejectsNonMultipartMissingAndBrokenTokens()
    {
        Assert.Throws<InvalidDataException>(() => BoundedMultipartBody.ExtractBoundary("application/json"));
        Assert.Throws<InvalidDataException>(() => BoundedMultipartBody.ExtractBoundary(null));
        Assert.Throws<InvalidDataException>(() => BoundedMultipartBody.ExtractBoundary("multipart/form-data"));
        Assert.Throws<InvalidDataException>(() => BoundedMultipartBody.ExtractBoundary("multipart/form-data; boundary="));
        Assert.Throws<InvalidDataException>(() => BoundedMultipartBody.ExtractBoundary("multipart/form-data; boundary=a\rb"));
        Assert.Throws<InvalidDataException>(() => BoundedMultipartBody.ExtractBoundary($"multipart/form-data; boundary={new string('b', 129)}"));
        Assert.Equal("quoted", BoundedMultipartBody.ExtractBoundary("multipart/form-data; boundary=\"quoted\""));
    }

    private static byte[] SampleBody()
    {
        using var buffer = new MemoryStream();
        Write(buffer, "--X-BOUND\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nold value\r\n");
        Write(buffer, "--X-BOUND\r\nContent-Disposition: form-data; name=\"blob\"; filename=\"a.bin\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        buffer.Write(new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F });
        Write(buffer, "\r\n--X-BOUND--\r\n");
        return buffer.ToArray();
    }

    private static byte[] DuplicateBody()
    {
        using var buffer = new MemoryStream();
        Write(buffer, "--X-BOUND\r\nContent-Disposition: form-data; name=\"tag\"\r\n\r\nfirst\r\n");
        Write(buffer, "--X-BOUND\r\nContent-Disposition: form-data; name=\"tag\"\r\n\r\nsecond\r\n--X-BOUND--\r\n");
        return buffer.ToArray();
    }

    private static void Write(MemoryStream buffer, string text) =>
        buffer.Write(Encoding.UTF8.GetBytes(text));

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
}
