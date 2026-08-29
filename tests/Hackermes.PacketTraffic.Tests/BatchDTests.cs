using Hackermes.App;
using Hackermes.Assessment;
using Hackermes.Base.Diagnostics;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BatchDTests
{
    [Fact]
    public void JndiListenerRecordsConnectionsAndStops()
    {
        var service = new JndiListenerService(new BaseDiagnosticsNullLogger());
        var state = service.Start(1);
        Assert.Equal("127.0.0.1", state.Host);
        Assert.True(state.Port > 0);

        using (var client = new TcpClient())
        {
            client.Connect(state.Host, state.Port);
            client.Client.Send(Encoding.ASCII.GetBytes("hello-listener"));
        }
        // The accept loop records the hit asynchronously; poll briefly.
        var snapshot = PollForHit(service, state.Token);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Active);
        var hit = Assert.Single(snapshot.Hits);
        Assert.Contains("127.0.0.1", hit.RemoteEndpoint);

        Assert.True(service.Stop(state.Token));
        Assert.False(service.Stop(state.Token));
        // Stopped listeners are removed entirely: reads return null afterwards.
        Assert.Null(service.Read(state.Token));
    }

    [Fact]
    public void JndiListenerUnknownTokenReturnsNull()
    {
        var service = new JndiListenerService(new BaseDiagnosticsNullLogger());
        Assert.Null(service.Read("missing-token"));
        Assert.False(service.Stop("missing-token"));
    }

    [Fact]
    public void Catalog_FastjsonPayloadIsGatedLikeExploitation()
    {
        Assert.True(AuthorizedToolCatalog.IsExploitationStage(AuthorizedToolCatalog.ExploitFastjsonPayload));
        Assert.True(AuthorizedToolCatalog.IsDetectionStage(AuthorizedToolCatalog.DetectShiroScan));
        Assert.True(AuthorizedToolCatalog.IsDetectionStage(AuthorizedToolCatalog.DetectStruts2Scan));
        Assert.True(AuthorizedToolCatalog.IsDetectionStage(AuthorizedToolCatalog.DetectNacosScan));
    }

    [Fact]
    public void Catalog_BuildsShiroStruts2NacosAndPayloadInvocations()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-batchd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var java = Path.Combine(root, "java.exe");
        var shiroJar = Path.Combine(root, "shiro_tool.jar");
        var struts2 = Path.Combine(root, "Struts2Scan.py");
        var nacos = Path.Combine(root, "nacos_probe.py");
        var payloadJar = Path.Combine(root, "FastjsonExploit-0.1-beta2-all.jar");
        foreach (var file in new[] { python, java, shiroJar, struts2, nacos, payloadJar })
            File.WriteAllText(file, string.Empty);
        var old = CaptureEnv();
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            Environment.SetEnvironmentVariable("HACKERMES_JAVA_PATH", java);
            Environment.SetEnvironmentVariable("HACKERMES_SHIRO_TOOL_PATH", shiroJar);
            Environment.SetEnvironmentVariable("HACKERMES_STRUTS2_SCAN_PATH", struts2);
            Environment.SetEnvironmentVariable("HACKERMES_NACOS_PROBE_PATH", nacos);
            Environment.SetEnvironmentVariable("HACKERMES_FASTJSON_PAYLOAD_JAR", payloadJar);

            var shiro = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectShiroScan,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":443}"), ["127.0.0.1"]);
            Assert.Equal(java, shiro.ExecutablePath);
            Assert.Equal(["-Dfile.encoding=UTF-8", "-jar", shiroJar, "https://127.0.0.1:443/"], shiro.Arguments);

            var struts2Invocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectStruts2Scan,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":8080}"), ["127.0.0.1"]);
            Assert.Equal([struts2, "-u", "http://127.0.0.1:8080/", "-q", "--timeout", "10"], struts2Invocation.Arguments);

            var nacosInvocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectNacosScan,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":8848}"), ["127.0.0.1"]);
            Assert.Equal([nacos, "--target", "http://127.0.0.1:8848", "--timeout", "8"], nacosInvocation.Arguments);

            var payload = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ExploitFastjsonPayload,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80,\"payload\":\"TemplatesImpl1\",\"command\":\"whoami\"}"),
                ["127.0.0.1"]);
            Assert.Equal(java, payload.ExecutablePath);
            Assert.Equal(["--add-opens", "java.xml/com.sun.org.apache.xalan.internal.xsltc.runtime=ALL-UNNAMED",
                "-jar", payloadJar, "TemplatesImpl1", "cmd:whoami"], payload.Arguments);

            foreach (var badInput in new[]
                     {
                         "{\"target\":\"127.0.0.1\",\"payload\":\"JdbcRowSetImpl1\",\"command\":\"whoami\"}",
                         "{\"target\":\"127.0.0.1\",\"payload\":\"TemplatesImpl1\"}",
                         "{\"target\":\"127.0.0.1\",\"payload\":\"TemplatesImpl1\",\"command\":\"whoami\\nx\"}"
                     })
                Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.ExploitFastjsonPayload, badInput), ["127.0.0.1"]));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectShiroScan, "{\"target\":\"example.com\"}"),
                ["127.0.0.1"]));
        }
        finally
        {
            RestoreEnv(old);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParserHandlesShiroStruts2AndNacosOutputs()
    {
        var shiroMiss = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectShiroScan,
            "[-] target: http://127.0.0.1/\n[-] target may not use shiro\n");
        Assert.Empty(shiroMiss);

        var shiroKeyFail = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectShiroScan,
            "[+] http://127.0.0.1/ is use shiro\n[-] get shiro key fail, please enter a shiro key\n");
        var shiroFail = Assert.Single(shiroKeyFail);
        Assert.Equal("shiro-framework-detected", shiroFail.Code);
        Assert.Equal("Medium", shiroFail.Severity);

        var shiroCracked = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectShiroScan,
            "[+] http://127.0.0.1/ is use shiro\n[*] start gadget menu\n");
        var shiroHit = Assert.Single(shiroCracked);
        Assert.Equal("shiro-key-confirmed", shiroHit.Code);
        Assert.Equal("High", shiroHit.Severity);

        var struts2 = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectStruts2Scan,
            "[*] ----------------results------------------\n[*] http://127.0.0.1:8080/ 存在漏洞: S2-045\n");
        var struts2Hit = Assert.Single(struts2);
        Assert.Equal("struts2-S2-045", struts2Hit.Code);
        Assert.Equal("High", struts2Hit.Severity);

        var nacos = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectNacosScan,
            "[NACOS-HIT] user-list-unauth | high | http://127.0.0.1:8848/nacos/v1/auth/users | status 200\n[NACOS-MISS] console-exposed\n");
        var nacosHit = Assert.Single(nacos);
        Assert.Equal("nacos-user-list-unauth", nacosHit.Code);
        Assert.Equal("High", nacosHit.Severity);
    }

    private static JndiListenerSnapshot? PollForHit(JndiListenerService service, string token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var snapshot = service.Read(token);
            if (snapshot is { Hits.Count: > 0 }) return snapshot;
            Thread.Sleep(100);
        }
        return service.Read(token);
    }

    private static (string?, string?, string?, string?, string?, string?) CaptureEnv() => (
        Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH"),
        Environment.GetEnvironmentVariable("HACKERMES_JAVA_PATH"),
        Environment.GetEnvironmentVariable("HACKERMES_SHIRO_TOOL_PATH"),
        Environment.GetEnvironmentVariable("HACKERMES_STRUTS2_SCAN_PATH"),
        Environment.GetEnvironmentVariable("HACKERMES_NACOS_PROBE_PATH"),
        Environment.GetEnvironmentVariable("HACKERMES_FASTJSON_PAYLOAD_JAR"));

    private static void RestoreEnv((string?, string?, string?, string?, string?, string?) values)
    {
        Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", values.Item1);
        Environment.SetEnvironmentVariable("HACKERMES_JAVA_PATH", values.Item2);
        Environment.SetEnvironmentVariable("HACKERMES_SHIRO_TOOL_PATH", values.Item3);
        Environment.SetEnvironmentVariable("HACKERMES_STRUTS2_SCAN_PATH", values.Item4);
        Environment.SetEnvironmentVariable("HACKERMES_NACOS_PROBE_PATH", values.Item5);
        Environment.SetEnvironmentVariable("HACKERMES_FASTJSON_PAYLOAD_JAR", values.Item6);
    }

    private sealed class BaseDiagnosticsNullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
