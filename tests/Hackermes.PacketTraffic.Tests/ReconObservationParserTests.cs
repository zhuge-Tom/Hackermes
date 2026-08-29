using Hackermes.Assessment;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class ReconObservationParserTests
{
    [Fact]
    public void Nmap_counts_open_ports_without_echoing_banners()
    {
        var observations = ReconObservationParser.Parse(
            AuthorizedToolCatalog.NmapQuick,
            "Nmap scan report\n22/tcp open ssh\n80/tcp open http\n443/tcp closed https\n");

        var observation = Assert.Single(observations);
        Assert.Equal("open-ports", observation.Code);
        Assert.Equal("Info", observation.Severity);
        Assert.Contains("2", observation.Message);
        Assert.DoesNotContain("ssh", observation.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Http_headers_emit_missing_security_codes()
    {
        var observations = ReconObservationParser.Parse(
            AuthorizedToolCatalog.HttpGetProbe,
            "HTTP/1.1 200 OK\nContent-Type: text/html\n\n<body>secret</body>");

        var codes = observations.Select(item => item.Code).ToArray();
        Assert.Contains("missing-hsts", codes);
        Assert.Contains("missing-csp", codes);
        Assert.DoesNotContain(observations, item => item.Message.Contains("secret", System.StringComparison.Ordinal));
        var allowed = new[] { "Critical", "High", "Medium", "Low", "Info" };
        Assert.All(observations, value => Assert.True(allowed.Contains(value.Severity), $"Unexpected severity: {value.Severity}"));
    }

    [Fact]
    public void Https_probe_to_cleartext_redirect_is_auto_confirmed_with_PoC()
    {
        var observations = ReconObservationParser.Parse(
            AuthorizedToolCatalog.HttpGetProbe,
            "HTTP/1.1 302 Found\nLocation: http://secure.test/index.html\nContent-Type: text/html\n\n<html>jump</html>",
            """{"target":"secure.test","scheme":"https","port":443,"path":"/"}""");

        var downgrade = Assert.Single(observations, value => value.Code == "https-downgrade-cleartext");
        Assert.Equal("Medium", downgrade.Severity);
        Assert.NotNull(downgrade.PoC);
        Assert.Contains("curl", downgrade.PoC, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://", downgrade.PoC, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Unauthorized_scanner_vulnerable_line_confirms_high_with_poc()
    {
        var output = "[*] Starting scan for 1 targets\n[+] Scan completed: http://127.0.0.1:18080\n" +
                     "  - swagger: Vulnerable (Swagger unauthorized access)\n  - redis: Secure (Redis unauthorized access not found)\n[+] Scan completed";
        var observations = ReconObservationParser.Parse(
            AuthorizedToolCatalog.ProbeUnauthorizedAccess, output,
            """{"target":"127.0.0.1","scheme":"http","port":18080}""");

        var hit = Assert.Single(observations, value => value.Code == "unauthorized-access");
        Assert.Equal("High", hit.Severity);
        Assert.NotNull(hit.PoC);
        Assert.Contains("swagger", hit.PoC, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://127.0.0.1", hit.PoC, StringComparison.Ordinal);
    }

    [Fact]
    public void Unauthorized_scanner_clean_run_does_not_emit_a_finding()
    {
        var output = "[+] Scan completed: http://127.0.0.1:18080\n  - redis: Secure (not found)\n  - swagger: Secure (not found)\n[+] Scan completed";
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ProbeUnauthorizedAccess, output));
    }

    [Fact]
    public void Sqlmap_vulnerable_output_confirms_high_with_poc()
    {
        var output = "[INFO] GET parameter 'id' is vulnerable. Do you want to continue?\n[INFO] target appears to be MySQL";
        var observations = ReconObservationParser.Parse(
            AuthorizedToolCatalog.ProbeSqlmapInject, output,
            """{"target":"127.0.0.1","scheme":"http","port":8080,"path":"/q","parameter":"id","value":"1"}""");

        var hit = Assert.Single(observations, value => value.Code == "sqli-confirmed");
        Assert.Equal("High", hit.Severity);
        Assert.NotNull(hit.PoC);
        Assert.Contains("sqlmap", hit.PoC, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sqlmap_negative_output_is_suppressed()
    {
        var output = "[WARNING] does not seem to be injectable\n[CRITICAL] all tested parameters do not appear to be injectable";
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ProbeSqlmapInject, output));
    }

    [Fact]
    public void Param_corpus_candidate_lines_produce_medium_candidate_observations()
    {
        var output = "CANDIDATE param=id payload=' OR 1=1 -- status=500 len=44 baseline=200/12 err=-\nCANDIDATE param=id payload=<img src=x> status=200 len=15 baseline=200/12 err=-\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ProbeParamCorpus, output,
            """{"target":"127.0.0.1","scheme":"http","port":8080,"path":"/q","parameter":"id"}""");

        var hits = observations.Where(value => value.Code.StartsWith("injection-candidate-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, hits.Length);
        Assert.All(hits, value => Assert.Equal("Medium", value.Severity));
        Assert.NotNull(hits[0].PoC);
        Assert.Contains("candidate", hits[0].PoC, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT a confirmed", hits[0].PoC, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Param_corpus_no_candidates_is_empty()
    {
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ProbeParamCorpus, "all baseline-equal\n"));
    }

    [Fact]
    public void Subdomain_enumeration_resolved_hosts_produce_an_info_observation()
    {
        var output = "www.example.com -> 1.2.3.4\napi.example.com -> 5.6.7.8\nnotfound.example.com\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconSubdomainEnum, output);

        var hit = Assert.Single(observations, value => value.Code == "subdomains-resolved");
        Assert.Equal("Info", hit.Severity);
        Assert.Contains("www.example.com", hit.PoC, StringComparison.Ordinal);
    }

    [Fact]
    public void Subdomain_enumeration_no_resolutions_is_empty()
    {
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ReconSubdomainEnum, "all timed out\n"));
    }

    [Fact]
    public void Corpus_resources_list_only_existing_files()
    {
        var corpus = Path.Combine(Path.GetTempPath(), "hackermes-corpus-" + Guid.NewGuid().ToString("N"), "resources", "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "subdomains.txt"), "www\n");
        File.WriteAllText(Path.Combine(corpus, "sqli-auth-bypass.txt"), "' OR 1=1 --\n");
        var oldCorpus = Environment.GetEnvironmentVariable("HACKERMES_CORPUS_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_CORPUS_ROOT", corpus);

            var resources = AuthorizedToolCatalog.CorpusResources();
            var ids = resources.Select(value => value.Id).ToArray();
            Assert.Contains("subdomains", ids);
            Assert.Contains("sqli-auth-bypass", ids);
            Assert.DoesNotContain("command-injection", ids);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_CORPUS_ROOT", oldCorpus);
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(corpus))!, recursive: true);
        }
    }

    [Fact]
    public void Cleartext_probe_to_http_is_not_mistaken_for_a_downgrade()
    {
        var observations = ReconObservationParser.Parse(
            AuthorizedToolCatalog.HttpGetProbe,
            "HTTP/1.1 301 Moved Permanently\nLocation: http://example.test/other\n\n",
            """{"target":"example.test","scheme":"http","port":80,"path":"/"}""");

        Assert.DoesNotContain(observations, value => value.Code == "https-downgrade-cleartext");
    }

    [Fact]
    public void Unknown_adapter_does_not_invent_findings_from_clean_output()
    {
        Assert.Empty(ReconObservationParser.Parse("custom.adapter", "all good"));
    }
}
