using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Terminal.Services;

/// <summary>Resolves the native interactive shell and executes <c>!</c> REPL commands.</summary>
public sealed class ShellCommandService
{
    public ShellLaunchSpec ResolveInteractiveShell()
    {
        var workingDirectory = Directory.GetCurrentDirectory();

        if (OperatingSystem.IsWindows())
        {
            var powerShell = FindOnPath("pwsh.exe");
            return powerShell is not null
                ? new ShellLaunchSpec(powerShell, [], workingDirectory, "PowerShell")
                : new ShellLaunchSpec(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", [], workingDirectory, "Command Prompt");
        }

        var configured = Environment.GetEnvironmentVariable("SHELL");
        var shell = !string.IsNullOrWhiteSpace(configured) ? configured : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        return new ShellLaunchSpec(shell, [], workingDirectory, Path.GetFileName(shell));
    }

    public async Task<ShellCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new ShellCommandResult(0, string.Empty, string.Empty);

        var shell = ResolveInteractiveShell();
        var startInfo = new ProcessStartInfo
        {
            FileName = shell.Process,
            WorkingDirectory = shell.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows() && Path.GetFileName(shell.Process).StartsWith("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ShellCommandResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(path, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public sealed record ShellLaunchSpec(string Process, string[] Arguments, string WorkingDirectory, string DisplayName);

public sealed record ShellCommandResult(int ExitCode, string StandardOutput, string StandardError);
