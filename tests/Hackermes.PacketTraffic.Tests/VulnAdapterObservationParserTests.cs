using Hackermes.Assessment;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class VulnAdapterObservationParserTests
{
    [Fact]
    public void WeblogicT3HitsBecomeHighObservationsWithCve()
    {
        const string output = "[*] ========Task Num: [2]========\n" +
            "[-] [t3://127.0.0.1:7001] weblogic not detected CVE-2017-10271\n" +
            "[+] [t3://127.0.0.1:7001] weblogic has a JAVA deserialization vulnerability:CVE-2017-10271\n" +
            "[+] [t3://127.0.0.1:7001] weblogic has a JAVA deserialization vulnerability:CVE-2019-2725\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectWeblogicT3Scan, output);
        Assert.Equal(2, observations.Count);
        Assert.All(observations, item => Assert.Equal("High", item.Severity));
        Assert.Contains(observations, item => item.Code.Contains("CVE-2017-10271"));
        Assert.Contains(observations, item => item.Code.Contains("CVE-2019-2725"));
        Assert.All(observations, item => Assert.NotNull(item.PoC));
    }

    [Fact]
    public void GitLeakCloneSuccessBecomesMediumObservation()
    {
        const string output = "[*] 2026-08-28 10:00:00,000 - Initialize Target\n" +
            "[*] Clone Success. Dist File : dst/127.0.0.1\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconGitLeakScan, output,
            "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80}");
        var observation = Assert.Single(observations);
        Assert.Equal("git-repo-disclosure", observation.Code);
        Assert.Equal("Medium", observation.Severity);
        Assert.Contains("/.git/", observation.PoC);
    }

    [Fact]
    public void SvnLeakTableRowsBecomeMediumObservation()
    {
        const string output = "[+] wc.db connected\n" +
            "+-----------+----------+----------------------------------+\n" +
            "|  文件名   | 文件类型  |             CheckSum            |\n" +
            "+-----------+----------+----------------------------------+\n" +
            "|  app.py   |   file   | 4$2d9c1e0f9ab2                   |\n" +
            "|  conf/    |   dir    |                                  |\n" +
            "+-----------+----------+----------------------------------+\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconSvnLeakScan, output);
        var observation = Assert.Single(observations);
        Assert.Equal("svn-repo-disclosure", observation.Code);
        Assert.Equal("Medium", observation.Severity);
    }

    [Fact]
    public void SvnLeakFailureProducesNoObservation()
    {
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconSvnLeakScan,
            "[-] 未找到 http://127.0.0.1/.svn/wc.db\n");
        Assert.Empty(observations);
    }

    [Fact]
    public void DsStoreEntriesBecomeLowObservation()
    {
        const string output = "[200] http://127.0.0.1/backup/\n[200] http://127.0.0.1/admin/\n[404] http://127.0.0.1/old/\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconDsStoreScan, output);
        var observation = Assert.Single(observations);
        Assert.Equal("ds-store-disclosure", observation.Code);
        Assert.Equal("Low", observation.Severity);
        Assert.Equal(3, ParseEntryCount(observation.Message));
    }

    [Fact]
    public void SwaggerEndpointsBecomeMediumObservation()
    {
        const string output = "[INFO] GET http://127.0.0.1/api/users 200\n[INFO] POST http://127.0.0.1/api/login 500\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconSwaggerApiEnum, output);
        var observation = Assert.Single(observations);
        Assert.Equal("swagger-api-exposure", observation.Code);
        Assert.Equal("Medium", observation.Severity);
    }

    [Fact]
    public void FastjsonVerdictRequiresExplicitMarker()
    {
        // Stage-3 calibration: JsonExp's normal output is "[+] 序号：N" delivery lines with
        // no verdict; a verdict only appears when the tool itself prints one.
        var deliveryOnly = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectFastjsonJndiScan,
            "[+] 单个URL检测中......\n[+] 序号：1\n{\"b\":\"x\"}\n[!] 连接http://x失败\n");
        Assert.Empty(deliveryOnly);

        var confirmed = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectFastjsonJndiScan,
            "[+] 存在漏洞: JdbcRowSetImpl5\n");
        var confirmedObservation = Assert.Single(confirmed);
        Assert.Equal("fastjson-jndi-confirmed", confirmedObservation.Code);
        Assert.Equal("High", confirmedObservation.Severity);
    }

    [Fact]
    public void HeapdumpSectionsRequireContentAndExcludeNotFound()
    {
        // Real JDumpSpider section layout captured during stage-3 calibration.
        const string output = "===========================================\n" +
            "HikariDataSource\n-------------\n" +
            "com.zaxxer.hikari.HikariDataSource:\n" +
            "[password = RangePass123, jdbcUrl = jdbc:mysql://root:SuperSecretPass@10.0.0.8:3306/app, username = rangeuser]\n\n" +
            "===========================================\n" +
            "CookieRememberMeManager(ShiroKey)\n-------------\n" +
            "not found!\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ExploitHeapdumpAnalyze, output);
        var observation = Assert.Single(observations);
        Assert.Equal("heapdump-sensitive-data", observation.Code);
        Assert.Equal("High", observation.Severity);
        Assert.Contains("HikariDataSource", observation.Message);
        Assert.DoesNotContain("ShiroKey", observation.Message);
    }

    [Fact]
    public void HeapdumpAllNotFoundProducesNoObservation()
    {
        const string output = "===========================================\n" +
            "SpringDataSourceProperties\n-------------\n" +
            "not found!\n" +
            "===========================================\n" +
            "MongoClient\n-------------\n" +
            "not found!\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ExploitHeapdumpAnalyze, output);
        Assert.Empty(observations);
    }

    [Fact]
    public void VcenterPositiveOutputIsMediumCandidateNotHigh()
    {
        // Stage-3 calibration: VcenterKiller prints "[+] Upload success" for any 2xx
        // endpoint (a plain JSON mock produced it), so the observation stays a candidate.
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.ExploitVcenterVerify,
            "[*] url: https://127.0.0.1/\n[+] Upload success, try command execute.\n");
        var observation = Assert.Single(observations);
        Assert.Equal("vcenter-verification-candidate", observation.Code);
        Assert.Equal("Medium", observation.Severity);
    }

    [Fact]
    public void OaPocHitsCarryPocSeverityAndEndpoint()
    {
        const string output = "[MISS] Other Check\n" +
            "[HIT] Tongda User Session Disclosure | high | http://127.0.0.1:80/general/userinfo.php?UID=1\n" +
            "[SUMMARY] module=tongda probed=2 hits=1 errors=0\n";
        var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectOaPocProbe, output);
        var observation = Assert.Single(observations);
        Assert.Equal("oa-poc-Tongda User Session Disclosure", observation.Code);
        Assert.Equal("High", observation.Severity);
        Assert.Contains("userinfo.php", observation.PoC, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOrNegativeOutputsProduceNoObservations()
    {
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ReconGitLeakScan, ""));
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.DetectWeblogicT3Scan,
            "[-] [t3://127.0.0.1:7001] weblogic not detected CVE-2017-10271"));
        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ExploitHeapdumpAnalyze,
            "no sections here"));
    }

    private static int ParseEntryCount(string message)
    {
        var start = message.IndexOf("enumerated ", System.StringComparison.Ordinal) + "enumerated ".Length;
        var end = message.IndexOf(" directory", System.StringComparison.Ordinal);
        return int.Parse(message[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }
}
