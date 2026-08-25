using Hackermes.App.Views;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>
/// 安全工具目录的进程内缓存。
/// <para>
/// <see cref="DesktopToolCatalog.Describe"/> 每次调用都做同步磁盘探测（File.Exists、PATH 扫描，
/// 极端情况下还有 java -version 子进程），旧实现每次切回左栏 Tab 都在 UI 线程上重跑一遍。
/// 本服务把扫描移到后台线程、同一扫描内去重运行时探测，结果作为不可变快照供界面直接消费；
/// 只有设置变更等显式触发时才重新扫描。
/// </para>
/// </summary>
public sealed class ToolCatalogService
{
    /// <summary>最近使用条目上限。置顶展示用的小集合，不值得为它单独建存储。</summary>
    public const int MaxRecentToolIds = 5;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly Action<Action> _marshal;
    private readonly Func<SecurityToolsSettings, ScanResult> _scan;
    private IReadOnlyList<DesktopToolEntry> _snapshot = [];

    /// <summary>快照已替换。已在 UI 线程上派发（测试可注入直通 marshaller）。</summary>
    public event Action? CatalogChanged;

    public ToolCatalogService(Action<Action>? uiMarshaller = null,
        Func<SecurityToolsSettings, ScanResult>? scan = null)
    {
        _marshal = uiMarshaller ?? (action => UiThreadBridge.Post(action));
        _scan = scan ?? DefaultScan;
    }

    /// <summary>当前目录快照。扫描完成前是空列表 —— 界面据此显示加载中占位。</summary>
    public IReadOnlyList<DesktopToolEntry> Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    /// <summary>清单状态的人读说明；无自定义清单时为 null。</summary>
    public string? ManifestNote { get; private set; }

    /// <summary>一次后台扫描是否正在进行。</summary>
    public bool IsBusy => _scanLock.CurrentCount == 0;

    public async Task RefreshAsync(SecurityToolsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _scanLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() =>
            {
                try { return _scan(settings); }
                catch { return new ScanResult(_snapshot, "工具目录扫描失败；沿用上一次结果。", true); }
            }).ConfigureAwait(false);

            lock (_snapshotGate)
            {
                if (!result.Failed || _snapshot.Count == 0) _snapshot = result.Tools;
            }
            ManifestNote = result.Note;
            _marshal(RaiseCatalogChanged);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void RaiseCatalogChanged() => CatalogChanged?.Invoke();

    private static ScanResult DefaultScan(SecurityToolsSettings settings)
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "tools");
        var probes = new PathProbeCache();
        var builtIn = DesktopToolCatalog.Describe(settings, bundledRoot, probes);

        var manifest = ToolManifest.Load(bundledRoot, out var skipped);
        string? note = null;
        var custom = new List<DesktopToolEntry>();
        if (skipped < 0)
        {
            note = "tools.json 无法解析；自定义工具清单未生效。";
        }
        else if (manifest.Count > 0 || skipped > 0)
        {
            var knownIds = builtIn.Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in manifest)
            {
                if (!knownIds.Add(entry.Id)) { skipped++; continue; }
                var tool = DesktopToolCatalog.CreateCustomEntry(entry, bundledRoot, settings, probes);
                if (tool is null) { skipped++; continue; }
                custom.Add(tool);
                knownIds.Add(tool.Id);
            }
            note = skipped > 0 ? $"自定义工具清单：{custom.Count} 条生效，{skipped} 条无效。"
                : $"自定义工具清单：{custom.Count} 条生效。";
        }

        return new ScanResult([.. builtIn, .. custom], note, Failed: false);
    }

    public sealed record ScanResult(IReadOnlyList<DesktopToolEntry> Tools, string? Note, bool Failed);

    /// <summary>
    /// 最近使用的归一化：新启动的排最前、去重、去空、截断到上限。
    /// 纯函数便于直测；未知 id 由界面渲染时自然过滤（查不到就不显示）。
    /// </summary>
    public static IReadOnlyList<string> NormalizeRecentIds(IEnumerable<string?> existing, string launched)
    {
        var ordered = string.IsNullOrWhiteSpace(launched)
            ? new List<string>()
            : [launched.Trim()];
        foreach (var id in existing)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var trimmed = id.Trim();
            if (!ordered.Contains(trimmed, StringComparer.Ordinal)) ordered.Add(trimmed);
            if (ordered.Count >= MaxRecentToolIds) break;
        }
        return ordered;
    }

}
