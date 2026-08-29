using Hackermes.Assessment;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

[Collection("ToolHost serial")]
public sealed class AuthorizedVulnAdaptersTests
{
    [Fact]
    public void Catalog_BuildsLeakReconInvocationsAndValidatesScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-leakrecon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var gitHack = Path.Combine(root, "GitHack.py");
        var svnExploit = Path.Combine(root, "SvnExploit.py");
        var dsStore = Path.Combine(root, "ds_store_exp.py");
        var swagger = Path.Combine(root, "swagger-hack2.0.py");
        foreach (var file in new[] { python, gitHack, svnExploit, dsStore, swagger })
            File.WriteAllText(file, string.Empty);
        var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
        var oldGit = Environment.GetEnvironmentVariable("HACKERMES_GITHACK_PATH");
        var oldSvn = Environment.GetEnvironmentVariable("HACKERMES_SVN_EXPLOIT_PATH");
        var oldDs = Environment.GetEnvironmentVariable("HACKERMES_DS_STORE_EXP_PATH");
        var oldSwagger = Environment.GetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            Environment.SetEnvironmentVariable("HACKERMES_GITHACK_PATH", gitHack);
            Environment.SetEnvironmentVariable("HACKERMES_SVN_EXPLOIT_PATH", svnExploit);
            Environment.SetEnvironmentVariable("HACKERMES_DS_STORE_EXP_PATH", dsStore);
            Environment.SetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH", swagger);

            var git = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ReconGitLeakScan,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":8443}", 180), ["127.0.0.1"]);
            Assert.Equal(python, git.ExecutablePath);
            Assert.Equal([gitHack, "https://127.0.0.1:8443/.git/"], git.Arguments);

            var svn = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ReconSvnLeakScan, "{\"target\":\"127.0.0.1\"}"), ["127.0.0.1"]);
            Assert.Equal([svnExploit, "-u", "http://127.0.0.1:80/.svn/", "--thread", "5"], svn.Arguments);

            var ds = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ReconDsStoreScan, "{\"target\":\"127.0.0.1\",\"port\":8080}"), ["127.0.0.1"]);
            Assert.Equal([dsStore, "http://127.0.0.1:8080/.DS_Store"], ds.Arguments);

            var swaggerInvocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ReconSwaggerApiEnum, "{\"target\":\"127.0.0.1\"}"), ["127.0.0.1"]);
            Assert.Equal([swagger, "-u", "http://127.0.0.1:80/"], swaggerInvocation.Arguments);

            // Swagger normalization carries the optional api-docs path; the other three
            // leak recon adapters keep the shared canonical web-endpoint shape.
            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80,\"path\":\"/\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.ReconSwaggerApiEnum, "{\"target\":\"127.0.0.1\"}"),
                    ["127.0.0.1"]).Input);
            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.ReconDsStoreScan, "{\"target\":\"127.0.0.1\"}"),
                    ["127.0.0.1"]).Input);

            foreach (var adapterId in new[]
                     {
                         AuthorizedToolCatalog.ReconGitLeakScan, AuthorizedToolCatalog.ReconSvnLeakScan,
                         AuthorizedToolCatalog.ReconDsStoreScan, AuthorizedToolCatalog.ReconSwaggerApiEnum
                     })
                Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(adapterId, "{\"target\":\"example.com\"}"), ["127.0.0.1"]));

            var tools = AuthorizedToolCatalog.Describe();
            foreach (var adapterId in new[]
                     {
                         AuthorizedToolCatalog.ReconGitLeakScan, AuthorizedToolCatalog.ReconSvnLeakScan,
                         AuthorizedToolCatalog.ReconDsStoreScan, AuthorizedToolCatalog.ReconSwaggerApiEnum
                     })
                Assert.True(tools.Single(tool => tool.Id == adapterId).Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
            Environment.SetEnvironmentVariable("HACKERMES_GITHACK_PATH", oldGit);
            Environment.SetEnvironmentVariable("HACKERMES_SVN_EXPLOIT_PATH", oldSvn);
            Environment.SetEnvironmentVariable("HACKERMES_DS_STORE_EXP_PATH", oldDs);
            Environment.SetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH", oldSwagger);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_BuildsWeblogicAndFastjsonInvocationsAndValidatesInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-mwprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var weblogic = Path.Combine(root, "WeblogicScan.py");
        var jsonExp = Path.Combine(root, "JsonExp.exe");
        var jsonTemplate = Path.Combine(root, "template", "fastjson.txt");
        File.WriteAllText(python, string.Empty);
        File.WriteAllText(weblogic, string.Empty);
        File.WriteAllText(jsonExp, string.Empty);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonTemplate)!);
        File.WriteAllText(jsonTemplate, "{\"a\":\"b\"}\n");
        var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
        var oldWeblogic = Environment.GetEnvironmentVariable("HACKERMES_WEBLOGIC_SCAN_PATH");
        var oldFastjson = Environment.GetEnvironmentVariable("HACKERMES_FASTJSON_JNDI_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            Environment.SetEnvironmentVariable("HACKERMES_WEBLOGIC_SCAN_PATH", weblogic);
            Environment.SetEnvironmentVariable("HACKERMES_FASTJSON_JNDI_PATH", jsonExp);

            var t3 = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectWeblogicT3Scan, "{\"target\":\"127.0.0.1\"}"), ["127.0.0.1"]);
            Assert.Equal(python, t3.ExecutablePath);
            Assert.Equal([weblogic, "-u", "127.0.0.1", "-p", "7001"], t3.Arguments);
            Assert.Equal("{\"target\":\"127.0.0.1\",\"port\":7001}", AuthorizedToolCatalog.NormalizeStep(
                new AssessmentStep(AuthorizedToolCatalog.DetectWeblogicT3Scan, "{\"target\":\"127.0.0.1\"}"),
                ["127.0.0.1"]).Input);
            Assert.Equal("{\"target\":\"127.0.0.1\",\"port\":8443}", AuthorizedToolCatalog.NormalizeStep(
                new AssessmentStep(AuthorizedToolCatalog.DetectWeblogicT3Scan, "{\"target\":\"127.0.0.1\",\"port\":8443}"),
                ["127.0.0.1"]).Input);
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectWeblogicT3Scan, "{\"target\":\"example.com\"}"),
                ["127.0.0.1"]));

            var fastjson = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectFastjsonJndiScan,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":8080,\"ldap\":\"127.0.0.1:1389/Exp\",\"rmi\":\"127.0.0.1:1099\",\"method\":\"get\"}"),
                ["127.0.0.1"]);
            Assert.Equal(jsonExp, fastjson.ExecutablePath);
            Assert.Equal([jsonExp, "-u", "http://127.0.0.1:8080/", "-to", "15", "-f", jsonTemplate,
                "-l", "127.0.0.1:1389/Exp", "-r", "127.0.0.1:1099", "-t", "get"], fastjson.Arguments);
            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":8080,\"ldap\":\"127.0.0.1:1389/Exp\",\"rmi\":\"127.0.0.1:1099\",\"method\":\"get\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.DetectFastjsonJndiScan,
                        "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":8080,\"ldap\":\"127.0.0.1:1389/Exp\",\"rmi\":\"127.0.0.1:1099\",\"method\":\"get\"}"),
                    ["127.0.0.1"]).Input);

            // Callback addresses stay bounded listener addresses: no scheme, no whitespace, valid port.
            foreach (var badCallback in new[] { "ldap://127.0.0.1:1389", "127.0.0.1 1389", "127.0.0.1:http", "host:99999" })
                Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.DetectFastjsonJndiScan,
                        $"{{\"target\":\"127.0.0.1\",\"ldap\":\"{badCallback}\"}}"), ["127.0.0.1"]));
            // Stage-3 calibration: JsonExp refuses to run without -l/-r/--dnslog, so the
            // adapter requires an operator-provided callback before launching the tool.
            Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectFastjsonJndiScan,
                    "{\"target\":\"127.0.0.1\"}"), ["127.0.0.1"]));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectFastjsonJndiScan,
                    "{\"target\":\"example.com\"}"), ["127.0.0.1"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
            Environment.SetEnvironmentVariable("HACKERMES_WEBLOGIC_SCAN_PATH", oldWeblogic);
            Environment.SetEnvironmentVariable("HACKERMES_FASTJSON_JNDI_PATH", oldFastjson);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_BuildsVcenterInvocationAndValidatesExploitInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-vcenter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var killer = Path.Combine(root, "main.exe");
        var shell = Path.Combine(root, "shell-verify.jsp");
        File.WriteAllText(killer, string.Empty);
        File.WriteAllText(shell, string.Empty);
        var oldKiller = Environment.GetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH");
        var oldShell = Environment.GetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH", killer);
            Environment.SetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH", shell);

            var verify = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ExploitVcenterVerify,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":443,\"mode\":\"21985\",\"command\":\"whoami\"}"),
                ["127.0.0.1"]);
            Assert.Equal(killer, verify.ExecutablePath);
            Assert.Equal([killer, "-u", "https://127.0.0.1:443/", "-m", "21985", "-c", "whoami"], verify.Arguments);

            var upload = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ExploitVcenterVerify,
                    "{\"target\":\"127.0.0.1\",\"mode\":\"21972\",\"action\":\"upload\"}"), ["127.0.0.1"]);
            Assert.Equal([killer, "-u", "http://127.0.0.1:80/", "-m", "21972", "-f", shell], upload.Arguments);

            var scan = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ExploitVcenterVerify,
                    "{\"target\":\"127.0.0.1\",\"mode\":\"log4center\",\"action\":\"scan\",\"remote\":\"ldap://127.0.0.1:1389\"}"),
                ["127.0.0.1"]);
            Assert.Equal([killer, "-u", "http://127.0.0.1:80/", "-m", "log4center", "-t", "scan", "-r", "ldap://127.0.0.1:1389"],
                scan.Arguments);

            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80,\"mode\":\"21985\",\"command\":\"whoami\",\"action\":\"\",\"remote\":\"\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.ExploitVcenterVerify,
                        "{\"target\":\"127.0.0.1\",\"mode\":\"21985\",\"command\":\"whoami\"}"), ["127.0.0.1"]).Input);

            foreach (var badInput in new[]
                     {
                         "{\"target\":\"127.0.0.1\",\"mode\":\"9999\"}",
                         "{\"target\":\"127.0.0.1\",\"mode\":\"21985\",\"action\":\"rshell\"}",
                         "{\"target\":\"127.0.0.1\",\"mode\":\"21985\",\"command\":\"whoami\\nwhoami2\"}",
                         "{\"target\":\"127.0.0.1\",\"mode\":\"21985\",\"remote\":\"http://127.0.0.1:1389\"}",
                         "{\"target\":\"127.0.0.1\"}"
                     })
                Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.ExploitVcenterVerify, badInput), ["127.0.0.1"]));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ExploitVcenterVerify,
                    "{\"target\":\"example.com\",\"mode\":\"21985\"}"), ["127.0.0.1"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH", oldKiller);
            Environment.SetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH", oldShell);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_HeapdumpResolvesArtifactsStrictlyInsideStoreRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-heapdump-" + Guid.NewGuid().ToString("N"));
        var store = Path.Combine(root, "store");
        Directory.CreateDirectory(store);
        var java = Path.Combine(root, "java.exe");
        var jar = Path.Combine(root, "JDumpSpider.jar");
        var artifact = Path.Combine(store, "heap.bin");
        File.WriteAllText(java, string.Empty);
        File.WriteAllText(jar, string.Empty);
        File.WriteAllText(artifact, string.Empty);
        var oldJava = Environment.GetEnvironmentVariable("HACKERMES_JAVA_PATH");
        var oldJar = Environment.GetEnvironmentVariable("HACKERMES_HEAPDUMP_SPIDER_PATH");
        var oldStore = Environment.GetEnvironmentVariable("HACKERMES_AGENT_ARTIFACT_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_JAVA_PATH", java);
            Environment.SetEnvironmentVariable("HACKERMES_HEAPDUMP_SPIDER_PATH", jar);
            Environment.SetEnvironmentVariable("HACKERMES_AGENT_ARTIFACT_ROOT", store);

            var invocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ExploitHeapdumpAnalyze, "{\"file\":\"heap.bin\"}", 120),
                ["127.0.0.1"]);
            Assert.Equal(java, invocation.ExecutablePath);
            Assert.Equal(["-Dfile.encoding=UTF-8", "-jar", jar, artifact], invocation.Arguments);
            Assert.Equal("{\"file\":\"heap.bin\"}", AuthorizedToolCatalog.NormalizeStep(
                new AssessmentStep(AuthorizedToolCatalog.ExploitHeapdumpAnalyze, "{\"file\":\"heap.bin\"}"),
                ["127.0.0.1"]).Input);
            Assert.True(AuthorizedToolCatalog.Describe().Single(tool => tool.Id == AuthorizedToolCatalog.ExploitHeapdumpAnalyze).Available);

            // Path traversal, nested paths and unknown artifacts are rejected outright.
            foreach (var badFile in new[] { "..\\evil.bin", "sub/heap.bin", ".\\heap.bin", "missing.bin" })
                Assert.ThrowsAny<Exception>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.ExploitHeapdumpAnalyze, $"{{\"file\":\"{badFile}\"}}"),
                    ["127.0.0.1"]));

            // Without a Java runtime the adapter reports unavailable instead of failing at run time.
            Environment.SetEnvironmentVariable("HACKERMES_JAVA_PATH", Path.Combine(root, "missing-java.exe"));
            var descriptor = AuthorizedToolCatalog.Describe().Single(tool => tool.Id == AuthorizedToolCatalog.ExploitHeapdumpAnalyze);
            Assert.False(descriptor.Available);
            Assert.Contains("Java", descriptor.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_JAVA_PATH", oldJava);
            Environment.SetEnvironmentVariable("HACKERMES_HEAPDUMP_SPIDER_PATH", oldJar);
            Environment.SetEnvironmentVariable("HACKERMES_AGENT_ARTIFACT_ROOT", oldStore);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_BuildsOaPocInvocationsAndValidatesModule()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-oapoc-" + Guid.NewGuid().ToString("N"));
        var runnerDir = Path.Combine(root, "detect.oa-poc.terminal");
        Directory.CreateDirectory(Path.Combine(runnerDir, "book", "tongda"));
        var python = Path.Combine(root, "python.exe");
        var runner = Path.Combine(runnerDir, "oa_poc_runner.py");
        var poc = Path.Combine(runnerDir, "book", "tongda", "tongda-demo.yaml");
        File.WriteAllText(python, string.Empty);
        File.WriteAllText(runner, string.Empty);
        File.WriteAllText(poc, "id: demo\n");
        var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
        var oldRunner = Environment.GetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            Environment.SetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH", runner);

            var listing = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectOaPocList, "{}"), ["127.0.0.1"]);
            Assert.Equal(python, listing.ExecutablePath);
            Assert.Equal([runner, "--list"], listing.Arguments);

            var probe = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectOaPocProbe,
                    "{\"target\":\"127.0.0.1\",\"module\":\"tongda\",\"poc\":\"tongda-demo.yaml\"}"),
                ["127.0.0.1"]);
            Assert.Equal(python, probe.ExecutablePath);
            Assert.Equal(runnerDir, probe.WorkingDirectory);
            Assert.Equal([runner, "--target", "http://127.0.0.1:80", "--module", "tongda",
                "--timeout", "10", "--poc", "tongda-demo.yaml"], probe.Arguments);
            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80,\"module\":\"tongda\",\"poc\":\"tongda-demo.yaml\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.DetectOaPocProbe,
                        "{\"target\":\"127.0.0.1\",\"module\":\"tongda\",\"poc\":\"tongda-demo.yaml\"}"),
                    ["127.0.0.1"]).Input);

            foreach (var badInput in new[]
                     {
                         "{\"target\":\"127.0.0.1\",\"module\":\"missing-module\"}",
                         "{\"target\":\"127.0.0.1\",\"module\":\"tongda;rm\"}",
                         "{\"target\":\"127.0.0.1\",\"module\":\"tongda\",\"poc\":\"../other.yaml\"}",
                         "{\"target\":\"127.0.0.1\"}"
                     })
                Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.DetectOaPocProbe, badInput), ["127.0.0.1"]));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectOaPocProbe,
                    "{\"target\":\"example.com\",\"module\":\"tongda\"}"), ["127.0.0.1"]));

            var tools = AuthorizedToolCatalog.Describe();
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.DetectOaPocList).Available);
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.DetectOaPocProbe).Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
            Environment.SetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH", oldRunner);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_NewAdaptersRejectUnknownIdentifier()
    {
        Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.NormalizeStep(
            new AssessmentStep("detect.unknown.vuln", "{\"target\":\"127.0.0.1\"}"), ["127.0.0.1"]));
    }
}
