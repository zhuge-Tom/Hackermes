using Hackermes.Automation.Packet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class HttpPacketParametersTests
{
    [Fact]
    public void Read_FindsDuplicateQueryFormAndTopLevelJsonParameters()
    {
        var query = HttpPacketCodec.Parse("GET /search?q=one&q=two&empty HTTP/1.1\r\n\r\n");
        var form = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/x-www-form-urlencoded; charset=utf-8\r\n\r\na=hello+world&a=2");
        var json = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: application/problem+json\r\n\r\n{\"ok\":true,\"nested\":{\"x\":1}}");

        Assert.Equal([0, 1], HttpPacketParameters.Read(query).Where(x => x.Name == "q").Select(x => x.Occurrence));
        Assert.Equal("hello world", HttpPacketParameters.Read(form).First(x => x.Location == HttpParameterLocation.Form).Value);
        Assert.Equal(["ok", "nested"], HttpPacketParameters.Read(json)
            .Where(x => x.Location == HttpParameterLocation.Json).Select(x => x.Name));
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

    [Fact]
    public void Read_and_set_preserve_duplicate_headers_and_reject_line_injection()
    {
        var packet = HttpPacketCodec.Parse("GET / HTTP/1.1\r\nX-Mode: one\r\nX-Mode: two\r\nAuthorization: Bearer old\r\n\r\n");
        var headers = HttpPacketParameters.Read(packet).Where(item => item.Location == HttpParameterLocation.Header).ToArray();

        Assert.Equal([0, 1], headers.Where(item => item.Name == "X-Mode").Select(item => item.Occurrence));
        var changed = HttpPacketParameters.Set(packet, HttpParameterLocation.Header, "x-mode", 1, "changed");
        Assert.Equal(["one", "changed"], changed.HeaderValues("X-Mode"));
        Assert.Throws<ArgumentException>(() => HttpPacketParameters.Set(packet,
            HttpParameterLocation.Header, "X-Mode", 0, "safe\r\nInjected: yes"));
    }

    [Fact]
    public void Read_and_set_cookie_preserve_other_pairs_and_set_cookie_attributes()
    {
        var request = HttpPacketCodec.Parse("GET / HTTP/1.1\r\nCookie: theme=dark; session=old; theme=light\r\n\r\n");
        var cookies = HttpPacketParameters.Read(request).Where(item => item.Location == HttpParameterLocation.Cookie).ToArray();
        Assert.Equal([0, 1], cookies.Where(item => item.Name == "theme").Select(item => item.Occurrence));
        var changedRequest = HttpPacketParameters.Set(request, HttpParameterLocation.Cookie, "theme", 1, "blue");
        Assert.Equal("theme=dark; session=old; theme=blue", changedRequest.HeaderValues("Cookie").Single());

        var response = HttpPacketCodec.Parse("HTTP/1.1 200 OK\r\nSet-Cookie: sid=old; Path=/; HttpOnly\r\nSet-Cookie: sid=second; Secure\r\n\r\n");
        var changedResponse = HttpPacketParameters.Set(response, HttpParameterLocation.Cookie, "sid", 1, "new");
        Assert.Equal("sid=new; Secure", changedResponse.HeaderValues("Set-Cookie").Last());
        Assert.Throws<ArgumentException>(() => HttpPacketParameters.Set(request,
            HttpParameterLocation.Cookie, "session", 0, "bad; injected=yes"));
    }

    private const string MultipartBody =
        "--X-BOUND\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nold value\r\n" +
        "--X-BOUND\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nhello\r\n--X-BOUND--\r\n";

    [Fact]
    public void Read_ListsMultipartPartsAndToleratesBrokenBoundary()
    {
        var packet = HttpPacketCodec.Parse(
            "POST /upload HTTP/1.1\r\nContent-Type: multipart/form-data; boundary=X-BOUND\r\n\r\n" + MultipartBody);
        var parts = HttpPacketParameters.Read(packet)
            .Where(item => item.Location == HttpParameterLocation.Multipart).ToArray();

        Assert.Equal(["note", "title"], parts.Select(item => item.Name));
        Assert.Equal(["old value", "hello"], parts.Select(item => item.Value));

        var broken = HttpPacketCodec.Parse(
            "POST /upload HTTP/1.1\r\nContent-Type: multipart/form-data\r\n\r\n--X-BOUND\r\n\r\n");
        Assert.Empty(HttpPacketParameters.Read(broken)
            .Where(item => item.Location == HttpParameterLocation.Multipart));
    }

    [Fact]
    public void Set_MultipartReplacesOnePartAndPreservesTheRestVerbatim()
    {
        var packet = HttpPacketCodec.Parse(
            "POST /upload HTTP/1.1\r\nContent-Type: multipart/form-data; boundary=X-BOUND\r\n\r\n" + MultipartBody);

        var changed = HttpPacketParameters.Set(packet, HttpParameterLocation.Multipart, "title", 0, "hi there");

        Assert.Contains("name=\"title\"\r\n\r\nhi there\r\n", changed.Body);
        Assert.Contains("\r\nold value\r\n", changed.Body);
        Assert.EndsWith("--X-BOUND--\r\n", changed.Body);
        Assert.Equal(MultipartBody.Length - "hello".Length + "hi there".Length, changed.Body.Length);

        Assert.Throws<KeyNotFoundException>(() =>
            HttpPacketParameters.Set(packet, HttpParameterLocation.Multipart, "absent", 0, "x"));
        var plain = HttpPacketCodec.Parse("POST / HTTP/1.1\r\nContent-Type: text/plain\r\n\r\nbody");
        Assert.Throws<InvalidDataException>(() =>
            HttpPacketParameters.Set(plain, HttpParameterLocation.Multipart, "note", 0, "x"));
    }
}
