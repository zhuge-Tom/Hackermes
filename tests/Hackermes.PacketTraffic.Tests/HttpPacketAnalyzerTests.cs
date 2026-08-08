using Hackermes.Automation.Packet;
using System.Linq;
using System.Text;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class HttpPacketAnalyzerTests
{
    [Fact]
    public void Analyze_reports_content_length_ambiguities_and_utf8_byte_mismatch()
    {
        var packet = HttpPacketCodec.Parse(
            "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 1\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n中文");

        var codes = HttpPacketAnalyzer.Analyze(packet).Findings.Select(x => x.Code).ToArray();

        Assert.Contains("content-length-mismatch", codes);
        Assert.Contains("ambiguous-content-length", codes);
        Assert.Contains("te-cl-ambiguity", codes);
    }

    [Fact]
    public void Findings_expose_stable_side_header_and_utf8_body_edit_locations()
    {
        const string body = "{\"前缀\":1,\"access_token\":\"secret\"}";
        var packet = HttpPacketCodec.Parse(
            "POST /login HTTP/1.1\r\nHost: example.test\r\nAuthorization: secret\r\n\r\n" + body);

        var analysis = HttpPacketAnalyzer.Analyze(packet);
        var header = analysis.Findings.Single(item => item.Code == "sensitive-header");
        var bodyFinding = analysis.Findings.Single(item => item.Code == "sensitive-body-field");

        Assert.Equal(PacketFindingSide.Request, header.Side);
        Assert.Equal(PacketFindingLocationKind.Header, header.LocationKind);
        Assert.Equal("Authorization", header.HeaderName);
        Assert.Equal(0, header.HeaderOccurrence);
        Assert.Equal(PacketFindingLocationKind.Body, bodyFinding.LocationKind);
        Assert.Equal(Encoding.UTF8.GetByteCount(body[..body.IndexOf("access_token", System.StringComparison.Ordinal)]), bodyFinding.BodyOffset);
        Assert.Equal(Encoding.UTF8.GetByteCount("access_token"), bodyFinding.BodyLength);
    }

    [Fact]
    public void Analyze_detects_sensitive_headers_form_fields_and_json_fields()
    {
        var form = HttpPacketCodec.Parse(
            "POST http://example.test/login HTTP/1.1\r\nHost: example.test\r\nAuthorization: Bearer secret\r\nCookie: sid=1\r\n\r\nusername=a&password=p&client_secret=s");
        var json = HttpPacketCodec.Parse(
            "POST /login HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n\r\n{\"access_token\":\"t\"}");

        var formAnalysis = HttpPacketAnalyzer.Analyze(form);
        var jsonAnalysis = HttpPacketAnalyzer.Analyze(json);

        Assert.Contains("header:Authorization", formAnalysis.SensitiveFields);
        Assert.Contains("header:Cookie", formAnalysis.SensitiveFields);
        Assert.Contains("body:password", formAnalysis.SensitiveFields);
        Assert.Contains("body:client_secret", formAnalysis.SensitiveFields);
        Assert.Contains(formAnalysis.Findings, x => x.Code == "plaintext-secret" && x.Severity == PacketFindingSeverity.High);
        Assert.Contains("body:access_token", jsonAnalysis.SensitiveFields);
    }

    [Fact]
    public void Diff_is_semantic_and_includes_duplicate_header_values()
    {
        var left = HttpPacketCodec.Parse("GET /a HTTP/1.1\r\nX-Tag: 1\r\nX-Tag: 2\r\n\r\nleft");
        var right = HttpPacketCodec.Parse("POST /b HTTP/1.1\r\nX-Tag: 1\r\nX-Tag: 3\r\n\r\nright");

        var differences = HttpPacketAnalyzer.Diff(left, right).ToDictionary(x => x.Location);

        Assert.Equal("GET", differences["start.method"].Left);
        Assert.Equal("/b", differences["start.target"].Right);
        Assert.Equal("1\n2", differences["header:X-Tag"].Left);
        Assert.Equal("1\n3", differences["header:X-Tag"].Right);
        Assert.Equal("right", differences["body"].Right);
    }
}
