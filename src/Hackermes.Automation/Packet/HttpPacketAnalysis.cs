using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hackermes.Automation.Packet;

public enum PacketFindingSeverity { Info, Warning, High }
public enum PacketFindingSide { Unknown, Request, Response }
public enum PacketFindingLocationKind { Packet, StartLine, Header, Body }

public sealed record PacketFinding(PacketFindingSeverity Severity, string Code, string Message, string? Location = null)
{
    public PacketFindingSide Side { get; init; }
    public PacketFindingLocationKind LocationKind { get; init; } = PacketFindingLocationKind.Packet;
    public string? Field { get; init; }
    public string? HeaderName { get; init; }
    public int? HeaderOccurrence { get; init; }
    public long? BodyOffset { get; init; }
    public int? BodyLength { get; init; }
}
public sealed record PacketAnalysis(IReadOnlyList<PacketFinding> Findings, IReadOnlyList<string> SensitiveFields);
public sealed record PacketDifference(string Location, string? Left, string? Right);

public static partial class HttpPacketAnalyzer
{
    private static readonly string[] SensitiveNames =
        ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key", "token", "access_token", "password", "passwd", "secret", "client_secret"];

    public static PacketAnalysis Analyze(HttpPacket packet)
    {
        var findings = new List<PacketFinding>();
        var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var side = packet.Kind == HttpPacketKind.Request ? PacketFindingSide.Request : PacketFindingSide.Response;
        for (var index = 0; index < packet.Headers.Count; index++)
        {
            var header = packet.Headers[index];
            if (!IsSensitive(header.Name)) continue;
            sensitive.Add($"header:{header.Name}");
            findings.Add(AtHeader(PacketFindingSeverity.Info, "sensitive-header",
                $"Header '{header.Name}' may contain sensitive data.", side, header.Name,
                packet.Headers.Take(index).Count(item => item.Name.Equals(header.Name, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var match in FormFieldRegex().Matches(packet.Body).Cast<Match>().Concat(JsonFieldRegex().Matches(packet.Body).Cast<Match>()))
        {
            var field = match.Groups[1];
            var decodedName = Uri.UnescapeDataString(field.Value);
            if (!IsSensitive(decodedName)) continue;
            sensitive.Add($"body:{field.Value}");
            findings.Add(AtBody(PacketFindingSeverity.Info, "sensitive-body-field",
                $"Body field '{field.Value}' may contain sensitive data.", side, packet.Body, field.Index, field.Length, field.Value));
        }

        if (packet.HeaderValues("Content-Length").FirstOrDefault() is { } length &&
            long.TryParse(length, out var declared) && declared != Encoding.UTF8.GetByteCount(packet.Body))
            findings.Add(AtHeader(PacketFindingSeverity.Warning, "content-length-mismatch", "Content-Length does not match the UTF-8 body length.", side, "Content-Length"));
        if (packet.Kind == HttpPacketKind.Request && packet.Target is { } target && Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == "http" && sensitive.Count > 0)
            findings.Add(AtStart(PacketFindingSeverity.High, "plaintext-secret", "Sensitive values may be transmitted over plaintext HTTP.", side, "target"));
        if (packet.Kind == HttpPacketKind.Request && !packet.HeaderValues("Host").Any() && packet.ProtocolVersion.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
            findings.Add(AtHeader(PacketFindingSeverity.Warning, "missing-host", "HTTP/1.1 request has no Host header.", side, "Host"));
        if (packet.Headers.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && g.Select(h => h.Value).Distinct().Count() > 1))
            findings.Add(AtHeader(PacketFindingSeverity.High, "ambiguous-content-length", "Conflicting Content-Length headers can enable request smuggling.", side, "Content-Length"));
        if (packet.HeaderValues("Transfer-Encoding").Any() && packet.HeaderValues("Content-Length").Any())
            findings.Add(AtHeader(PacketFindingSeverity.High, "te-cl-ambiguity", "Both Transfer-Encoding and Content-Length are present.", side, "Transfer-Encoding"));
        if (packet.Headers.Any(h => h.Value.Contains("\r", StringComparison.Ordinal) || h.Value.Contains("\n", StringComparison.Ordinal)))
            findings.Add(AtHeader(PacketFindingSeverity.High, "header-injection", "A header value contains a line break.", side,
                packet.Headers.First(h => h.Value.Contains("\r", StringComparison.Ordinal) || h.Value.Contains("\n", StringComparison.Ordinal)).Name));
        if (sensitive.Count > 0)
            findings.Add(new PacketFinding(PacketFindingSeverity.Info, "sensitive-data", $"Detected {sensitive.Count} sensitive field(s); redact before sharing.", "packet") { Side = side });
        if (packet.Kind == HttpPacketKind.Response)
            AddResponseSecurityObservations(packet, findings, side);
        return new PacketAnalysis(findings, sensitive.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static IReadOnlyList<PacketDifference> Diff(HttpPacket left, HttpPacket right)
    {
        var result = new List<PacketDifference>();
        Add("kind", left.Kind.ToString(), right.Kind.ToString());
        Add("start.method", left.Method, right.Method); Add("start.target", left.Target, right.Target);
        Add("start.status", left.StatusCode?.ToString(), right.StatusCode?.ToString());
        Add("start.reason", left.ReasonPhrase, right.ReasonPhrase); Add("start.version", left.ProtocolVersion, right.ProtocolVersion);
        var names = left.Headers.Concat(right.Headers).Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            Add($"header:{name}", string.Join("\n", left.HeaderValues(name)), string.Join("\n", right.HeaderValues(name)));
        Add("body", left.Body, right.Body);
        return result;

        void Add(string location, string? a, string? b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal)) result.Add(new(location, a, b));
        }
    }

    private static void AddResponseSecurityObservations(HttpPacket packet, List<PacketFinding> findings, PacketFindingSide side)
    {
        var cspValues = packet.HeaderValues("Content-Security-Policy").ToArray();
        if (cspValues.Length == 0)
            findings.Add(AtHeader(PacketFindingSeverity.Warning, "missing-csp",
                "The response has no Content-Security-Policy header.", side, "Content-Security-Policy"));
        else
        {
            var tokens = string.Join(';', cspValues).Split([' ', '\t', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Contains("'unsafe-inline'", StringComparer.OrdinalIgnoreCase))
                findings.Add(AtHeader(PacketFindingSeverity.Warning, "csp-unsafe-inline",
                    "The Content-Security-Policy allows unsafe-inline.", side, "Content-Security-Policy"));
            if (tokens.Contains("'unsafe-eval'", StringComparer.OrdinalIgnoreCase))
                findings.Add(AtHeader(PacketFindingSeverity.Warning, "csp-unsafe-eval",
                    "The Content-Security-Policy allows unsafe-eval.", side, "Content-Security-Policy"));
            if (tokens.Contains("*", StringComparer.Ordinal))
                findings.Add(AtHeader(PacketFindingSeverity.Warning, "csp-wildcard-src",
                    "The Content-Security-Policy includes a wildcard source.", side, "Content-Security-Policy"));
        }

        if (!packet.HeaderValues("X-Content-Type-Options").Any())
            findings.Add(AtHeader(PacketFindingSeverity.Info, "missing-xcto",
                "The response has no X-Content-Type-Options header.", side, "X-Content-Type-Options"));

        var hasFrameAncestors = cspValues.Any(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Any(name => name is not null && name.Equals("frame-ancestors", StringComparison.OrdinalIgnoreCase)));
        if (!packet.HeaderValues("X-Frame-Options").Any() && !hasFrameAncestors)
            findings.Add(AtHeader(PacketFindingSeverity.Warning, "missing-frame-protection",
                "The response has no frame protection.", side, "X-Frame-Options"));

        if (!packet.HeaderValues("Strict-Transport-Security").Any() && IsHttpsTarget(packet))
            findings.Add(AtHeader(PacketFindingSeverity.Warning, "missing-hsts",
                "The response has no Strict-Transport-Security header.", side, "Strict-Transport-Security"));

        var cookieIndex = 0;
        foreach (var setCookie in packet.HeaderValues("Set-Cookie"))
        {
            var occurrence = cookieIndex++;
            var attributes = setCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToArray();
            if (!attributes.Any(attribute => string.Equals(attribute, "secure", StringComparison.OrdinalIgnoreCase)))
                findings.Add(AtHeader(PacketFindingSeverity.Warning, "cookie-missing-secure",
                    "A Set-Cookie header is missing the Secure attribute.", side, "Set-Cookie", occurrence));
            if (!attributes.Any(attribute => string.Equals(attribute, "httponly", StringComparison.OrdinalIgnoreCase)))
                findings.Add(AtHeader(PacketFindingSeverity.Warning, "cookie-missing-httponly",
                    "A Set-Cookie header is missing the HttpOnly attribute.", side, "Set-Cookie", occurrence));
        }
    }

    private static bool IsHttpsTarget(HttpPacket packet) =>
        packet.Target is { Length: > 0 } target &&
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitive(string name) => SensitiveNames.Any(s =>
        name.Equals(s, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase));

    private static PacketFinding AtHeader(PacketFindingSeverity severity, string code, string message,
        PacketFindingSide side, string headerName, int? occurrence = null) =>
        new(severity, code, message, $"header:{headerName}")
        {
            Side = side, LocationKind = PacketFindingLocationKind.Header,
            HeaderName = headerName, HeaderOccurrence = occurrence
        };

    private static PacketFinding AtStart(PacketFindingSeverity severity, string code, string message,
        PacketFindingSide side, string field) => new(severity, code, message, $"start:{field}")
        { Side = side, LocationKind = PacketFindingLocationKind.StartLine, Field = field };

    private static PacketFinding AtBody(PacketFindingSeverity severity, string code, string message,
        PacketFindingSide side, string body, int characterOffset, int characterLength, string field) =>
        new(severity, code, message, $"body:{field}")
        {
            Side = side, LocationKind = PacketFindingLocationKind.Body, Field = field,
            BodyOffset = Encoding.UTF8.GetByteCount(body.AsSpan(0, characterOffset)),
            BodyLength = Encoding.UTF8.GetByteCount(body.AsSpan(characterOffset, characterLength))
        };

    [GeneratedRegex(@"(?:^|&)([^=&]+)=", RegexOptions.CultureInvariant)]
    private static partial Regex FormFieldRegex();
    [GeneratedRegex("[\\\"']([^\\\"']+)[\\\"']\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex JsonFieldRegex();
}
