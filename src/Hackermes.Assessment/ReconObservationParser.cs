using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hackermes.Assessment;

/// <summary>
/// Bounded, value-free summaries of authorized recon adapter stdout.
/// Emits observation codes only — never confirmed vulnerabilities or payloads.
/// </summary>
public static partial class ReconObservationParser
{
    public sealed record Observation(string Code, string Severity, string Title, string Message, string? PoC = null);

    public static IReadOnlyList<Observation> Parse(string adapterId, string output, string? probeInput = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var text = output.Length > 262_144 ? output[..262_144] : output;
        return adapterId switch
        {
            AuthorizedToolCatalog.NmapQuick or AuthorizedToolCatalog.NmapService => ParseNmap(text),
            AuthorizedToolCatalog.DirsearchQuick => ParseDirsearch(text),
            AuthorizedToolCatalog.Wafw00fQuick => ParseWaf(text),
            AuthorizedToolCatalog.HttpHeadersProbe or AuthorizedToolCatalog.HttpGetProbe => ParseHttpHeaders(text, probeInput),
            AuthorizedToolCatalog.HttpxProbe => ParseHttpx(text),
            AuthorizedToolCatalog.DnsResolve => ParseDns(text),
            AuthorizedToolCatalog.ProbeSqlmapInject => ParseSqlmap(text, probeInput),
            AuthorizedToolCatalog.ProbeUnauthorizedAccess => ParseUnauthorized(text, probeInput),
            AuthorizedToolCatalog.ProbeParamCorpus => ParseParamCorpus(text, probeInput),
            AuthorizedToolCatalog.ReconSubdomainEnum => ParseSubdomains(text),
            AuthorizedToolCatalog.ReconGitLeakScan => ParseGitLeak(text, probeInput),
            AuthorizedToolCatalog.ReconSvnLeakScan => ParseSvnLeak(text, probeInput),
            AuthorizedToolCatalog.ReconDsStoreScan => ParseDsStore(text, probeInput),
            AuthorizedToolCatalog.ReconSwaggerApiEnum => ParseSwaggerApi(text, probeInput),
            AuthorizedToolCatalog.DetectWeblogicT3Scan => ParseWeblogicT3(text),
            AuthorizedToolCatalog.DetectFastjsonJndiScan => ParseFastjsonJndi(text),
            AuthorizedToolCatalog.ExploitVcenterVerify => ParseVcenterVerify(text),
            AuthorizedToolCatalog.DetectOaPocProbe => ParseOaPoc(text, probeInput),
            AuthorizedToolCatalog.DetectShiroScan => ParseShiro(text),
            AuthorizedToolCatalog.DetectStruts2Scan => ParseStruts2(text, probeInput),
            AuthorizedToolCatalog.DetectNacosScan => ParseNacos(text),
            AuthorizedToolCatalog.ProbeCloudAkskVerify => ParseCloudVerify(text),
            AuthorizedToolCatalog.ExploitHeapdumpAnalyze => ParseHeapdump(text),
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

    private static IReadOnlyList<Observation> ParseHttpHeaders(string text, string? probeInput)
    {
        var items = new List<Observation>();
        if (!HasHeader(text, "strict-transport-security"))
            items.Add(new Observation("missing-hsts", "Low", "No HSTS in recon headers",
                "Recon response headers did not include Strict-Transport-Security."));
        if (!HasHeader(text, "content-security-policy"))
            items.Add(new Observation("missing-csp", "Low", "No CSP in recon headers",
                "Recon response headers did not include Content-Security-Policy."));
        if (!HasHeader(text, "x-content-type-options"))
            items.Add(new Observation("missing-xcto", "Info", "No XCTO in recon headers",
                "Recon response headers did not include X-Content-Type-Options."));
        if (!HasHeader(text, "x-frame-options") &&
            text.IndexOf("frame-ancestors", StringComparison.OrdinalIgnoreCase) < 0)
            items.Add(new Observation("missing-frame-protection", "Low", "No frame protection in recon headers",
                "Recon response headers did not include X-Frame-Options or CSP frame-ancestors."));

        var downgrade = PlanToCleartextDowngrade(text, probeInput);
        if (downgrade is not null) items.Add(downgrade);
        return items;
    }

    private static Observation? PlanToCleartextDowngrade(string text, string? probeInput)
    {
        var location = HeaderValue(text, "Location");
        if (location is null) return null;
        location = location.Trim();
        var scheme = ProbeScheme(probeInput);
        if (!string.Equals(scheme, "https", StringComparison.Ordinal)) return null;
        if (!location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;

        var request = ProbeRequest(probeInput);
        var poc = new StringBuilder($"# Cleartext downgrade reproduction (authorized recon)\n")
            .AppendLine($"probe: {request}")
            .AppendLine($"response: {HeaderStatusLine(text) ?? "3xx"}")
            .AppendLine($"location: {location}")
            .AppendLine("note: an HTTPS probe was answered with an unencrypted http:// redirect, exfiltrating any credentials or session cookies it would carry.");
        return new Observation("https-downgrade-cleartext", "Medium",
            "HTTPS probe downgraded to unencrypted HTTP redirect",
            "Recon observed a 3xx to a plaintext http:// URL from an internal HTTPS probe. Verify whether authenticated traffic follows this redirect before treating as a finding.",
            LimitPoC(poc.ToString()));
    }

    private static string? HeaderValue(string text, string name)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static string? HeaderStatusLine(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return line.Trim();
        return null;
    }

    private static string LimitPoC(string value) => value.Length > 2000 ? value[..2000] : value;

    private static string ProbeScheme(string? probeInput) => OptionText(probeInput, "scheme") is { Length: > 0 } scheme ? scheme.Trim().ToLowerInvariant() : string.Empty;

    private static string ProbeRequest(string? probeInput)
    {
        var scheme = OptionText(probeInput, "scheme") ?? string.Empty;
        var target = OptionText(probeInput, "target") ?? string.Empty;
        var port = OptionText(probeInput, "port") ?? string.Empty;
        var path = OptionText(probeInput, "path") ?? string.Empty;
        var hostPort = port.Length > 0 && port != "443" && port != "80" ? $"{target}:{port}" : target;
        var url = $"{(scheme.Length > 0 ? scheme : "https")}://{hostPort}{(path.Length > 0 ? path : "/")}";
        return $"curl -sSI --connect-timeout 10 '{url}'";
    }

    private static string? OptionText(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
                ? item.GetString() : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : null;
        }
        catch (JsonException) { return null; }
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

    private static IReadOnlyList<Observation> ParseSqlmap(string text, string? probeInput)
    {
        var suppressed = text.Contains("do not appear to be injectable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not seem to be injectable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("all tested parameters do not appear", StringComparison.OrdinalIgnoreCase);
        if (suppressed || !text.Contains("is vulnerable", StringComparison.OrdinalIgnoreCase)) return [];
        var poc = new StringBuilder("# SQL injection reproduction (authorized recon)\n")
            .AppendLine($"probe: {SqlmapProbe(probeInput)}")
            .AppendLine("result: sqlmap reported the parameter as vulnerable; confirm the payload and impact before filing.");
        return
        [
            new Observation("sqli-confirmed", "High", "SQL injection confirmed by sqlmap",
                "sqlmap reported the target parameter as vulnerable. Verify the payload and impact before filing.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseUnauthorized(string text, string? probeInput)
    {
        var observations = new List<Observation>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = line.IndexOf(": Vulnerable", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0) continue;
            var service = line[..marker].Trim().TrimStart('-', ' ');
            if (service.Length == 0) continue;
            var url = $"{ProbeSiteUrl(probeInput)}{(service == "swagger" ? "swagger-ui.html" : service)}";
            observations.Add(new Observation("unauthorized-access", "High", $"Unauthorized access: {service}",
                $"The unauthorized-access scanner reported '{service}' exposed without authentication. Confirm on the authorized target.",
                LimitPoC($"# Unauthorized access reproduction (authorized recon)\nprobe: GET {url}\nresult: returned an unauthenticated response in verify.")));
            if (observations.Count >= 3) break;
        }
        return observations;
    }

    private static string SqlmapProbe(string? probeInput)
    {
        var scheme = OptionText(probeInput, "scheme") ?? "https";
        var target = OptionText(probeInput, "target") ?? string.Empty;
        var port = OptionText(probeInput, "port") ?? (scheme == "https" ? "443" : "80");
        var path = OptionText(probeInput, "path") ?? "/";
        var parameter = OptionText(probeInput, "parameter") ?? "id";
        var value = OptionText(probeInput, "value") ?? "1";
        var hostPort = port is "443" or "80" ? target : $"{target}:{port}";
        return $"sqlmap -u \"{scheme}://{hostPort}{path}?{parameter}={value}\" --batch --level 1 --risk 1 --technique=BE";
    }

    private static string ProbeSiteUrl(string? probeInput)
    {
        var scheme = OptionText(probeInput, "scheme") ?? "http";
        var target = OptionText(probeInput, "target") ?? string.Empty;
        var port = OptionText(probeInput, "port") ?? (scheme == "https" ? "443" : "80");
        var hostPort = port is "443" or "80" ? target : $"{target}:{port}";
        return $"{scheme}://{hostPort}/";
    }

    private static IReadOnlyList<Observation> ParseParamCorpus(string text, string? probeInput)
    {
        var observations = new List<Observation>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("CANDIDATE ", StringComparison.Ordinal)) continue;
            var parameter = SkimValue(line, "param=");
            var payload = SkimValue(line, "payload=");
            var status = SkimValue(line, "status=");
            var baselineStatus = SkimValue(line, "baseline=");
            var code = $"injection-candidate-{parameter}";
            var poc = new StringBuilder("# Possible injection indicator — verify (authorized recon)\n")
                .AppendLine($"probe: {CorpusProbe(probeInput, parameter, payload)}")
                .AppendLine($"behavior: status {status} vs baseline {baselineStatus}")
                .AppendLine("note: a payload changed the response; this is a candidate, NOT a confirmed vulnerability. Confirm with probe.sqlmap.inject or manual review before filing.");
            observations.Add(new Observation(code, "Medium",
                $"Injection indicator candidate on '{parameter}'",
                "The payload corpus produced a distinguishable response for this parameter. Verify before treating as a finding.",
                LimitPoC(poc.ToString())));
            if (observations.Count >= 3) break;
        }
        return observations;
    }

    private static string? SkimValue(string line, string token)
    {
        var index = line.IndexOf(token, StringComparison.Ordinal);
        if (index < 0) return null;
        var start = index + token.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static string CorpusProbe(string? probeInput, string? parameter, string? payload)
    {
        var scheme = OptionText(probeInput, "scheme") ?? "http";
        var target = OptionText(probeInput, "target") ?? string.Empty;
        var port = OptionText(probeInput, "port") ?? (scheme == "https" ? "443" : "80");
        var path = OptionText(probeInput, "path") ?? "/";
        var value = OptionText(probeInput, "value") ?? "1";
        var hostPort = port is "443" or "80" ? target : $"{target}:{port}";
        return $"GET {scheme}://{hostPort}{path}?{(parameter ?? "id")}={(payload ?? value)}";
    }

    private static IReadOnlyList<Observation> ParseSubdomains(string text)
    {
        var hits = 0;
        var domains = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0) continue;
            var name = line[..arrow].Trim();
            if (name.Length > 0 && name.Contains('.')) { hits++; if (domains.Count < 12) domains.Add(name); }
        }
        if (hits <= 0) return [];
        return
        [
            new Observation("subdomains-resolved", "Info", "Resolved subdomains",
                $"Subdomain enumeration resolved {hits} in-scope host(s) — {string.Join(", ", domains)}. Confirm each before scanning.",
                LimitPoC($"# Subdomain recon (authorized)\n{string.Join("\n", domains)}"))
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

    private static IReadOnlyList<Observation> ParseGitLeak(string text, string? probeInput)
    {
        if (text.IndexOf("Clone Success", StringComparison.OrdinalIgnoreCase) < 0)
            return [];
        var poc = new StringBuilder("# .git repository disclosure (authorized recon)\n")
            .AppendLine($"probe: {ProbeSiteUrl(probeInput).TrimEnd('/')}/.git/")
            .AppendLine("result: GitHack restored the remote git repository; review the recovered file list in the evidence and remove any uploaded artifacts after verification.");
        return
        [
            new Observation("git-repo-disclosure", "Medium", ".git repository disclosed and restored",
                "GitHack cloned the exposed .git directory. Recovered sources may contain credentials and deployment details; confirm and report source-code disclosure.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseSvnLeak(string text, string? probeInput)
    {
        if (text.IndexOf("wc.db", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("|", StringComparison.Ordinal) < 0)
            return [];
        var rows = text.Split('\n').Count(line => line.Contains('|', StringComparison.Ordinal) &&
            !line.Contains("文件名", StringComparison.Ordinal) && !line.Contains("---", StringComparison.Ordinal));
        if (rows <= 0)
            return [];
        var poc = new StringBuilder("# .svn repository disclosure (authorized recon)\n")
            .AppendLine($"probe: {ProbeSiteUrl(probeInput).TrimEnd('/')}/.svn/wc.db")
            .AppendLine($"result: SvnExploit enumerated {rows} versioned file(s); confirm source-code disclosure in the evidence.");
        return
        [
            new Observation("svn-repo-disclosure", "Medium", ".svn repository disclosed",
                $"SvnExploit listed {rows} version-controlled file(s) from the exposed .svn directory. Verify before filing.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseDsStore(string text, string? probeInput)
    {
        var entries = DsStoreEntryRegex().Matches(text).Count;
        if (entries <= 0)
            return [];
        var poc = new StringBuilder("# .DS_Store directory disclosure (authorized recon)\n")
            .AppendLine($"probe: {ProbeSiteUrl(probeInput).TrimEnd('/')}/.DS_Store")
            .AppendLine($"result: ds_store_exp enumerated {entries} entry/entries; review the recovered directory tree in the evidence.");
        return
        [
            new Observation("ds-store-disclosure", "Low", ".DS_Store directory entries disclosed",
                $"ds_store_exp enumerated {entries} directory entr(y/ies) from the exposed .DS_Store file. Confirm before treating as a finding.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseSwaggerApi(string text, string? probeInput)
    {
        var endpoints = SwaggerEndpointRegex().Matches(text).Count;
        if (endpoints <= 0)
            return [];
        var poc = new StringBuilder("# Swagger API documentation exposure (authorized recon)\n")
            .AppendLine($"probe: {ProbeSiteUrl(probeInput).TrimEnd('/')}/swagger-ui.html")
            .AppendLine($"result: swagger-hack enumerated {endpoints} API endpoint(s) from the exposed documentation; test for unauthorized access before filing.");
        return
        [
            new Observation("swagger-api-exposure", "Medium", "Swagger documentation enumerates live APIs",
                $"swagger-hack enumerated {endpoints} API endpoint(s). Verify which are callable without authentication.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseWeblogicT3(string text)
    {
        var observations = new List<Observation>();
        foreach (var match in WeblogicHitRegex().Matches(text).Cast<Match>())
        {
            var target = match.Groups[1].Value.Trim();
            var cve = match.Groups[2].Value.Trim();
            if (target.Length == 0 || cve.Length == 0) continue;
            var poc = new StringBuilder("# Weblogic deserialization reproduction (authorized recon)\n")
                .AppendLine($"probe: WeblogicScan -u {target}")
                .AppendLine($"result: T3 endpoint {target} reported {cve} as vulnerable; verify the gadget chain before filing.");
            observations.Add(new Observation($"weblogic-{cve}", "High",
                $"Weblogic deserialization: {cve}",
                $"WeblogicScan reported {cve} on {target}. Confirm the evidence and impact before filing.",
                LimitPoC(poc.ToString())));
            if (observations.Count >= 3) break;
        }
        return observations;
    }

    private static IReadOnlyList<Observation> ParseFastjsonJndi(string text)
    {
        // Measured behaviour (stage-3 calibration): JsonExp's normal run prints "[+] 序号：N"
        // delivery lines and never prints a verdict — the callback verdict comes from the
        // operator's LDAP/RMI listener or DNSLog console, not from stdout. Only an explicit
        // verdict marker in the output counts as a confirmed finding; otherwise stay silent
        // (the evidence still carries every payload for human review).
        var confirmed = text.IndexOf("存在漏洞", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("vulnerable", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!confirmed)
            return [];
        var poc = new StringBuilder("# Fastjson/Jackson payload verdict (authorized recon)\n")
            .AppendLine("probe: JsonExp -u <target> -l ldap://listener")
            .AppendLine("result: JsonExp marked the target vulnerable; verify the callback evidence and listener log before filing.\n");
        return
        [
            new Observation("fastjson-jndi-confirmed", "High",
                "Fastjson/Jackson vulnerability confirmed",
                "JsonExp reported the target as vulnerable to a deserialization payload. Verify the callback evidence before filing.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseVcenterVerify(string text)
    {
        // Measured behaviour (stage-3 calibration): VcenterKiller prints "[+] Upload success"
        // for ANY endpoint answering 2xx — a plain JSON mock produced it. Tool output alone
        // can therefore never confirm a vCenter compromise; record a Medium candidate for
        // human confirmation instead of a High finding.
        if (text.IndexOf("[+]", StringComparison.Ordinal) < 0 &&
            text.IndexOf("vulnerable", StringComparison.OrdinalIgnoreCase) < 0)
            return [];
        var poc = new StringBuilder("# vCenter verification output (authorized)\n")
            .AppendLine("probe: VcenterKiller -u <vcenter-url> -m <cve> [-c command | -t scan | -f shell]")
            .AppendLine("result: VcenterKiller reported positive output; the tool is optimistic — reproduce manually against the target before filing, and remove any uploaded verification shell afterwards.");
        return
        [
            new Observation("vcenter-verification-candidate", "Medium", "vCenter tool reported positive output (needs confirmation)",
                "VcenterKiller produced positive output for the authorized target. Measured calibration shows the tool can report upload success against any 2xx endpoint, so confirm manually in the evidence before filing.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseOaPoc(string text, string? probeInput)
    {
        var observations = new List<Observation>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("[HIT] ", StringComparison.Ordinal)) continue;
            var parts = line[6..].Split('|');
            if (parts.Length < 3) continue;
            var pocName = parts[0].Trim();
            var severity = parts[1].Trim().ToLowerInvariant() switch
            {
                "critical" => "Critical", "high" => "High", "medium" => "Medium",
                "low" => "Low", _ => "Info"
            };
            var endpoint = parts[2].Trim();
            var poc = new StringBuilder("# OA POC hit (authorized recon)\n")
                .AppendLine($"probe: {endpoint}")
                .AppendLine("result: the OA POC matchers reported a hit; confirm exploitability in the evidence before filing.");
            observations.Add(new Observation($"oa-poc-{pocName}", severity,
                $"OA vulnerability indicator: {pocName}",
                $"The OA POC runner matched '{pocName}' on the authorized target. Review the evidence; the POC severity rating is {severity}.",
                LimitPoC(poc.ToString())));
            if (observations.Count >= 3) break;
        }
        return observations;
    }

    private static IReadOnlyList<Observation> ParseCloudVerify(string text)
    {
        // cf output calibration is pending (needs a live cloud account); keep the parser
        // conservative: positive markers produce a Medium candidate for human review, and
        // the full listing always travels in the evidence. The agent must treat any
        // resource listing as proof the credential is VALID and file accordingly.
        var positives = text.Split('\n').Count(line => line.Contains("[+]", StringComparison.Ordinal));
        if (positives <= 0)
            return [];
        var poc = new StringBuilder("# Cloud credential verification (authorized)\n")
            .AppendLine("probe: cf <provider> ls|perm (read-only; staged credential injected via process environment)")
            .AppendLine("result: cf reported positive output — the staged credential is valid. Review the resource listing in the evidence and file with account scope details; never echo the key material.");
        return
        [
            new Observation("cloud-credential-indicator", "Medium", "Cloud credential verified (review listing)",
                $"cf produced {positives} positive output line(s) for the staged credential. The key is valid — review the resource listing in the evidence and file with account scope details.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseShiro(string text)
    {
        // Calibrated against shiro_tool: "[+] ... is use shiro" means the framework was
        // confirmed; "[-] get shiro key fail" means the key was not cracked; anything else
        // ("[-] target may not use shiro") is a miss.
        if (text.IndexOf("is use shiro", StringComparison.OrdinalIgnoreCase) < 0)
            return [];
        var keyCracked = text.IndexOf("get shiro key fail", StringComparison.OrdinalIgnoreCase) < 0;
        var poc = new StringBuilder("# Shiro rememberMe detection (authorized recon)\n")
            .AppendLine("probe: java -jar shiro_tool.jar <target-url>")
            .AppendLine(keyCracked
                ? "result: shiro_tool cracked a usable rememberMe key and reached the gadget menu; verify in the evidence before filing."
                : "result: shiro confirmed but the rememberMe key was not cracked; file as an exposure indicator only.");
        return
        [
            new Observation(keyCracked ? "shiro-key-confirmed" : "shiro-framework-detected",
                keyCracked ? "High" : "Medium",
                keyCracked ? "Shiro rememberMe key confirmed" : "Shiro framework detected (key not cracked)",
                keyCracked
                    ? "shiro_tool confirmed the target uses Shiro and found a usable rememberMe key. Verify the gadget result in the evidence before filing."
                    : "shiro_tool confirmed the target uses Shiro but did not crack the rememberMe key. Keep as an exposure indicator.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static IReadOnlyList<Observation> ParseStruts2(string text, string? probeInput)
    {
        var observations = new List<Observation>();
        foreach (var match in Struts2HitRegex().Matches(text).Cast<Match>())
        {
            var url = match.Groups[1].Value.Trim();
            var vuln = match.Groups[2].Value.Trim();
            if (url.Length == 0 || vuln.Length == 0) continue;
            var poc = new StringBuilder("# Struts2 OGNL detection (authorized recon)\n")
                .AppendLine($"probe: Struts2Scan -u {url} -q")
                .AppendLine($"result: Struts2Scan reported {vuln} as present on {url}; verify the OGNL result in the evidence before filing.");
            observations.Add(new Observation($"struts2-{vuln}", "High",
                $"Struts2 vulnerability: {vuln}",
                $"Struts2Scan confirmed {vuln} on {url}. Review the evidence before filing.",
                LimitPoC(poc.ToString())));
            if (observations.Count >= 3) break;
        }
        return observations;
    }

    private static IReadOnlyList<Observation> ParseNacos(string text)
    {
        var observations = new List<Observation>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("[NACOS-HIT] ", StringComparison.Ordinal)) continue;
            var parts = line[12..].Split('|');
            if (parts.Length < 3) continue;
            var checkId = parts[0].Trim();
            var severity = parts[1].Trim().ToLowerInvariant() switch
            {
                "high" => "High", "medium" => "Medium", "low" => "Low", _ => "Info"
            };
            var endpoint = parts[2].Trim();
            var poc = new StringBuilder("# Nacos exposure check (authorized recon)\n")
                .AppendLine($"probe: {endpoint}")
                .AppendLine("result: the read-only Nacos probe reported this exposure; confirm impact in the evidence before filing.");
            observations.Add(new Observation($"nacos-{checkId}", severity,
                $"Nacos exposure: {checkId}",
                $"The Nacos probe matched '{checkId}' on the authorized target ({severity}). Review the evidence before filing.",
                LimitPoC(poc.ToString())));
            if (observations.Count >= 3) break;
        }
        return observations;
    }

    private static IReadOnlyList<Observation> ParseHeapdump(string text)
    {
        // Measured behaviour (stage-3 calibration): JDumpSpider prints one section per
        // candidate category — "=== banner", category name, "-----", then either values
        // or "not found!". Only sections whose content is not just "not found!" carry
        // extracted material, so the count must come from section contents, not banners.
        var foundCategories = new List<string>();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string? category = null;
        var content = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("=", StringComparison.Ordinal) && line.Length >= 3)
            {
                FinalizeHeapdumpSection(foundCategories, category, content);
                category = null;
                content.Clear();
                var inner = line.Trim('=').Trim();
                if (inner.Length > 0 && inner.Length <= 40)
                    category = inner;
                continue;
            }
            if (line.Length >= 3 && line.All(character => character == '-'))
                continue;
            if (category is null)
            {
                if (line.Length > 0 && line.Length <= 40)
                    category = line;
                continue;
            }
            content.AppendLine(line);
        }
        FinalizeHeapdumpSection(foundCategories, category, content);
        if (foundCategories.Count == 0)
            return [];
        var names = string.Join(", ", foundCategories.Take(5));
        var poc = new StringBuilder("# Heapdump sensitive data extraction (authorized)\n")
            .AppendLine("probe: java -jar JDumpSpider.jar <heapdump>")
            .AppendLine($"result: JDumpSpider extracted {foundCategories.Count} sensitive-data section(s) — {names}. Review the evidence and rotate the exposed credentials immediately.");
        return
        [
            new Observation("heapdump-sensitive-data", "High", "Heapdump contains sensitive data",
                $"JDumpSpider extracted sensitive material in {foundCategories.Count} section(s) — {names}. Rotate the exposed material and file with the evidence.",
                LimitPoC(poc.ToString()))
        ];
    }

    private static void FinalizeHeapdumpSection(List<string> foundCategories, string? category, StringBuilder content)
    {
        if (category is null || content.Length == 0)
            return;
        var body = content.ToString();
        if (body.Contains("not found!", StringComparison.OrdinalIgnoreCase) && body.Length < 40)
            return;
        foundCategories.Add(category);
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

    [GeneratedRegex(@"^\[\d{3}\]\s+\S+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DsStoreEntryRegex();

    [GeneratedRegex(@"\b(?:GET|POST|PUT|DELETE|OPTIONS|HEAD)\s+https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SwaggerEndpointRegex();

    [GeneratedRegex(@"\[\*\]\s*(\S+)\s+存在漏洞[:：]\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Struts2HitRegex();

    [GeneratedRegex(@"\[\+\]\s*\[([^\]]+)\]\s*weblogic has a .*deserialization vulnerability[:：]\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeblogicHitRegex();

    [GeneratedRegex(@"\b\d{1,5}/(?:tcp|udp)\s+open\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NmapOpenPortRegex();

    [GeneratedRegex(@"^\s*(?:200|201|204|301|302|307|308)\s+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DirsearchHitRegex();
}
