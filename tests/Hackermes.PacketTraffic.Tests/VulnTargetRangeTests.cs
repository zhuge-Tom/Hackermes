using Hackermes.Assessment;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Stage-3 test-range regression: exercises the vendored tool sources through the exact
/// production argument surface (AuthorizedToolCatalog.BuildInvocation) against the local
/// mock range, then asserts the ReconObservationParser maps outputs to finding candidates.
/// Skipped quietly when Python or git are unavailable on PATH.
/// </summary>
[Collection("ToolHost serial")]
public sealed class VulnTargetRangeTests
{
    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => Path.Combine(path, fileName)).FirstOrDefault(File.Exists);

    private static string? PythonPath() => FindOnPath("python.exe");

    private static string FixturesRoot() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools");

    private static string ToolPath(params string[] parts) =>
        Path.Combine([FixturesRoot(), .. parts]);

    [Fact]
    public void Range_OaPocTongdaAndSeeyouProduceHitsWithSeverities()
    {
        var python = PythonPath();
        var runner = ToolPath("detect.oa-poc.terminal", "oa_poc_runner.py");
        if (python is null || !File.Exists(runner)) return;
        using var range = new RangeServer(python);
        var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
        var oldRunner = Environment.GetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            Environment.SetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH", runner);
            var baseInput = $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{range.Port}}}";

            var tongda = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectOaPocProbe,
                    $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{range.Port},\"module\":\"tongda\",\"poc\":\"tongda-session-disclosure.yaml\"}}", 60),
                ["127.0.0.1"]);
            var tongdaOutput = range.Run(python, tongda.WorkingDirectory, tongda.Arguments);
            Assert.True(tongdaOutput.Contains("[HIT]"), "tongda output: " + tongdaOutput);
            var observations = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectOaPocProbe, tongdaOutput, baseInput);
            var finding = Assert.Single(observations);
            Assert.Equal("Medium", finding.Severity);
            Assert.StartsWith("oa-poc-", finding.Code);

            var seeyon = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.DetectOaPocProbe,
                    $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{range.Port},\"module\":\"seeyou\",\"poc\":\"Seeyon-Unauthori-Access.yaml\"}}", 60),
                ["127.0.0.1"]);
            var seeyonOutput = range.Run(python, seeyon.WorkingDirectory, seeyon.Arguments);
            Assert.Contains("[HIT]", seeyonOutput);
            var seeyonFindings = ReconObservationParser.Parse(AuthorizedToolCatalog.DetectOaPocProbe, seeyonOutput, baseInput);
            Assert.Equal("High", seeyonFindings.Single().Severity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
            Environment.SetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH", oldRunner);
        }
    }

    [Fact]
    public void Range_GitHackRestoresDumbHttpRepository()
    {
        var python = PythonPath();
        var git = FindOnPath("git.exe");
        var gitHack = ToolPath("recon.git-leak.terminal", "GitHack.py");
        if (python is null || git is null || !File.Exists(gitHack)) return;
        // The repository must exist before the range server starts: it is served
        // statically from --gitroot for the whole server lifetime.
        var repoRoot = RangeServer.PrepareGitRepository(git);
        try
        {
            using var range = new RangeServer(python, repoRoot);
            var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
            var oldGitHack = Environment.GetEnvironmentVariable("HACKERMES_GITHACK_PATH");
            var stepInput = $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{range.Port}}}";
            try
            {
                Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
                Environment.SetEnvironmentVariable("HACKERMES_GITHACK_PATH", gitHack);
                var invocation = AuthorizedToolCatalog.BuildInvocation(
                    new AssessmentStep(AuthorizedToolCatalog.ReconGitLeakScan, stepInput, 120),
                    ["127.0.0.1"]);
                Assert.Equal(gitHack, invocation.Arguments[0]);
                var output = range.Run(python, invocation.WorkingDirectory, invocation.Arguments);
                Assert.True(output.Contains("Clone Success"), "githack output: " + output);
                var findings = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconGitLeakScan, output, stepInput);
                var finding = Assert.Single(findings);
                Assert.Equal("git-repo-disclosure", finding.Code);
                Assert.Equal("Medium", finding.Severity);
                Assert.Contains("/.git/", finding.PoC);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
                Environment.SetEnvironmentVariable("HACKERMES_GITHACK_PATH", oldGitHack);
            }
        }
        finally
        {
            try { Directory.Delete(repoRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Range_SwaggerHackEnumeratesLiveEndpoints()
    {
        var python = PythonPath();
        var swagger = ToolPath("recon.swagger-api.terminal", "swagger-hack2.0.py");
        if (python is null || !File.Exists(swagger)) return;
        using var range = new RangeServer(python);
        var oldPython = Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH");
        var oldSwagger = Environment.GetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH");
        // check() inspects only the URL it is given, so the range test points the
        // adapter straight at the api-docs path, the way the agent would after discovery.
        var stepInput = $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":{range.Port},\"path\":\"/v2/api-docs\"}}";
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", python);
            Environment.SetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH", swagger);
            var invocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ReconSwaggerApiEnum, stepInput, 120),
                ["127.0.0.1"]);
            var output = range.Run(python, invocation.WorkingDirectory, invocation.Arguments);
            var findings = ReconObservationParser.Parse(AuthorizedToolCatalog.ReconSwaggerApiEnum, output, stepInput);
            Assert.True(findings is { Count: 1 }, "swagger output: " + output);
            Assert.Equal("swagger-api-exposure", findings[0].Code);
            Assert.Equal("Medium", findings[0].Severity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_PYTHON_PATH", oldPython);
            Environment.SetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH", oldSwagger);
        }
    }

    private sealed class RangeServer : IDisposable
    {
        private readonly Process _process;
        public int Port { get; }

        public RangeServer(string python, string? gitRoot = null)
        {
            var server = ToolPath("_testrange", "testrange_server.py");
            var start = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(server);
            if (gitRoot is not null)
            {
                start.ArgumentList.Add("--gitroot");
                start.ArgumentList.Add(gitRoot);
                _gitRoot = gitRoot;
            }
            _process = Process.Start(start) ?? throw new InvalidOperationException("Range server could not start.");
            // The server picks its own ephemeral port and reports it, so parallel
            // test collections can never steal the port between probe and bind.
            var ready = _process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(20_000) || ready.Result is null)
                throw new TimeoutException("Test range server did not report READY.");
            Port = int.Parse(ready.Result["RANGE_READY port=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string PrepareGitRepository(string git)
        {
            var root = Path.Combine(Path.GetTempPath(), "hackermes-range-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            RunGit(git, root, ["init", "-q"]);
            File.WriteAllText(Path.Combine(root, "range-secret.txt"), "hackermes-range-marker\n");
            RunGit(git, root, ["add", "."]);
            RunGit(git, root, ["-c", "user.email=range@test", "-c", "user.name=range", "commit", "-qm", "range"]);
            RunGit(git, root, ["update-server-info"]);
            return root;
        }

        private string? _gitRoot;

        public string Run(string python, string workingDirectory, IReadOnlyList<string> arguments)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"Range server exited early with code {_process.ExitCode}; stderr: {_process.StandardError.ReadToEnd()}");
            var start = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Tool could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { try { process.Kill(true); } catch { } throw new TimeoutException("Tool exceeded 120s."); }
            var text = stdout.Result;
            var error = stderr.Result;
            return string.IsNullOrWhiteSpace(error) ? text : text + Environment.NewLine + "[stderr]" + Environment.NewLine + error;
        }

        private static string RunGit(string git, string workingDirectory, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = git, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not start.");
            if (!process.WaitForExit(30_000)) throw new TimeoutException("git did not finish.");
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0) throw new InvalidOperationException("git failed: " + error);
            return process.StandardOutput.ReadToEnd();
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(true); } catch { }
            _process.Dispose();
            if (_gitRoot is not null)
            {
                try { Directory.Delete(_gitRoot, recursive: true); } catch { }
            }
        }
    }
}
