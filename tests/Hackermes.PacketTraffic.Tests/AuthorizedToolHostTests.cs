using Hackermes.Assessment;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using Hackermes.Platform.Events;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

[CollectionDefinition("ToolHost serial", DisableParallelization = true)]
public sealed class ToolHostSerialCollection;

[Collection("ToolHost serial")]
public sealed class AuthorizedToolHostTests
{
    [Fact]
    public void Nmap_service_is_unavailable_when_its_version_detection_runtime_is_incomplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-nmap-runtime-" + Guid.NewGuid().ToString("N"));
        var nmap = Path.Combine(root, "nmap.exe");
        Directory.CreateDirectory(root);
        File.WriteAllText(nmap, string.Empty);
        var oldNmap = Environment.GetEnvironmentVariable("HACKERMES_NMAP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_NMAP_PATH", nmap);

            var tools = AuthorizedToolCatalog.Describe();

            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.NmapQuick).Available);
            var service = tools.Single(tool => tool.Id == AuthorizedToolCatalog.NmapService);
            Assert.False(service.Available);
            Assert.Contains("nselib", service.UnavailableReason, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<FileNotFoundException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.NmapService,
                    "{\"target\":\"127.0.0.1\",\"ports\":\"80\"}"), ["127.0.0.1"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_NMAP_PATH", oldNmap);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_UsesBundledPythonToolsWithoutExternalDrivePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-toolhost-bundle-" + Guid.NewGuid().ToString("N"));
        var python = Path.Combine(root, "_runtime", "python", "python.exe");
        var dirsearch = Path.Combine(root, "recon.dirsearch.terminal", "dirsearch.py");
        var wordlist = Path.Combine(root, "recon.dirsearch.terminal", "db", "templates", "admin.txt");
        var waf = Path.Combine(root, "detect.wafw00f.terminal", "wafw00f", "main.py");
        foreach (var file in new[] { python, dirsearch, wordlist, waf })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty);
        }
        var oldRoot = Environment.GetEnvironmentVariable("HACKERMES_BUNDLED_TOOLS_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_BUNDLED_TOOLS_ROOT", root);
            var tools = AuthorizedToolCatalog.Describe();

            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.DirsearchQuick).Available);
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.Wafw00fQuick).Available);
            var invocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DirsearchQuick, "{\"target\":\"127.0.0.1\"}"),
                ["127.0.0.1"]);
            Assert.Equal(python, invocation.ExecutablePath);
            Assert.Equal(dirsearch, invocation.Arguments[0]);
            Assert.Equal(wordlist, invocation.Arguments[4]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_BUNDLED_TOOLS_ROOT", oldRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_RejectsTargetOutsideExactScopeAndCommandInjection()
    {
        var outside = new AssessmentStep(AuthorizedToolCatalog.NmapQuick, "{\"target\":\"example.com\",\"ports\":\"80\"}");
        Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(outside, ["127.0.0.1"]));
        var injection = new AssessmentStep(AuthorizedToolCatalog.NmapQuick, "{\"target\":\"127.0.0.1\",\"ports\":\"80 --script vuln\"}");
        Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(injection, ["127.0.0.1"]));
    }

    [Fact]
    public void Catalog_BuildsBoundedWafw00fInvocationAndRejectsInvalidScopeOrScheme()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-wafw00f-test-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "wafw00f");
        var main = Path.Combine(package, "main.py");
        var python = Path.Combine(root, "python.exe");
        Directory.CreateDirectory(package);
        File.WriteAllText(main, string.Empty);
        File.WriteAllText(python, string.Empty);
        var oldWafw00f = Environment.GetEnvironmentVariable("HACKERMES_WAFW00F_PATH");
        var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_WAFW00F_PATH", main);
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            var step = new AssessmentStep(AuthorizedToolCatalog.Wafw00fQuick,
                "{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":8080}", 900, 999_999);

            var invocation = AuthorizedToolCatalog.BuildInvocation(step, ["127.0.0.1"]);

            Assert.Equal(python, invocation.ExecutablePath);
            Assert.Equal(root, invocation.WorkingDirectory);
            Assert.Equal(600, invocation.TimeoutSeconds);
            Assert.Equal(262_144, invocation.MaxOutputBytes);
            Assert.Equal(["-m", "wafw00f.main", "http://127.0.0.1:8080/", "--no-colors", "-T", "600", "-o", "-", "-f", "json"], invocation.Arguments);
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(step, ["localhost"]));
            Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(step with
            {
                Input = "{\"target\":\"127.0.0.1\",\"scheme\":\"http;whoami\",\"port\":8080}"
            }, ["127.0.0.1"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_WAFW00F_PATH", oldWafw00f);
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_BuildsBoundedWebProbeInvocationsAndValidatesScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-webprobe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var curl = Path.Combine(root, "curl.exe");
        var httpx = Path.Combine(root, "httpx.exe");
        File.WriteAllText(curl, string.Empty);
        File.WriteAllText(httpx, string.Empty);
        var oldCurl = Environment.GetEnvironmentVariable("HACKERMES_CURL_PATH");
        var oldHttpx = Environment.GetEnvironmentVariable("HACKERMES_HTTPX_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_CURL_PATH", curl);
            Environment.SetEnvironmentVariable("HACKERMES_HTTPX_PATH", httpx);

            var headers = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.HttpHeadersProbe,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":8443}", 15), ["127.0.0.1"]);
            Assert.Equal(curl, headers.ExecutablePath);
            Assert.Equal(root, headers.WorkingDirectory);
            Assert.Equal(["-sS", "-I", "--connect-timeout", "10", "--max-time", "15", "https://127.0.0.1:8443/"],
                headers.Arguments);

            var probe = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.HttpxProbe, "{\"target\":\"127.0.0.1\"}", 900, 999_999),
                ["127.0.0.1"]);
            Assert.Equal(httpx, probe.ExecutablePath);
            Assert.Equal(600, probe.TimeoutSeconds);
            Assert.Equal(262_144, probe.MaxOutputBytes);
            Assert.Equal(["-u", "http://127.0.0.1:80/", "-status-code", "-no-color", "-threads", "1", "-timeout", "600"],
                probe.Arguments);

            // Plan normalization keeps the shared bounded endpoint shape for both adapters.
            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.HttpHeadersProbe, "{\"target\":\"127.0.0.1\"}"),
                    ["127.0.0.1"]).Input);

            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.HttpHeadersProbe, "{\"target\":\"example.com\"}"),
                ["127.0.0.1"]));
            Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.HttpxProbe,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"gopher\"}"), ["127.0.0.1"]));
            Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.NormalizeStep(
                new AssessmentStep("recon.unknown.adapter", "{\"target\":\"127.0.0.1\"}"), ["127.0.0.1"]));

            var tools = AuthorizedToolCatalog.Describe();
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.HttpHeadersProbe).Available);
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.HttpxProbe).Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_CURL_PATH", oldCurl);
            Environment.SetEnvironmentVariable("HACKERMES_HTTPX_PATH", oldHttpx);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_BuildsBoundedHttpGetAndDnsInvocationsAndValidatesInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-getdns-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var curl = Path.Combine(root, "curl.exe");
        var nslookup = Path.Combine(root, "nslookup.exe");
        File.WriteAllText(curl, string.Empty);
        File.WriteAllText(nslookup, string.Empty);
        var oldCurl = Environment.GetEnvironmentVariable("HACKERMES_CURL_PATH");
        var oldNslookup = Environment.GetEnvironmentVariable("HACKERMES_NSLOOKUP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_CURL_PATH", curl);
            Environment.SetEnvironmentVariable("HACKERMES_NSLOOKUP_PATH", nslookup);

            var get = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe,
                    "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":8443,\"path\":\"/admin\"}", 30), ["127.0.0.1"]);
            Assert.Equal(curl, get.ExecutablePath);
            Assert.Equal(root, get.WorkingDirectory);
            Assert.Equal(["-sS", "-D", "-", "-o", "-", "--connect-timeout", "10", "--max-time", "30",
                "https://127.0.0.1:8443/admin"], get.Arguments);

            var getDefault = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe, "{\"target\":\"127.0.0.1\"}", 900, 999_999),
                ["127.0.0.1"]);
            Assert.Contains("http://127.0.0.1:80/", getDefault.Arguments);
            Assert.Equal(600, getDefault.TimeoutSeconds);
            Assert.Equal(262_144, getDefault.MaxOutputBytes);

            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":8443,\"path\":\"/admin\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe,
                        "{\"target\":\"127.0.0.1\",\"scheme\":\"https\",\"port\":8443,\"path\":\"/admin\"}"),
                    ["127.0.0.1"]).Input);
            Assert.Equal("{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80,\"path\":\"/\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe, "{\"target\":\"127.0.0.1\"}"),
                    ["127.0.0.1"]).Input);

            foreach (var badPath in new[] { "/bad path", "relative", "/" + new string('x', 256) })
                Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe,
                        JsonSerializer.Serialize(new { target = "127.0.0.1", path = badPath })), ["127.0.0.1"]));

            var dns = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DnsResolve, "{\"target\":\"127.0.0.1\"}", 10), ["127.0.0.1"]);
            Assert.Equal(nslookup, dns.ExecutablePath);
            Assert.Equal(root, dns.WorkingDirectory);
            Assert.Equal(["127.0.0.1"], dns.Arguments);
            Assert.Equal("{\"target\":\"localhost\"}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.DnsResolve, "{\"target\":\"LOCALHOST\"}"), ["localhost"]).Input);

            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DnsResolve, "{\"target\":\"example.com\"}"), ["127.0.0.1"]));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.NormalizeStep(
                new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe, "{\"target\":\"example.com\"}"), ["127.0.0.1"]));

            var tools = AuthorizedToolCatalog.Describe();
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.HttpGetProbe).Available);
            Assert.True(tools.Single(tool => tool.Id == AuthorizedToolCatalog.DnsResolve).Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_CURL_PATH", oldCurl);
            Environment.SetEnvironmentVariable("HACKERMES_NSLOOKUP_PATH", oldNslookup);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ToolHost_RunsHeaderProbeAgainstLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = ToolHostPath();
        if (!File.Exists(host) || !AuthorizedToolCatalog.Describe().Single(x => x.Id == AuthorizedToolCatalog.HttpHeadersProbe).Available) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);
        try
        {
            var result = await RunExternalAsync(host, new AssessmentStep(AuthorizedToolCatalog.HttpHeadersProbe,
                $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{port}}}", 20), ["127.0.0.1"]);
            Assert.True(result.Success, result.Error + Environment.NewLine + result.Output);
            Assert.Contains("404 Not Found", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } }
    }

    [Fact]
    public async Task ToolHost_RunsHttpGetProbeAgainstLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = ToolHostPath();
        if (!File.Exists(host) || !AuthorizedToolCatalog.Describe().Single(x => x.Id == AuthorizedToolCatalog.HttpGetProbe).Available) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);
        try
        {
            var result = await RunExternalAsync(host, new AssessmentStep(AuthorizedToolCatalog.HttpGetProbe,
                $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{port},\"path\":\"/status\"}}", 20), ["127.0.0.1"]);
            Assert.True(result.Success, result.Error + Environment.NewLine + result.Output);
            Assert.Contains("404 Not Found", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Connection: close", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } }
    }

    [Fact]
    public async Task ToolHost_RunsBoundedHttpxAgainstLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = ToolHostPath();
        if (!File.Exists(host) || !AuthorizedToolCatalog.Describe().Single(x => x.Id == AuthorizedToolCatalog.HttpxProbe).Available) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);
        try
        {
            var result = await RunExternalAsync(host, new AssessmentStep(AuthorizedToolCatalog.HttpxProbe,
                $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{port}}}", 20), ["127.0.0.1"]);
            Assert.True(result.Success, result.Error + Environment.NewLine + result.Output);
            Assert.Contains("127.0.0.1", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } }
    }

    [Fact]
    public void TicketSigner_DetectsPayloadTampering()
    {
        var signer = new ToolHostTicketSigner(new MemorySecrets());
        var ticket = new ToolHostTicket("nonce", "job", "plan", "approval", "scope", "operator", ["127.0.0.1"],
            new AssessmentStep(AuthorizedToolCatalog.NmapQuick, "{\"target\":\"127.0.0.1\",\"ports\":\"80\"}"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        var envelope = signer.Issue(ticket);
        Assert.Equal("job", signer.Verify(envelope).JobId);
        Assert.Throws<UnauthorizedAccessException>(() => signer.Verify(envelope with { Payload = envelope.Payload.Replace("80", "81", StringComparison.Ordinal) }));
    }

    [Fact]
    public async Task Approval_IsOneTimeAndBoundToFrozenPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-assessment-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plane = new AssessmentControlPlane(new SimulatedAssessmentExecutionHost(), new TestSettings(Path.Combine(root, "settings.json")), new NullLogger());
            var scope = plane.CreateScope("loopback", "test", "tester", ["127.0.0.1"], DateTimeOffset.UtcNow.AddMinutes(5));
            var plan = plane.CreatePlan(scope.Id, "echo", [new AssessmentStep(AuthorizedToolCatalog.SimulationEcho, "ok")], "tester");
            var approval = plane.Approve(plan.Id, "approver", DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.Equal(AssessmentJobStatus.Completed, (await plane.StartAsync(plan.Id, approval.Id, "tester")).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => plane.StartAsync(plan.Id, approval.Id, "tester"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ToolHost_RunsNmapAgainstLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = ToolHostPath();
        if (!File.Exists(host) || !AuthorizedToolCatalog.Describe().Single(x => x.Id == AuthorizedToolCatalog.NmapQuick).Available) return;
        var result = await RunExternalAsync(host, new AssessmentStep(AuthorizedToolCatalog.NmapQuick,
            "{\"target\":\"127.0.0.1\",\"ports\":\"9,80\"}", 20), ["127.0.0.1"]);
        Assert.True(result.Success, result.Error);
        Assert.Contains("Nmap scan report", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolHost_RunsBoundedDirsearchAgainstLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = ToolHostPath();
        if (!File.Exists(host) || !AuthorizedToolCatalog.Describe().Single(x => x.Id == AuthorizedToolCatalog.DirsearchQuick).Available) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);
        try
        {
            var step = new AssessmentStep(AuthorizedToolCatalog.DirsearchQuick,
                $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{port}}}", 15);
            var result = await RunExternalAsync(host, step, ["127.0.0.1"]);
            Assert.True(result.Success, result.Error + Environment.NewLine + result.Output);
        }
        finally { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } }
    }

    [Fact]
    public async Task ToolHost_RunsBoundedWafw00fAgainstLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = ToolHostPath();
        if (!File.Exists(host) || !AuthorizedToolCatalog.Describe().Single(x => x.Id == AuthorizedToolCatalog.Wafw00fQuick).Available) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);
        try
        {
            var step = new AssessmentStep(AuthorizedToolCatalog.Wafw00fQuick,
                $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{port}}}", 20);
            var result = await RunExternalAsync(host, step, ["127.0.0.1"]);
            Assert.True(result.Success, result.Error + Environment.NewLine + result.Output);
            Assert.Contains("127.0.0.1", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<AssessmentExecutionResult> RunExternalAsync(string hostPath, AssessmentStep step, string[] targets)
    {
        var secretFile = Path.Combine(Path.GetTempPath(), "hackermes-toolhost-secret-" + Guid.NewGuid().ToString("N") + ".dat");
        var oldHost = Environment.GetEnvironmentVariable("HACKERMES_TOOLHOST_PATH");
        var oldSecret = Environment.GetEnvironmentVariable("HACKERMES_TOOLHOST_SECRET_FILE");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_TOOLHOST_PATH", hostPath);
            Environment.SetEnvironmentVariable("HACKERMES_TOOLHOST_SECRET_FILE", secretFile);
            var signer = new ToolHostTicketSigner(new DpapiSecretStore(new NullLogger(), secretFile));
            var executor = new ExternalToolHost(signer, new NullLogger());
            var authorization = new AssessmentExecutionAuthorization("job", "plan", "approval", "scope", "tester", targets, DateTimeOffset.UtcNow.AddMinutes(1));
            return await executor.ExecuteAsync(step, authorization, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_TOOLHOST_PATH", oldHost);
            Environment.SetEnvironmentVariable("HACKERMES_TOOLHOST_SECRET_FILE", oldSecret);
            if (File.Exists(secretFile)) File.Delete(secretFile);
        }
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, ct);
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, ct);
        }
    }

    private static string ToolHostPath() => Environment.GetEnvironmentVariable("HACKERMES_TEST_TOOLHOST_PATH") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hackermes", "Build", "bin", "Hackermes.App", "Debug", "net10.0", "Hackermes.ToolHost.exe");

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public void Set(string key, string? value) { if (value is null) _values.Remove(key); else _values[key] = value; }
        public bool Contains(string key) => _values.ContainsKey(key);
        public void Remove(string key) => _values.Remove(key);
    }
    private sealed class NullLogger : IAppLogger { public void Log(LogLevel level, string category, string message, Exception? exception = null) { } }
    private sealed class TestSettings(string path) : ISettingsService
    {
        public AppSettings Load() => new(); public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null) => true;
        public string SettingsFilePath => path;
    }
}
