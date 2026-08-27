using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hackermes.Assessment;

/// <summary>
/// Bounded, value-free summaries of authorized recon adapter stdout.
/// Emits observation codes only — never confirmed vulnerabilities or payloads.
/// </summary>
public static partial class ReconObservationParser
{
    public sealed record Observation(string Code, string Severity, string Title, string Message);

    public static IReadOnlyList<Observation> Parse(string adapterId, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var text = output.Length > 262_144 ? output[..262_144] : output;
        return adapterId switch
        {
            AuthorizedToolCatalog.NmapQuick or AuthorizedToolCatalog.NmapService => ParseNmap(text),
            AuthorizedToolCatalog.DirsearchQuick => ParseDirsearch(text),
            AuthorizedToolCatalog.Wafw00fQuick => ParseWaf(text),
            AuthorizedToolCatalog.HttpHeadersProbe or AuthorizedToolCatalog.HttpGetProbe => ParseHttpHeaders(text),
            AuthorizedToolCatalog.HttpxProbe => ParseHttpx(text),
            AuthorizedToolCatalog.DnsResolve => ParseDns(text),
            _ => ParseGeneric(text)
        };
    }

    private static IReadOnlyList<Observation> ParseNmap(string text)
    {
        var open = NmapOpenPortRegex().Matches(text).Count;
        if (open <= 0) return [];
        return
        [
            new Observation("open-ports", "Info", "Open ports listed",
                $"Adapter listed {open} open port line(s). Review the evidence; this is not a confirmed vulnerability.")
        ];
    }

    private static IReadOnlyList<Observation> ParseDirsearch(string text)
    {
        var hits = DirsearchHitRegex().Matches(text).Count;
        if (hits <= 0) return [];
        return
        [
            new Observation("dirsearch-hits", "Info", "Directory probe hits",
                $"Adapter listed {hits} HTTP 2xx/3xx path line(s). Review the evidence; paths are not confirmed findings.")
        ];
    }

    private static IReadOnlyList<Observation> ParseWaf(string text)
    {
        if (!text.Contains("is behind", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("WAF", StringComparison.OrdinalIgnoreCase))
            return [];
        return
        [
            new Observation("waf-detected", "Info", "WAF indication",
                "Adapter output indicated a web application firewall. Confirm in evidence before recording a finding.")
        ];
    }

    private static IReadOnlyList<Observation> ParseHttpHeaders(string text)
    {
        var items = new List<Observation>();
        if (!HasHeader(text, "strict-transport-security"))
            items.Add(new Observation("missing-hsts", "Warning", "No HSTS in recon headers",
                "Recon response headers did not include Strict-Transport-Security."));
        if (!HasHeader(text, "content-security-policy"))
            items.Add(new Observation("missing-csp", "Warning", "No CSP in recon headers",
                "Recon response headers did not include Content-Security-Policy."));
        if (!HasHeader(text, "x-content-type-options"))
            items.Add(new Observation("missing-xcto", "Info", "No XCTO in recon headers",
                "Recon response headers did not include X-Content-Type-Options."));
        if (!HasHeader(text, "x-frame-options") &&
            text.IndexOf("frame-ancestors", StringComparison.OrdinalIgnoreCase) < 0)
            items.Add(new Observation("missing-frame-protection", "Warning", "No frame protection in recon headers",
                "Recon response headers did not include X-Frame-Options or CSP frame-ancestors."));
        return items;
    }

    private static IReadOnlyList<Observation> ParseHttpx(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (lines <= 0) return [];
        return
        [
            new Observation("httpx-probe", "Info", "HTTP probe lines",
                $"Adapter listed {lines} probe line(s). Review the evidence.")
        ];
    }

    private static IReadOnlyList<Observation> ParseDns(string text)
    {
        if (text.IndexOf("address", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("Name:", StringComparison.OrdinalIgnoreCase) < 0)
            return [];
        return
        [
            new Observation("dns-resolved", "Info", "DNS resolution output",
                "Adapter returned a DNS resolution transcript. Review the evidence.")
        ];
    }

    private static IReadOnlyList<Observation> ParseGeneric(string text)
    {
        if (text.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0)
            return [];
        return
        [
            new Observation("recon-error", "Info", "Adapter reported an error",
                "Adapter output contained an error token. Read the evidence; do not treat this as a vulnerability.")
        ];
    }

    private static bool HasHeader(string text, string name)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\b\d{1,5}/(?:tcp|udp)\s+open\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NmapOpenPortRegex();

    [GeneratedRegex(@"^\s*(?:200|201|204|301|302|307|308)\s+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DirsearchHitRegex();
}
