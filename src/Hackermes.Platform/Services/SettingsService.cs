using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Serialization;
using System;
using System.IO;
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

        settings.Browser.PageAgentDisabledHosts ??= new();

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
        settings.Ai.Model = string.IsNullOrWhiteSpace(settings.Ai.Model) ? "gpt-5-mini" : settings.Ai.Model.Trim();
        settings.Ai.MaxToolRounds = Math.Clamp(settings.Ai.MaxToolRounds, 1, 64);
        settings.Ai.McpServers ??= new();
        settings.Ai.McpServers.RemoveAll(server => string.IsNullOrWhiteSpace(server.Id) || string.IsNullOrWhiteSpace(server.Command));
        foreach (var server in settings.Ai.McpServers) server.Arguments ??= new();

        settings.Browser.MaxCapturedBodyBytes =
            Math.Clamp(settings.Browser.MaxCapturedBodyBytes, 64 * 1024, 64 * 1024 * 1024);

        settings.Traffic.LastArchivePath = NormalizeRecentPath(settings.Traffic.LastArchivePath);
        settings.Traffic.LastRulesPath = NormalizeRecentPath(settings.Traffic.LastRulesPath);
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
}
