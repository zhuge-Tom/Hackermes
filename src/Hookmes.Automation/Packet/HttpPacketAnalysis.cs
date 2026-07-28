using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hookmes.Automation.Packet;

public enum PacketFindingSeverity { Info, Warning, High }

public sealed record PacketFinding(PacketFindingSeverity Severity, string Code, string Message, string? Location = null);
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
        foreach (var header in packet.Headers)
            if (IsSensitive(header.Name)) sensitive.Add($"header:{header.Name}");

        foreach (Match match in FormFieldRegex().Matches(packet.Body))
            if (IsSensitive(Uri.UnescapeDataString(match.Groups[1].Value))) sensitive.Add($"body:{match.Groups[1].Value}");
        foreach (Match match in JsonFieldRegex().Matches(packet.Body))
            if (IsSensitive(match.Groups[1].Value)) sensitive.Add($"body:{match.Groups[1].Value}");

        if (packet.HeaderValues("Content-Length").FirstOrDefault() is { } length &&
            long.TryParse(length, out var declared) && declared != Encoding.UTF8.GetByteCount(packet.Body))
            findings.Add(new(PacketFindingSeverity.Warning, "content-length-mismatch", "Content-Length does not match the UTF-8 body length.", "header:Content-Length"));
        if (packet.Kind == HttpPacketKind.Request && packet.Target is { } target && Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == "http" && sensitive.Count > 0)
            findings.Add(new(PacketFindingSeverity.High, "plaintext-secret", "Sensitive values may be transmitted over plaintext HTTP.", "request-target"));
        if (packet.Kind == HttpPacketKind.Request && !packet.HeaderValues("Host").Any() && packet.ProtocolVersion.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
            findings.Add(new(PacketFindingSeverity.Warning, "missing-host", "HTTP/1.1 request has no Host header.", "header:Host"));
        if (packet.Headers.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && g.Select(h => h.Value).Distinct().Count() > 1))
            findings.Add(new(PacketFindingSeverity.High, "ambiguous-content-length", "Conflicting Content-Length headers can enable request smuggling.", "header:Content-Length"));
        if (packet.HeaderValues("Transfer-Encoding").Any() && packet.HeaderValues("Content-Length").Any())
            findings.Add(new(PacketFindingSeverity.High, "te-cl-ambiguity", "Both Transfer-Encoding and Content-Length are present.", "headers"));
        if (packet.Headers.Any(h => h.Value.Contains("\r", StringComparison.Ordinal) || h.Value.Contains("\n", StringComparison.Ordinal)))
            findings.Add(new(PacketFindingSeverity.High, "header-injection", "A header value contains a line break.", "headers"));
        if (sensitive.Count > 0)
            findings.Add(new(PacketFindingSeverity.Info, "sensitive-data", $"Detected {sensitive.Count} sensitive field(s); redact before sharing.", "packet"));
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

    private static bool IsSensitive(string name) => SensitiveNames.Any(s =>
        name.Equals(s, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?:^|&)([^=&]+)=", RegexOptions.CultureInvariant)]
    private static partial Regex FormFieldRegex();
    [GeneratedRegex("[\\\"']([^\\\"']+)[\\\"']\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex JsonFieldRegex();
}
