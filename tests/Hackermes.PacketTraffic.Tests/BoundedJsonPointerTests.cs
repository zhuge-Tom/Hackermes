using Hackermes.Automation.Packet;
using System;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BoundedJsonPointerTests
{
    [Fact]
    public void Read_and_set_support_nested_objects_arrays_and_escaping()
    {
        const string json = "{\"a/b\":[{\"name\":\"old\"}],\"depth\":{\"x\":1}}";
        var values = BoundedJsonPointer.Read(json);
        Assert.Contains(values, value => value.Pointer == "/a~1b/0/name" && value.Value == "\"old\"");
        var changed = BoundedJsonPointer.Set(json, "/a~1b/0/name", "\"new\"");
        Assert.Contains("\"new\"", changed);
    }

    [Fact]
    public void Bounds_reject_excessive_depth_and_entries()
    {
        Assert.Throws<InvalidOperationException>(() => BoundedJsonPointer.Read("{\"a\":{\"b\":1}}", maximumDepth: 1));
        Assert.Throws<InvalidOperationException>(() => BoundedJsonPointer.Read("[1,2]", maximumEntries: 1));
    }
}
