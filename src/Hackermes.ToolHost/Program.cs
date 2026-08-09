using Hackermes.Assessment;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.ToolHost;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            if (input.Length is 0 or > 32_768) return await WriteAsync(new(false, string.Empty, "ToolHost request is empty or too large.")).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<ToolHostEnvelope>(input) ?? throw new UnauthorizedAccessException("ToolHost envelope is invalid.");
            var secretFile = Environment.GetEnvironmentVariable("HACKERMES_TOOLHOST_SECRET_FILE");
            var signer = new ToolHostTicketSigner(SecretStoreFactory.Create(new FileAppLogger(LogLevel.Warn), secretFile));
            var ticket = signer.Verify(envelope);
            ToolHostReplayGuard.Consume(ticket.Nonce, ticket.ExpiresAt);
            var invocation = AuthorizedToolCatalog.BuildInvocation(ticket.Step, ticket.AllowedTargets);
            if (invocation.AdapterId == AuthorizedToolCatalog.SimulationEcho)
                return await WriteAsync(new(true, ticket.Step.Input[..Math.Min(ticket.Step.Input.Length, ticket.Step.MaxOutputBytes)], null, 0)).ConfigureAwait(false);
            var response = await ExecuteAsync(invocation).ConfigureAwait(false);
            return await WriteAsync(response).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await WriteAsync(new(false, string.Empty, exception.Message)).ConfigureAwait(false);
        }
    }

    private static async Task<ToolHostResponse> ExecuteAsync(AuthorizedToolInvocation invocation)
    {
        var start = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var executableName = Path.GetFileName(invocation.ExecutablePath);
        if (executableName.Equals("python.exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("python", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("python3", StringComparison.OrdinalIgnoreCase))
        {
            start.Environment["PYTHONNOUSERSITE"] = "1";
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        }
        foreach (var argument in invocation.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return new(false, string.Empty, "Tool process could not be started.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(invocation.TimeoutSeconds));
        try
        {
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, invocation.MaxOutputBytes, timeout.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, Math.Min(invocation.MaxOutputBytes, 65_536), timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + Environment.NewLine + "[stderr]" + Environment.NewLine + stderr;
            return new(process.ExitCode == 0, combined, process.ExitCode == 0 ? null : $"Tool exited with code {process.ExitCode}.", process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new(false, string.Empty, "Tool execution timed out.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var buffer = new char[2048];
        var output = new StringBuilder(Math.Min(maxBytes, 16_384));
        while (output.Length < maxBytes)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - output.Length)), ct).ConfigureAwait(false);
            if (count == 0) break;
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static async Task<int> WriteAsync(ToolHostResponse response)
    {
        await Console.Out.WriteAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        return response.Success ? 0 : 1;
    }
}
