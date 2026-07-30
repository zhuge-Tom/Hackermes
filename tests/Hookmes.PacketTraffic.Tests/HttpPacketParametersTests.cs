using Hookmes.Automation.Packet;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class HttpPacketParametersTests
{
    [Fact]
    public void Read_FindsDuplicateQueryFormAndTopLevelJsonParameters()
    {
        var query = HttpPacketCodec.Parse("GET /search?q=one&q=two&empty HTTP/1.1\r\n\r\n");
        var form = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/x-www-form-urlencoded; charset=utf-8\r\n\r\na=hello+world&a=2");
        var json = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/problem+json\r\n\r\n{\"ok\":true,\"nested\":{\"x\":1}}");

        Assert.Equal([0, 1], HttpPacketParameters.Read(query).Where(x => x.Name == "q").Select(x => x.Occurrence));
        Assert.Equal("hello world", HttpPacketParameters.Read(form).First().Value);
        Assert.Equal(["ok", "nested"], HttpPacketParameters.Read(json).Select(x => x.Name));
    }

    [Fact]
    public void Set_QueryPreservesUntouchedEncodingDuplicatesAndFragment()
    {
        var packet = HttpPacketCodec.Parse("GET /?q=a%2Fb&q=old&x=%2f#part HTTP/1.1\r\n\r\n");
        var changed = HttpPacketParameters.Set(packet, HttpParameterLocation.Query, "q", 1, "hello world");

        Assert.Equal("/?q=a%2Fb&q=hello%20world&x=%2f#part", changed.Target);
    }

    [Fact]
    public void Set_FormUsesPlusEncodingAndPreservesOtherPairs()
    {
        var packet = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/x-www-form-urlencoded\r\n\r\na=1&name=old&flag");
        var changed = HttpPacketParameters.Set(packet, HttpParameterLocation.Form, "name", 0, "Jane Doe");

        Assert.Equal("a=1&name=Jane+Doe&flag", changed.Body);
    }

    [Fact]
    public void Set_JsonPreservesExistingScalarType()
    {
        var packet = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/json\r\n\r\n{\"count\":1,\"enabled\":false,\"name\":\"old\"}");
        var count = HttpPacketParameters.Set(packet, HttpParameterLocation.Json, "count", 0, "42");
        var enabled = HttpPacketParameters.Set(count, HttpParameterLocation.Json, "enabled", 0, "true");
        var name = HttpPacketParameters.Set(enabled, HttpParameterLocation.Json, "name", 0, "42");

        Assert.Contains("\"count\":42", name.Body);
        Assert.Contains("\"enabled\":true", name.Body);
        Assert.Contains("\"name\":\"42\"", name.Body);
    }

    [Fact]
    public void Set_RejectsWrongContentTypeMissingOccurrenceAndNestedRoot()
    {
        var plain = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: text/plain\r\n\r\na=1");
        Assert.Throws<InvalidDataException>(() => HttpPacketParameters.Set(plain, HttpParameterLocation.Form, "a", 0, "2"));
        Assert.Throws<KeyNotFoundException>(() => HttpPacketParameters.Set(plain, HttpParameterLocation.Query, "a", 0, "2"));

        var array = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/json\r\n\r\n[1,2]");
        Assert.Throws<InvalidDataException>(() => HttpPacketParameters.Set(array, HttpParameterLocation.Json, "a", 0, "2"));
    }
}
