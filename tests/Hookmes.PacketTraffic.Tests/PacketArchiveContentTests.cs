using Hookmes.Automation.Packet;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class PacketArchiveContentTests
{
    [Theory]
    [InlineData("hookmesJson", PacketArchiveFormat.HookmesJson)]
    [InlineData("json", PacketArchiveFormat.HookmesJson)]
    [InlineData("HAR", PacketArchiveFormat.Har)]
    public void Format_is_explicit_and_never_inferred_from_a_path(string value, PacketArchiveFormat expected) =>
        Assert.Equal(expected, PacketArchiveContent.ParseFormat(value));

    [Fact]
    public void Content_round_trip_uses_existing_archive_codec()
    {
        var entry = new PacketArchiveEntry("one", DateTimeOffset.UnixEpoch,
            "GET https://example.test/ HTTP/1.1\r\n\r\n");

        var content = PacketArchiveContent.Serialize([entry], PacketArchiveFormat.HookmesJson);
        var restored = Assert.Single(PacketArchiveContent.Deserialize(content, PacketArchiveFormat.HookmesJson));

        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(entry.Request, restored.Request);
    }

    [Fact]
    public void Oversized_content_and_entry_counts_are_rejected_before_import()
    {
        var oversized = new string('x', PacketArchiveContent.MaximumUtf8Bytes + 1);
        Assert.Throws<InvalidDataException>(() => PacketArchiveContent.Deserialize(oversized, PacketArchiveFormat.HookmesJson));

        var entries = Enumerable.Range(0, PacketArchiveContent.MaximumEntries + 1)
            .Select(index => new PacketArchiveEntry(index.ToString(), DateTimeOffset.UnixEpoch,
                "GET https://example.test/ HTTP/1.1\r\n\r\n")).ToArray();
        Assert.Throws<InvalidDataException>(() => PacketArchiveContent.Serialize(entries, PacketArchiveFormat.HookmesJson));
    }

    [Fact]
    public void Arbitrary_path_text_is_not_a_supported_format() =>
        Assert.Throws<ArgumentException>(() => PacketArchiveContent.ParseFormat("C:\\secrets\\capture.har"));
}
