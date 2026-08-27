using Hackermes.Assessment;
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
    }

    [Fact]
    public void Unknown_adapter_does_not_invent_findings_from_clean_output()
    {
        Assert.Empty(ReconObservationParser.Parse("custom.adapter", "all good"));
    }
}
