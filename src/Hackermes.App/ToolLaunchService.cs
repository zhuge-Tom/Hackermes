using Hackermes.Assessment;
using Hackermes.Base;
using Hackermes.App.Views;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Hackermes.App;

/// <summary>Human-facing launcher that preserves each tool's native interaction model.</summary>
public sealed class ToolLaunchService
{
    public void LaunchGui(string executablePath)
    {
        var start = CreateGuiStartInfo(executablePath);
        Directory.CreateDirectory(start.Environment["TEMP"]!);
        _ = Process.Start(start) ?? throw new InvalidOperationException("工具进程未能启动。");
    }

    /// <summary>
    /// Launches a GUI tool entry, choosing the bundled-JavaFX runtime for tools declared
    /// with RequiresJavaFx (OpenJDK does not ship JavaFX) and a plain JVM for Swing jars.
    /// </summary>
    public void LaunchGui(DesktopToolEntry tool)
    {
        var start = DesktopToolCatalog.TryGetBundledGuiLaunch(tool.Id, out var java, out var arguments, out var workingDirectory)
            ? CreateJavaGuiStartInfo(java, arguments, workingDirectory)
            : CreateGuiStartInfo(tool.Path!);
        Directory.CreateDirectory(start.Environment["TEMP"]!);
        _ = Process.Start(start) ?? throw new InvalidOperationException("工具进程未能启动。");
    }

    internal static ProcessStartInfo CreateJavaGuiStartInfo(string java, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userData))
            userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        var start = new ProcessStartInfo
        {
            FileName = java,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        start.Environment["TEMP"] = Path.Combine(userData, "Temp");
        start.Environment["TMP"] = Path.Combine(userData, "Temp");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// GUI 工具必须使用当前用户的临时目录。开发宿主、提权启动器或系统服务可能把
    /// TEMP/TMP 设为 C:\Windows\Temp；旧版 PyInstaller/Tk 程序虽然能在那里解包，
    /// 却可能在创建窗口时直接报 Failed to execute script。
    /// </summary>
    internal static ProcessStartInfo CreateGuiStartInfo(string executablePath, string? localAppData = null)
    {
        var executable = RequireFile(executablePath);
        var userData = string.IsNullOrWhiteSpace(localAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppData;
        if (string.IsNullOrWhiteSpace(userData))
            userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        var userTemp = Path.GetFullPath(Path.Combine(userData, "Temp"));
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        start.Environment["TEMP"] = userTemp;
        start.Environment["TMP"] = userTemp;
        return start;
    }

    public void OpenDocument(string path)
    {
        var document = RequireFile(path);
        _ = Process.Start(new ProcessStartInfo { FileName = document, UseShellExecute = true })
            ?? throw new InvalidOperationException("文件未能打开。");
    }

    public void LaunchBatch(string batchPath)
    {
        var batch = RequireFile(batchPath);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            WorkingDirectory = Path.GetDirectoryName(batch)!,
            UseShellExecute = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(batch);
        _ = Process.Start(start) ?? throw new InvalidOperationException("批处理工具未能启动。");
    }

    public void LaunchTeachingTerminal(DesktopToolEntry tool, SecurityToolsSettings settings)
    {
        if (tool.Kind != DesktopToolKind.TeachingTerminal)
            throw new ArgumentException("工具不是教学终端入口。", nameof(tool));
        var entry = RequireFile(tool.Path ?? string.Empty);
        var workingDirectory = ResolveWorkingDirectory(settings, tool.WorkingDirectory ?? Path.GetDirectoryName(entry)!);
        var start = CreateTeachingTerminalStartInfo(tool, settings, workingDirectory);
        _ = Process.Start(start) ?? throw new InvalidOperationException("教学终端未能启动。");
    }

    public static ProcessStartInfo CreateTeachingTerminalStartInfo(
        DesktopToolEntry tool, SecurityToolsSettings settings, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("终端工作目录不能为空。", nameof(workingDirectory));
        var mode = ResolveTerminalMode(settings.TerminalMode);
        return mode switch
        {
            "WindowsTerminal" => WindowsTerminal(BuildTeachingScript(tool), workingDirectory),
            "CommandPrompt" => TeachingCommandPrompt(tool, workingDirectory),
            _ => PowerShell(BuildTeachingScript(tool), workingDirectory)
        };
    }

    public static string BuildTeachingScript(DesktopToolEntry tool)
    {
        var commands = new List<string>();
        var bundledPython = FindBundledPython(tool.Path);
        if (bundledPython is not null)
        {
            commands.Add("$env:PYTHONNOUSERSITE = '1'");
            commands.Add("$env:PYTHONDONTWRITEBYTECODE = '1'");
            commands.Add("$env:PATH = " + QuotePowerShell(Path.GetDirectoryName(bundledPython)!) + " + ';' + $env:PATH");
        }
        var lines = new List<string>
        {
            $"[Hackermes] {tool.Name}",
            $"工具位置：{tool.Path}",
            "仅对你拥有或已明确授权的目标使用本工具。",
            string.Empty
        };
        lines.AddRange(tool.Instructions ?? []);
        lines.Add(string.Empty);
        lines.Add("终端已就绪，请在下方输入原生工具命令。");
        commands.AddRange(lines.Select(line => "Write-Host " + QuotePowerShell(line)));
        return string.Join("; ", commands);
    }

    public void LaunchTerminal(AuthorizedToolInvocation invocation, SecurityToolsSettings settings)
    {
        var executable = RequireFile(invocation.ExecutablePath);
        var workingDirectory = ResolveWorkingDirectory(settings, invocation.WorkingDirectory);
        var command = PowerShellCommand(executable, invocation.Arguments);
        var mode = ResolveTerminalMode(settings.TerminalMode);

        ProcessStartInfo start = mode switch
        {
            "WindowsTerminal" => WindowsTerminal(command, workingDirectory),
            "CommandPrompt" => CommandPrompt(executable, invocation.Arguments, workingDirectory),
            _ => PowerShell(command, workingDirectory)
        };
        _ = Process.Start(start) ?? throw new InvalidOperationException("终端未能启动。");
    }

    public void LaunchWsl(string linuxCommand, SecurityToolsSettings settings)
    {
        if (string.IsNullOrWhiteSpace(linuxCommand)) throw new ArgumentException("Linux 命令不能为空。", nameof(linuxCommand));
        var start = new ProcessStartInfo { FileName = "wsl.exe", UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(settings.WslDistribution))
        {
            start.ArgumentList.Add("--distribution");
            start.ArgumentList.Add(settings.WslDistribution);
        }
        start.ArgumentList.Add("--"); start.ArgumentList.Add("bash"); start.ArgumentList.Add("-lc");
        start.ArgumentList.Add(linuxCommand + "; printf '\n[Hackermes] command finished.\n'; exec bash");
        _ = Process.Start(start) ?? throw new InvalidOperationException("WSL 终端未能启动。请确认 WSL 已安装。");
    }

    private static ProcessStartInfo WindowsTerminal(string command, string workingDirectory)
    {
        var shell = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe";
        // Windows Terminal treats semicolons in its command line as separators
        // between terminal actions. Passing a PowerShell script verbatim can
        // therefore create one error tab per Write-Host statement. EncodedCommand
        // keeps the complete script inside one opaque argument.
        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var start = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("new-tab"); start.ArgumentList.Add("--startingDirectory"); start.ArgumentList.Add(workingDirectory);
        start.ArgumentList.Add(shell); start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoExit");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(encodedCommand);
        return start;
    }

    private static ProcessStartInfo PowerShell(string command, string workingDirectory)
    {
        var start = new ProcessStartInfo { FileName = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe", WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoExit"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(command);
        return start;
    }

    private static ProcessStartInfo CommandPrompt(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var command = QuoteCmd(executable) + " " + string.Join(" ", arguments.Select(QuoteCmd)) + " & echo. & echo [Hackermes] command finished.";
        var start = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/k"); start.ArgumentList.Add(command); return start;
    }

    private static ProcessStartInfo TeachingCommandPrompt(DesktopToolEntry tool, string workingDirectory)
    {
        var lines = new List<string>
        {
            $"[Hackermes] {tool.Name}",
            $"工具位置：{tool.Path}",
            "仅对你拥有或已明确授权的目标使用本工具。",
            string.Empty
        };
        lines.AddRange(tool.Instructions ?? []);
        lines.Add(string.Empty);
        lines.Add("终端已就绪，请在下方输入原生工具命令。");
        var commands = new List<string>();
        var bundledPython = FindBundledPython(tool.Path);
        if (bundledPython is not null)
        {
            commands.Add("set PYTHONNOUSERSITE=1");
            commands.Add("set PYTHONDONTWRITEBYTECODE=1");
            commands.Add("set PATH=" + QuoteCmd(Path.GetDirectoryName(bundledPython)!) + ";%PATH%");
        }
        commands.AddRange(lines.Select(line => line.Length == 0 ? "echo." : "echo " + QuoteCmdEcho(line)));
        var command = string.Join(" & ", commands);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/k");
        start.ArgumentList.Add(command);
        return start;
    }

    private static string ResolveTerminalMode(string configured)
    {
        if (configured != "Auto") return configured;
        if (FindOnPath("wt.exe") is not null) return "WindowsTerminal";
        return "PowerShell";
    }

    private static string ResolveWorkingDirectory(SecurityToolsSettings settings, string invocationDirectory)
    {
        var candidate = !string.IsNullOrWhiteSpace(settings.WorkingDirectory) ? settings.WorkingDirectory : invocationDirectory;
        if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) return candidate;
        var fallback = AppDataPaths.Resolve("ToolWork");
        Directory.CreateDirectory(fallback); return fallback;
    }

    private static string PowerShellCommand(string executable, IReadOnlyList<string> arguments) =>
        "& " + QuotePowerShell(executable) + " " + string.Join(" ", arguments.Select(QuotePowerShell)) +
        "; Write-Host ''; Write-Host '[Hackermes] command finished.'";

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string QuoteCmdEcho(string value) => value
        .Replace("^", "^^", StringComparison.Ordinal)
        .Replace("&", "^&", StringComparison.Ordinal)
        .Replace("|", "^|", StringComparison.Ordinal)
        .Replace("<", "^<", StringComparison.Ordinal)
        .Replace(">", "^>", StringComparison.Ordinal);
    private static string QuoteCmd(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string? FindBundledPython(string? toolPath)
    {
        if (string.IsNullOrWhiteSpace(toolPath)) return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(toolPath))!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "_runtime", "python", "python.exe");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
    private static string RequireFile(string path) => File.Exists(path) ? Path.GetFullPath(path) : throw new FileNotFoundException("工具文件不存在。", path);
    private static string? FindOnPath(string fileName) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => Path.Combine(path, fileName)).FirstOrDefault(File.Exists);
}
