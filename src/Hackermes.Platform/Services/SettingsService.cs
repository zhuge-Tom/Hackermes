using Hackermes.Base.Diagnostics;
using Hackermes.Base;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Serialization;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hackermes.Platform.Services;

public interface ISettingsService
{
    AppSettings Load();

    bool Save(AppSettings settings);

    /// <summary>读改写的便捷形式。返回是否保存成功。</summary>
    bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null);

    string SettingsFilePath { get; }
}

/// <summary>
/// 设置持久化。三条防线:
/// <list type="number">
/// <item>候选目录逐级回退 —— 某些环境下 LocalAppData 不可写</item>
/// <item>写入走 tmp + 原子替换,并保留 .bak</item>
/// <item>主文件损坏时自动读 .bak</item>
/// </list>
/// 规范化与迁移放在序列化边界(Load 与 Save 两侧都跑),这样无论数据从哪来都是干净的。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string FileName = "settings.json";

    private readonly IEventBus _eventBus;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private AppSettings? _cached;
    private string? _resolvedPath;

    public SettingsService(IEventBus eventBus, IAppLogger logger)
    {
        _eventBus = eventBus;
        _logger = logger.ForCategory(nameof(SettingsService));
    }

    public string SettingsFilePath => _resolvedPath ??= ResolvePath();

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (_cached is not null)
                return _cached;

            var settings = ReadFromDisk() ?? new AppSettings();
            Normalize(settings);
            _cached = settings;
            return settings;
        }
    }

    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);

        lock (_gate)
        {
            if (!WriteToDisk(settings))
                return false;

            _cached = settings;
        }

        _eventBus.Publish(new AppSettingsSavedEvent());
        return true;
    }

    public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var settings = Load();
        mutate(settings);

        if (!Save(settings))
            return false;

        if (changedSection is { } section)
            _eventBus.Publish(new SettingsSectionChangedEvent(section));

        return true;
    }

    private AppSettings? ReadFromDisk()
    {
        var path = SettingsFilePath;

        if (TryRead(path, out var settings))
            return settings;

        var backup = path + ".bak";
        if (File.Exists(backup) && TryRead(backup, out settings))
        {
            _logger.Warn("主配置文件读取失败,已回退到 .bak");
            return settings;
        }

        return null;
    }

    private bool TryRead(string path, out AppSettings? settings)
    {
        settings = null;

        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            return settings is not null;
        }
        catch (Exception ex)
        {
            _logger.Error($"读取配置失败: {path}", ex);
            return false;
        }
    }

    private bool WriteToDisk(AppSettings settings)
    {
        var path = SettingsFilePath;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);

            if (File.Exists(path))
            {
                try
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch
                {
                    // 备份失败不阻断保存。
                }
            }

            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"保存配置失败: {path}", ex);
            return false;
        }
    }

    private string ResolvePath()
    {
        if (AppDataPaths.HasExplicitRoot)
        {
            var explicitRoot = AppDataPaths.Root;
            Directory.CreateDirectory(explicitRoot);
            if (!VerifyWritable(explicitRoot))
                throw new IOException($"Configured Hackermes data root is not writable: {explicitRoot}");
            return AppDataPaths.Resolve(FileName);
        }

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData
                 })
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(folder), "Hackermes");
                Directory.CreateDirectory(dir);

                if (VerifyWritable(dir))
                    return Path.Combine(dir, FileName);
            }
            catch
            {
                // 试下一个候选目录。
            }
        }

        var fallback = Path.Combine(Path.GetTempPath(), "Hackermes");
        Directory.CreateDirectory(fallback);
        _logger.Warn($"配置目录回退到临时目录: {fallback}");
        return Path.Combine(fallback, FileName);
    }

    private static bool VerifyWritable(string dir)
    {
        var probe = Path.Combine(dir, ".write-probe");

        try
        {
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>补齐 null 集合、纠正越界数值。在 Load 与 Save 两侧都执行。</summary>
    private static void Normalize(AppSettings settings)
    {
        settings.General ??= new GeneralSettings();
        settings.Layout ??= new LayoutSettings();
        settings.Browser ??= new BrowserSettings();
        settings.Terminal ??= new TerminalSettings();
        settings.Ai ??= new AiSettings();
        settings.Traffic ??= new TrafficSettings();
        settings.SecurityTools ??= new SecurityToolsSettings();

        settings.Browser.PageAgentDisabledHosts ??= new();
        settings.Browser.HomePage = string.IsNullOrWhiteSpace(settings.Browser.HomePage) ||
                                    string.Equals(settings.Browser.HomePage.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase)
            ? "https://www.bing.com/"
            : settings.Browser.HomePage.Trim();

        var layout = settings.Layout;
        layout.LeftPanelWidth = Math.Clamp(layout.LeftPanelWidth, 160, 800);
        layout.RightPanelWidth = Math.Clamp(layout.RightPanelWidth, 240, 900);
        layout.BottomPanelHeight = Math.Clamp(layout.BottomPanelHeight, 120, 900);

        var terminal = settings.Terminal;
        terminal.FontSize = Math.Clamp(terminal.FontSize, 8, 32);
        terminal.ScrollbackLines = Math.Clamp(terminal.ScrollbackLines, 200, 100_000);

        settings.Ai.Endpoint = string.IsNullOrWhiteSpace(settings.Ai.Endpoint)
            ? "https://api.openai.com/v1"
            : settings.Ai.Endpoint.TrimEnd('/');
        if (settings.Ai.Endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            settings.Ai.Endpoint = settings.Ai.Endpoint[..^"/chat/completions".Length].TrimEnd('/');
        settings.Ai.ChatCompletionsPath = string.IsNullOrWhiteSpace(settings.Ai.ChatCompletionsPath)
            ? "/chat/completions"
            : "/" + settings.Ai.ChatCompletionsPath.Trim().TrimStart('/');
        settings.Ai.Model = string.IsNullOrWhiteSpace(settings.Ai.Model) ? "gpt-5-mini" : settings.Ai.Model.Trim();
        // trustedMode existed before the three-way policy. Preserve an explicit prior choice
        // instead of unexpectedly downgrading it when the application first upgrades.
        if (settings.Ai.TrustedMode)
        {
            settings.Ai.PermissionMode = AiPermissionMode.FullAccess;
            settings.Ai.TrustedMode = false;
        }
        if (!Enum.IsDefined(settings.Ai.PermissionMode))
            settings.Ai.PermissionMode = AiPermissionMode.RequestApproval;
        settings.Ai.MaxToolRounds = Math.Clamp(settings.Ai.MaxToolRounds, 1, 256);
        settings.Ai.MaxContextCharacters = Math.Clamp(settings.Ai.MaxContextCharacters, 4_000, 600_000);
        settings.Ai.MaxRecentMessages = Math.Clamp(settings.Ai.MaxRecentMessages, 2, 64);
        settings.Ai.MaxToolResultCharacters = Math.Clamp(settings.Ai.MaxToolResultCharacters, 1_000, 200_000);
        settings.Ai.ToolCallTimeoutSeconds = Math.Clamp(settings.Ai.ToolCallTimeoutSeconds, 5, 3_600);
        // Persistent compaction/memory is a system capability, not an operator preference.
        // Keep reading the legacy setting for compatibility, but always run it enabled.
        settings.Ai.MemoryEnabled = true;
        settings.Ai.MaxToolDownloadBytes = Math.Clamp(settings.Ai.MaxToolDownloadBytes, 1 * 1024 * 1024, 512 * 1024 * 1024);
        settings.Ai.McpServers ??= new();
        settings.Ai.McpServers.RemoveAll(server => string.IsNullOrWhiteSpace(server.Id) || string.IsNullOrWhiteSpace(server.Command));
        foreach (var server in settings.Ai.McpServers) server.Arguments ??= new();

        // Migrate the former 2 MiB default to the leaner 512 KiB default. Larger
        // explicit values remain supported when users intentionally configure them.
        if (settings.Browser.MaxCapturedBodyBytes == 2 * 1024 * 1024)
            settings.Browser.MaxCapturedBodyBytes = 512 * 1024;
        settings.Browser.MaxCapturedBodyBytes =
            Math.Clamp(settings.Browser.MaxCapturedBodyBytes, 64 * 1024, 64 * 1024 * 1024);
        settings.Browser.ProxyMode = string.Equals(settings.Browser.ProxyMode, "burp", StringComparison.OrdinalIgnoreCase)
            ? "burp"
            : "direct";

        settings.Traffic.LastArchivePath = NormalizeRecentPath(settings.Traffic.LastArchivePath);
        settings.Traffic.LastRulesPath = NormalizeRecentPath(settings.Traffic.LastRulesPath);
        settings.SecurityTools.PrimaryToolRoot = NormalizeToolRoot(settings.SecurityTools.PrimaryToolRoot, @"E:\tool");
        settings.SecurityTools.SecondaryToolRoot = NormalizeToolRoot(settings.SecurityTools.SecondaryToolRoot, @"F:\racetools");
        settings.SecurityTools.TerminalMode = settings.SecurityTools.TerminalMode is "Auto" or "WindowsTerminal" or "PowerShell" or "CommandPrompt"
            ? settings.SecurityTools.TerminalMode : "Auto";
        settings.SecurityTools.WslDistribution = (settings.SecurityTools.WslDistribution ?? string.Empty).Trim();
        settings.SecurityTools.WorkingDirectory = NormalizeRecentPath(settings.SecurityTools.WorkingDirectory) ?? string.Empty;
        settings.SecurityTools.DefaultTimeoutSeconds = Math.Clamp(settings.SecurityTools.DefaultTimeoutSeconds, 10, 120);
        settings.SecurityTools.RecentToolIds ??= new();
        settings.SecurityTools.RecentToolIds.RemoveAll(id => string.IsNullOrWhiteSpace(id));
        // 去重与新到旧的排序由写入侧的归一化函数负责，这里只兜底截断。
        if (settings.SecurityTools.RecentToolIds.Count > 5)
            settings.SecurityTools.RecentToolIds =
                [.. settings.SecurityTools.RecentToolIds.Take(5)];
    }

    private static string? NormalizeRecentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeToolRoot(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return fallback; }
    }
}
