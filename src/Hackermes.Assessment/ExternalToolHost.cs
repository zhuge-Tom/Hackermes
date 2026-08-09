using Hackermes.Base.Diagnostics;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Assessment;

/// <summary>Runs one signed request in one short-lived ToolHost process.</summary>
public sealed class ExternalToolHost : IAssessmentExecutionHost
{
    private readonly ToolHostTicketSigner _signer;
    private readonly IAppLogger _logger;

    public ExternalToolHost(ToolHostTicketSigner signer, IAppLogger logger)
    {
        _signer = signer;
        _logger = logger.ForCategory(nameof(ExternalToolHost));
    }

    public async Task<AssessmentExecutionResult> ExecuteAsync(AssessmentStep step, AssessmentExecutionAuthorization authorization, CancellationToken ct)
    {
        // Validate in the desktop process before a child process is created.
        AuthorizedToolCatalog.BuildInvocation(step, authorization.AllowedTargets);
        var ticket = new ToolHostTicket(Guid.NewGuid().ToString("N"), authorization.JobId, authorization.PlanId,
            authorization.ApprovalId, authorization.ScopeId, authorization.Actor, [.. authorization.AllowedTargets],
            step, DateTimeOffset.UtcNow, authorization.ExpiresAt);
        var envelope = _signer.Issue(ticket);

        var start = ResolveStartInfo();
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) return new(false, string.Empty, "ToolHost could not be started.");
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(envelope).AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stdout)) return new(false, string.Empty, string.IsNullOrWhiteSpace(stderr) ? $"ToolHost exited with code {process.ExitCode}." : stderr);
            var response = JsonSerializer.Deserialize<ToolHostResponse>(stdout);
            return response is null ? new(false, string.Empty, "ToolHost returned invalid JSON.") : new(response.Success, response.Output, response.Error);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            _logger.Warn($"ToolHost failed: {exception.Message}");
            return new(false, string.Empty, exception.Message);
        }
    }

    private static ProcessStartInfo ResolveStartInfo()
    {
        var configured = Environment.GetEnvironmentVariable("HACKERMES_TOOLHOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Base(Path.GetFullPath(configured));

        var executableName = OperatingSystem.IsWindows() ? "Hackermes.ToolHost.exe" : "Hackermes.ToolHost";
        var executable = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(executable)) return Base(executable);
        var assembly = Path.Combine(AppContext.BaseDirectory, "Hackermes.ToolHost.dll");
        if (File.Exists(assembly)) { var info = Base("dotnet"); info.ArgumentList.Add(assembly); return info; }
        throw new FileNotFoundException("Hackermes.ToolHost is not installed next to the desktop application.", executable);
    }

    private static ProcessStartInfo Base(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
