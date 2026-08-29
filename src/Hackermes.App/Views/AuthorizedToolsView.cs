using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hackermes.App.Views;

/// <summary>
/// 左栏「安全工具」面板。
/// <para>
/// 目录数据来自 <see cref="ToolCatalogService"/> 的后台扫描快照 —— 本视图不在 UI 线程上做任何
/// 磁盘探测；Tab 激活也不再整体重建视觉树，只有目录快照变化或搜索词变化时才重建列表，
/// 分组展开状态与滚动位置跨重建保留。
/// </para>
/// </summary>
public sealed class AuthorizedToolsView : UserControl
{
    private readonly ISettingsService _settings;
    private readonly ToolLaunchService _launcher;
    private readonly ToolCatalogService _catalog;
    private readonly IEventBus _eventBus;

    private readonly TextBox _search = new()
    {
        PlaceholderText = "搜索：名称 / 描述 / 分类",
        FontSize = 12
    };
    private readonly StackPanel _listHost = new() { Spacing = 4 };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, 2, 0)
    };
    // SelectableTextBlock：错误/状态信息可选中复制。
    private readonly SelectableTextBlock _status = new() { FontSize = 11, Opacity = .68, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _manifestNote = new() { FontSize = 10, Opacity = .55, TextWrapping = TextWrapping.Wrap };

    /// <summary>用户明确设置过的分类展开状态；未接入工具默认收起，其余默认展开。</summary>
    private readonly Dictionary<string, bool> _categoryExpansion;
    private readonly List<(DesktopToolEntry Tool, Border Row, string? Group)> _rows = [];
    private List<DesktopToolEntry> _navTools = [];
    private int _keyboardIndex = -1;

    public AuthorizedToolsView(ISettingsService settings, ToolLaunchService launcher,
        ToolCatalogService catalog, IEventBus eventBus)
    {
        _settings = settings;
        _launcher = launcher;
        _catalog = catalog;
        _eventBus = eventBus;
        _categoryExpansion = new Dictionary<string, bool>(
            _settings.Load().SecurityTools.ToolCategoryExpansion ?? [], StringComparer.Ordinal);

        BuildSkeleton();
        AttachBehaviors();

        _catalog.CatalogChanged += OnCatalogChanged;
        RebuildList(restoreScrollOffset: false);

        _ = RefreshCatalogAsync();
    }

    private void BuildSkeleton()
    {
        var searchHost = new Border
        {
            Padding = new Thickness(8, 8, 8, 4),
            Child = _search
        };
        var footer = new StackPanel
        {
            Margin = new Thickness(10, 4, 10, 8),
            Spacing = 3,
            Children = { _manifestNote, _status }
        };
        _scroll.Content = _listHost;
        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                searchHost,
                footer,
                _scroll
            }
        };
        DockPanel.SetDock(searchHost, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
    }

    private void AttachBehaviors()
    {
        _search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) RebuildList(restoreScrollOffset: false);
        };
        // 方向键/回车在隧道阶段拦截 —— 单行 TextBox 的类处理可能先吃掉按键,
        // 隧道处理器保证导航语义优先于光标移动。
        AddHandler(KeyDownEvent, OnViewPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>设置窗口保存后由宿主调用；也用于首次加载。</summary>
    public async Task RefreshCatalogAsync()
    {
        try { await _catalog.RefreshAsync(_settings.Load().SecurityTools); }
        catch (Exception exception) { _status.Text = "工具目录刷新失败：" + exception.Message; }
    }

    private void OnCatalogChanged() => RebuildList(restoreScrollOffset: true);

    // ─────────────────────────────────────────────────────────────────────────
    // 列表构建

    private void RebuildList(bool restoreScrollOffset)
    {
        var savedOffset = restoreScrollOffset ? _scroll.Offset : default;
        ResetKeyboardSelection();
        _rows.Clear();
        _listHost.Children.Clear();
        _manifestNote.Text = _catalog.ManifestNote ?? string.Empty;

        var snapshot = _catalog.Snapshot;
        if (snapshot.Count == 0)
        {
            _listHost.Children.Add(new TextBlock
            {
                Text = "正在扫描工具目录…",
                Opacity = .6,
                FontSize = 12,
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var query = _search.Text;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var matches = snapshot.Where(tool => ToolSearchFilter.Matches(tool, query)).ToArray();
            if (matches.Length == 0)
            {
                _listHost.Children.Add(new TextBlock
                {
                    Text = "没有匹配的工具。",
                    Opacity = .6,
                    FontSize = 12,
                    Margin = new Thickness(12)
                });
            }
            else
            {
                foreach (var tool in matches) _listHost.Children.Add(BuildRow(tool, groupName: null));
            }
        }
        else
        {
            AppendRecentSection(snapshot);
            foreach (var group in ToolCatalogPresentation.Group(snapshot))
                AppendGroup(group.Category, group.Tools.ToList());
        }

        RebuildNavTools();
        if (restoreScrollOffset && savedOffset != default)
            Dispatcher.UIThread.Post(() => _scroll.Offset = savedOffset, DispatcherPriority.Loaded);
    }

    private void AppendRecentSection(IReadOnlyList<DesktopToolEntry> snapshot)
    {
        var recentIds = ReadRecentIds();
        if (recentIds.Count == 0) return;
        var known = snapshot.ToDictionary(tool => tool.Id, StringComparer.Ordinal);
        var recent = recentIds.Where(known.ContainsKey).Select(id => known[id]).Where(tool => tool.Available).ToList();
        if (recent.Count == 0) return;

        _listHost.Children.Add(new TextBlock
        {
            Text = "最近使用",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Opacity = .62,
            Margin = new Thickness(10, 4, 0, 0)
        });
        var host = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var tool in recent) host.Children.Add(BuildRow(tool, groupName: null));
        _listHost.Children.Add(host);
        _listHost.Children.Add(new Separator { Margin = new Thickness(10, 2, 10, 2) });
    }

    private void AppendGroup(string category, List<DesktopToolEntry> tools)
    {
        var body = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var tool in tools) body.Children.Add(BuildRow(tool, groupName: category));

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        header.Children.Add(new TextBlock { Text = category, FontWeight = FontWeight.SemiBold, FontSize = 12 });
        var available = tools.Count(tool => tool.Available);
        header.Children.Add(new TextBlock
        {
            Text = available == tools.Count ? $"({tools.Count})" : $"({available}/{tools.Count})",
            FontSize = 11,
            Opacity = .55,
            VerticalAlignment = VerticalAlignment.Center
        });

        var expander = new Expander
        {
            Header = header,
            Content = body,
            IsExpanded = IsCategoryExpanded(category),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 2)
        };
        expander.Expanded += (_, _) => SetCategoryExpanded(category, true);
        expander.Collapsed += (_, _) => SetCategoryExpanded(category, false);
        _listHost.Children.Add(expander);
    }

    /// <summary>紧凑行：一行只放名称 + 徽标；描述/不可用原因/路径进 ToolTip。</summary>
    private Border BuildRow(DesktopToolEntry tool, string? groupName)
    {
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.Add(new TextBlock
        {
            Text = tool.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (tool.AdapterId is not null)
        {
            nameRow.Children.Add(new Border
            {
                Background = ResourceBrush("SemiColorPrimary"),
                Opacity = .85,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "受控",
                    FontSize = 9,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        var badge = ToolCatalogPresentation.StatusLabel(tool);
        if (badge is not null)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 9,
                Opacity = .55,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var launchButton = new Button
        {
            Content = nameRow,
            IsEnabled = tool.Available,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        launchButton.Click += async (_, _) => await LaunchAsync(tool);

        var row = new Border
        {
            Child = launchButton,
            CornerRadius = new CornerRadius(4)
        };
        ToolTip.SetTip(row, BuildToolTip(tool));
        row.ContextMenu = BuildContextMenu(tool);
        _rows.Add((tool, row, groupName));
        return row;
    }

    private static string BuildToolTip(DesktopToolEntry tool)
    {
        var lines = new List<string> { tool.Description };
        if (!tool.Available && !string.IsNullOrWhiteSpace(tool.UnavailableReason))
            lines.Add("⚠ " + tool.UnavailableReason);
        else if (!string.IsNullOrWhiteSpace(tool.VerificationNote))
            lines.Add("⚠ " + tool.VerificationNote);
        else if (tool.Path is not null)
            lines.Add(tool.Path);
        if (tool.Path is not null && !lines.Contains(tool.Path, StringComparer.Ordinal))
            lines.Add(tool.Path);
        if (tool.Instructions is { Count: > 0 })
            lines.Add("常用示例：" + string.Join(" ｜ ", tool.Instructions.Take(2)));
        return string.Join('\n', lines);
    }

    private ContextMenu? BuildContextMenu(DesktopToolEntry tool)
    {
        if (tool.Path is null && tool.AdapterId is null) return null;
        var items = new List<object>();
        if (tool.Path is not null)
        {
            var copyPath = new MenuItem { Header = "复制路径" };
            copyPath.Click += async (_, _) => await CopyToClipboardAsync(tool.Path);
            items.Add(copyPath);

            var openFolder = new MenuItem { Header = "打开所在目录" };
            openFolder.Click += (_, _) => OpenContainingFolder(tool);
            items.Add(openFolder);
        }
        if (tool.AdapterId is not null)
        {
            var gotoAssessment = new MenuItem { Header = "转到授权评估（受控执行）" };
            gotoAssessment.Click += (_, _) => _eventBus.Publish(
                new SwitchDockTabRequestedEvent(DockPosition.Content, "authorized-assessment"));
            items.Add(gotoAssessment);
        }
        return new ContextMenu { ItemsSource = items };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 键盘导航

    private void RebuildNavTools()
    {
        // 收起分组里的行虽然已构建但不参与键盘导航 —— 导航列表始终对应用户看得见的行。
        _navTools = _rows
            .Where(row => row.Group is null || IsCategoryExpanded(row.Group))
            .Select(row => row.Tool)
            .DistinctBy(tool => tool.Id)
            .ToList();
        if (_keyboardIndex >= _navTools.Count) SetKeyboardSelection(_navTools.Count - 1);
    }

    private void OnViewPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ReferenceEquals(e.Source, _search) && HandleSearchKey(e)) return;
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>搜索框内的导航键。返回 true 表示已消费。</summary>
    private bool HandleSearchKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                SetKeyboardSelection(Math.Min(_keyboardIndex + 1, _navTools.Count - 1));
                return true;
            case Key.Up:
                SetKeyboardSelection(Math.Max(_keyboardIndex - 1, 0));
                return true;
            case Key.Enter:
                if (_navTools.Count > 0)
                {
                    var target = _keyboardIndex >= 0 ? _keyboardIndex : 0;
                    _ = LaunchAsync(_navTools[target]);
                }
                return true;
            case Key.Escape:
                _search.Text = string.Empty;
                return true;
            default:
                return false;
        }
    }

    private void ResetKeyboardSelection()
    {
        if (_keyboardIndex >= 0 && _keyboardIndex < _navTools.Count)
        {
            var previous = _rows.FirstOrDefault(row => ReferenceEquals(row.Tool, _navTools[_keyboardIndex]));
            if (previous.Row is not null) HighlightRow(previous.Row, false);
        }
        _keyboardIndex = -1;
    }

    private void SetKeyboardSelection(int index)
    {
        if (_navTools.Count == 0) return;
        if (_keyboardIndex >= 0 && _keyboardIndex < _navTools.Count)
        {
            var previous = _rows.FirstOrDefault(row => ReferenceEquals(row.Tool, _navTools[_keyboardIndex]));
            if (previous.Row is not null) HighlightRow(previous.Row, false);
        }
        _keyboardIndex = Math.Clamp(index, 0, _navTools.Count - 1);
        var selected = _rows.FirstOrDefault(row => ReferenceEquals(row.Tool, _navTools[_keyboardIndex]));
        if (selected.Row is null) return;
        HighlightRow(selected.Row, true);
        selected.Row.BringIntoView();
    }

    /// <summary>键盘选中高亮用固定半透明灰，不依赖主题资源键是否存在。</summary>
    private static readonly IBrush KeyboardHighlightBrush = new SolidColorBrush(Color.Parse("#20808080"));

    private void HighlightRow(Border row, bool highlighted) =>
        row.Background = highlighted ? KeyboardHighlightBrush : null;

    // ─────────────────────────────────────────────────────────────────────────
    // 启动与辅助

    private async Task LaunchAsync(DesktopToolEntry tool)
    {
        try
        {
            _status.Text = string.Empty;
            switch (tool.Kind)
            {
                case DesktopToolKind.BuiltIn:
                    await ShowBuiltInWindowAsync(tool);
                    break;
                case DesktopToolKind.Gui:
                    _launcher.LaunchGui(tool);
                    _status.Text = $"已启动：{tool.Name}";
                    break;
                case DesktopToolKind.Shortcut:
                    _launcher.OpenDocument(tool.Path!);
                    _status.Text = $"已打开：{tool.Name}";
                    break;
                case DesktopToolKind.Batch:
                    _launcher.LaunchBatch(tool.Path!);
                    _status.Text = $"已启动：{tool.Name}";
                    break;
                case DesktopToolKind.TeachingTerminal:
                    _launcher.LaunchTeachingTerminal(tool, _settings.Load().SecurityTools);
                    _status.Text = $"已打开教学终端：{tool.Name}";
                    break;
            }
            NoteLaunched(tool.Id);
            RebuildList(restoreScrollOffset: true);
        }
        catch (Exception exception) { _status.Text = "启动失败：" + exception.Message; }
    }

    private async Task ShowBuiltInWindowAsync(DesktopToolEntry tool)
    {
        Window window = tool.Id switch
        {
            "util.regex.tester" => new RegexTesterWindow(),
            _ => new CodecWorkbenchWindow(ToolCatalogPresentation.PreferredCodecOperation(tool.Id))
        };
        if (TopLevel.GetTopLevel(this) is Window owner) await window.ShowDialog(owner);
        else window.Show();
    }

    private void NoteLaunched(string toolId)
    {
        _settings.Update(value =>
            value.SecurityTools.RecentToolIds =
            [
                .. ToolCatalogService.NormalizeRecentIds(value.SecurityTools.RecentToolIds, toolId)
            ], SettingsSection.Security);
    }

    private IReadOnlyList<string> ReadRecentIds() => _settings.Load().SecurityTools.RecentToolIds ?? [];

    private bool IsCategoryExpanded(string category) =>
        _categoryExpansion.TryGetValue(category, out var expanded) && expanded;

    private void SetCategoryExpanded(string category, bool expanded)
    {
        _categoryExpansion[category] = expanded;
        _settings.Update(value =>
        {
            value.SecurityTools.ToolCategoryExpansion ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            value.SecurityTools.ToolCategoryExpansion[category] = expanded;
        }, SettingsSection.Security);
        RebuildNavTools();
    }

    private async Task CopyToClipboardAsync(string path)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(path);
        _status.Text = "路径已复制。";
    }

    private void OpenContainingFolder(DesktopToolEntry tool)
    {
        try
        {
            var directory = Path.GetDirectoryName(tool.Path!);
            if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("该工具没有可打开的目录。");
            _launcher.OpenDocument(directory);
            _status.Text = "已打开所在目录。";
        }
        catch (Exception exception) { _status.Text = "打开目录失败：" + exception.Message; }
    }

    private static IBrush? ResourceBrush(string key) =>
        Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true
            ? value as IBrush
            : null;
}

/// <summary>目录过滤的纯函数：把查询拆成空格分隔的词，全部命中（名称/描述/分类/id）才算匹配。</summary>
internal static class ToolSearchFilter
{
    public static bool Matches(DesktopToolEntry tool, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var haystack = string.Concat(tool.Name, '\n', tool.Description, '\n', tool.Category, '\n', tool.Id);
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!haystack.Contains(token, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

internal sealed record ToolDisplayGroup(string Category, IReadOnlyList<DesktopToolEntry> Tools);

internal static class ToolCatalogPresentation
{
    public const string NotIntegratedCategory = "未接入工具";

    public static IReadOnlyList<ToolDisplayGroup> Group(IReadOnlyList<DesktopToolEntry> tools)
    {
        var connected = tools.Where(tool => tool.Availability != DesktopToolAvailability.NotIntegrated)
            .GroupBy(tool => tool.Category)
            .Select(group => new ToolDisplayGroup(group.Key, group.ToArray()))
            .ToList();
        var notIntegrated = tools.Where(tool => tool.Availability == DesktopToolAvailability.NotIntegrated).ToArray();
        if (notIntegrated.Length > 0)
            connected.Add(new ToolDisplayGroup(NotIntegratedCategory, notIntegrated));
        return connected;
    }

    public static string? StatusLabel(DesktopToolEntry tool) => tool.Availability switch
    {
        DesktopToolAvailability.Unverified => "未验证",
        DesktopToolAvailability.NotIntegrated => "未接入",
        DesktopToolAvailability.Missing => "缺失",
        DesktopToolAvailability.DependencyMissing => "缺依赖",
        DesktopToolAvailability.Invalid => "无效",
        _ => null
    };

    public static string? PreferredCodecOperation(string toolId) => toolId switch
    {
        "crypto.radix" => "二进制 → 十进制",
        "crypto.jwt" => "JWT 解码",
        "web.url.parse" => "URL 结构解析",
        "util.timestamp" => "时间戳 ↔ 日期",
        _ => null
    };
}

public sealed class SecurityToolsSettingsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ToolCatalogService _catalog;
    private readonly TextBox _primaryToolRoot = new();
    private readonly TextBox _secondaryToolRoot = new();
    private readonly ComboBox _terminalMode = new();
    private readonly TextBox _wslDistribution = new();
    private readonly TextBox _workingDirectory = new();
    private readonly NumericUpDown _timeout = new() { Minimum = 10, Maximum = 120, Increment = 10 };
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _detectionResults = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        MinHeight = 120,
        MaxHeight = 190,
        FontSize = 11
    };
    private static readonly TerminalChoice[] TerminalChoices =
    [
        new("Auto", "自动（优先 Windows Terminal）"), new("WindowsTerminal", "Windows Terminal"),
        new("PowerShell", "PowerShell"), new("CommandPrompt", "命令提示符")
    ];

    public SecurityToolsSettingsWindow(ISettingsService settings, ToolCatalogService catalog)
    {
        _settings = settings; _catalog = catalog; Title = "安全工具设置"; Width = 620; Height = 680; MinWidth = 520; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _terminalMode.ItemsSource = TerminalChoices;
        var value = settings.Load().SecurityTools;
        _primaryToolRoot.Text = value.PrimaryToolRoot;
        _secondaryToolRoot.Text = value.SecondaryToolRoot;
        _terminalMode.SelectedItem = TerminalChoices.First(choice => choice.Value == value.TerminalMode);
        _wslDistribution.Text = value.WslDistribution; _workingDirectory.Text = value.WorkingDirectory; _timeout.Value = value.DefaultTimeoutSeconds;
        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock { Text = "内置工具优先使用应用目录中的 tools；本机工具从下面两个授权目录解析。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "本机工具目录", FontSize = 16, FontWeight = FontWeight.SemiBold });
        form.Children.Add(PathField("主工具目录", _primaryToolRoot));
        form.Children.Add(PathField("次工具目录", _secondaryToolRoot));
        form.Children.Add(new TextBlock { Text = "执行环境", FontSize = 16, FontWeight = FontWeight.SemiBold });
        form.Children.Add(Field("默认 Windows 终端", _terminalMode)); form.Children.Add(Field("WSL 发行版（Linux 工具使用，留空为默认）", _wslDistribution));
        form.Children.Add(Field("工具工作目录（留空使用工具自身目录）", _workingDirectory)); form.Children.Add(Field("默认超时（秒）", _timeout));
        form.Children.Add(_status);
        form.Children.Add(Field("检测结果（状态 / 工具 / 最终路径）", _detectionResults));
        var cancel = new Button { Content = "取消", MinWidth = 80 }; cancel.Click += (_, _) => Close(false);
        var detect = new Button { Content = "保存并重新检测", MinWidth = 120 }; detect.Click += async (_, _) => await SaveAsync(closeAfterSave: false);
        var save = new Button { Content = "保存", MinWidth = 80 }; save.Click += async (_, _) => await SaveAsync(closeAfterSave: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, detect, save } };
        var root = new Avalonia.Controls.DockPanel { Margin = new Thickness(18) };
        Avalonia.Controls.DockPanel.SetDock(actions, Avalonia.Controls.Dock.Bottom); root.Children.Add(actions); root.Children.Add(new ScrollViewer { Content = form }); return root;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (_terminalMode.SelectedItem is not TerminalChoice terminal) { _status.Text = "请选择默认终端。"; return; }
        var primaryRoot = (_primaryToolRoot.Text ?? string.Empty).Trim();
        var secondaryRoot = (_secondaryToolRoot.Text ?? string.Empty).Trim();
        if (!Directory.Exists(primaryRoot)) { _status.Text = "主工具目录不存在：" + primaryRoot; return; }
        if (!Directory.Exists(secondaryRoot)) { _status.Text = "次工具目录不存在：" + secondaryRoot; return; }
        if (!_settings.Update(value =>
        {
            value.SecurityTools.PrimaryToolRoot = primaryRoot; value.SecurityTools.SecondaryToolRoot = secondaryRoot;
            value.SecurityTools.TerminalMode = terminal.Value; value.SecurityTools.WslDistribution = (_wslDistribution.Text ?? string.Empty).Trim();
            value.SecurityTools.WorkingDirectory = (_workingDirectory.Text ?? string.Empty).Trim(); value.SecurityTools.DefaultTimeoutSeconds = (int)(_timeout.Value ?? 120);
        }, Hackermes.Platform.Events.SettingsSection.Security)) { _status.Text = "设置保存失败。"; return; }
        try
        {
            _status.Text = "正在重新检测工具…";
            await _catalog.RefreshAsync(_settings.Load().SecurityTools);
            var snapshot = _catalog.Snapshot;
            _detectionResults.Text = string.Join(Environment.NewLine, snapshot.Select(tool =>
                $"[{ToolCatalogPresentation.StatusLabel(tool) ?? "可用"}] {tool.Name} — {tool.Path ?? "应用内置"}"));
            _status.Text = $"检测完成：{snapshot.Count(tool => tool.Available)}/{snapshot.Count} 个入口可以启动。";
            if (closeAfterSave) Close(true);
        }
        catch (Exception exception) { _status.Text = "重新检测失败：" + exception.Message; }
    }

    private Control PathField(string label, TextBox editor)
    {
        var browse = new Button { Content = "浏览…", MinWidth = 72 };
        browse.Click += async (_, _) => await BrowseFolderAsync(editor, label);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(editor);
        Grid.SetColumn(browse, 1); row.Children.Add(browse);
        return Field(label, row);
    }

    private async Task BrowseFolderAsync(TextBox target, string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择" + title,
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) target.Text = path;
    }

    private sealed record TerminalChoice(string Value, string Label) { public override string ToString() => Label; }
    private static Control Field(string label, Control editor) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, editor } };
}

public sealed class CodecWorkbenchWindow : Window
{
    private readonly ComboBox _operation = new();
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130 };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130 };
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    internal static readonly CodecOperation[] Operations =
    [
        new("Base64 编码", text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))),
        new("Base64 解码", text => Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()))),
        new("URL 编码", Uri.EscapeDataString), new("URL 解码", Uri.UnescapeDataString),
        new("Hex 编码", text => Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant()),
        new("Hex 解码", text => Encoding.UTF8.GetString(Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal)))),
        new("SHA-256", text => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()),
        new("SHA-1", text => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()),
        new("MD5", text => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()),
        new("二进制 → 十进制", text => ConvertRadix(text, 2, 10)),
        new("八进制 → 十进制", text => ConvertRadix(text, 8, 10)),
        new("十进制 → 二进制", text => ConvertRadix(text, 10, 2)),
        new("十进制 → 八进制", text => ConvertRadix(text, 10, 8)),
        new("十进制 → 十六进制", text => ConvertRadix(text, 10, 16)),
        new("十六进制 → 十进制", text => ConvertRadix(text, 16, 10)),
        new("JWT 解码", DecodeJwt),
        new("URL 结构解析", ParseUrlStructure),
        new("时间戳 ↔ 日期", ConvertTimestamp)
    ];

    public CodecWorkbenchWindow(string? preferredOperationName = null)
    {
        Title = "编码与哈希工作台"; Width = 680; Height = 610; MinWidth = 520; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _operation.ItemsSource = Operations;
        _operation.SelectedItem = Operations.FirstOrDefault(operation => operation.Name == preferredOperationName) ?? Operations[0];
        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(Field("操作", _operation)); form.Children.Add(Field("输入", _input)); form.Children.Add(Field("输出", _output)); form.Children.Add(_status);
        var run = new Button { Content = "执行", MinWidth = 80 }; run.Click += (_, _) => Execute();
        var swap = new Button { Content = "输出转为输入", MinWidth = 100 }; swap.Click += (_, _) => { _input.Text = _output.Text; _output.Text = string.Empty; };
        var copy = new Button { Content = "复制结果", MinWidth = 90 }; copy.Click += async (_, _) => await CopyAsync();
        var close = new Button { Content = "关闭", MinWidth = 80 }; close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { swap, copy, close, run } };
        var root = new Avalonia.Controls.DockPanel { Margin = new Thickness(18) }; Avalonia.Controls.DockPanel.SetDock(actions, Avalonia.Controls.Dock.Bottom); root.Children.Add(actions); root.Children.Add(new ScrollViewer { Content = form }); return root;
    }

    private void Execute()
    {
        try { _status.Text = string.Empty; _output.Text = (_operation.SelectedItem as CodecOperation ?? Operations[0]).Transform(_input.Text ?? string.Empty); }
        catch (Exception exception) { _status.Text = "转换失败：" + exception.Message; }
    }

    private async Task CopyAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) { await clipboard.SetTextAsync(_output.Text ?? string.Empty); _status.Text = "结果已复制。"; }
    }

    internal static string DecodeJwt(string text)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length is < 2 or > 3)
            throw new InvalidDataException("不是有效的 JWT：需要 2~3 段以点分隔的 base64url。");
        var header = PrettyJson(DecodeBase64Url(parts[0]));
        var payload = PrettyJson(DecodeBase64Url(parts[1]));
        var signature = parts.Length == 3 ? parts[2] : "（无签名段）";
        return $"HEADER:\n{header}\n\nPAYLOAD:\n{payload}\n\nSIGNATURE:\n{signature}";
    }

    internal static string DecodeBase64Url(string segment)
    {
        var padded = segment.Trim().Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=')));
    }

    internal static string PrettyJson(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    internal static string ParseUrlStructure(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidDataException("无法解析为绝对 URL。");
        var builder = new StringBuilder();
        builder.Append("协议: ").AppendLine(uri.Scheme);
        builder.Append("主机: ").AppendLine(uri.Host);
        builder.Append("端口: ").AppendLine(uri.IsDefaultPort ? "(默认)" : uri.Port.ToString());
        if (!string.IsNullOrEmpty(uri.UserInfo)) builder.Append("用户信息: ").AppendLine(uri.UserInfo);
        builder.Append("路径: ").AppendLine(Uri.UnescapeDataString(uri.AbsolutePath));
        builder.Append("查询: ").AppendLine(string.IsNullOrEmpty(uri.Query) ? "(无)" : uri.Query);
        if (!string.IsNullOrEmpty(uri.Fragment)) builder.Append("片段: ").AppendLine(uri.Fragment);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            builder.AppendLine("\n查询参数:");
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                builder.Append("  ").Append(Uri.UnescapeDataString(key)).Append(" = ")
                    .AppendLine(Uri.UnescapeDataString(value));
            }
        }
        return builder.ToString().TrimEnd();
    }

    internal static string ConvertTimestamp(string text)
    {
        var trimmed = text.Trim();
        var digits = trimmed.StartsWith('-') ? trimmed[1..] : trimmed;
        if (digits.All(char.IsDigit) && digits.Length > 0)
        {
            var value = long.Parse(trimmed);
            var offset = digits.Length >= 13
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return $"UTC:   {offset.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n本地:  {offset.LocalDateTime:yyyy-MM-dd HH:mm:ss}\nISO:   {offset:O}";
        }
        if (!DateTimeOffset.TryParse(trimmed, out var parsed))
            throw new InvalidDataException("无法识别的时间戳：请输入 Unix 秒/毫秒数字或可解析的日期文本。");
        return $"Unix 秒:  {parsed.ToUnixTimeSeconds()}\nUnix 毫秒: {parsed.ToUnixTimeMilliseconds()}";
    }

    private static string ConvertRadix(string text, int sourceRadix, int targetRadix) =>
        Convert.ToString(Convert.ToInt64(text.Trim(), sourceRadix), targetRadix) ?? string.Empty;
    private static Control Field(string label, Control editor) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, editor } };
}

internal sealed record CodecOperation(string Name, Func<string, string> Transform)
{
    public override string ToString() => Name;
}

/// <summary>进程内正则测试器：零外部依赖，输出匹配列表与分组内容。</summary>
public sealed class RegexTesterWindow : Window
{
    private const int MaxListedMatches = 200;

    private readonly TextBox _pattern = new() { PlaceholderText = "正则表达式（例如 (?i)password\\s*=\\s*\\S+ ）", FontSize = 13 };
    private readonly CheckBox _ignoreCase = new() { Content = "忽略大小写", IsChecked = true };
    private readonly CheckBox _multiline = new() { Content = "多行模式 (^ $ 匹配行边界)", IsChecked = true };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140 };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 170 };
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    public RegexTesterWindow()
    {
        Title = "正则测试器"; Width = 720; Height = 640; MinWidth = 560; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _ignoreCase, _multiline } };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(Field("模式", _pattern));
        form.Children.Add(options);
        form.Children.Add(Field("文本", _input));
        form.Children.Add(Field("匹配结果", _output));
        form.Children.Add(_status);
        var run = new Button { Content = "执行", MinWidth = 80 }; run.Click += (_, _) => Execute();
        var close = new Button { Content = "关闭", MinWidth = 80 }; close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { close, run } };
        var root = new Avalonia.Controls.DockPanel { Margin = new Thickness(18) };
        Avalonia.Controls.DockPanel.SetDock(actions, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = form });
        return root;
    }

    private void Execute()
    {
        try
        {
            _status.Text = string.Empty;
            _output.Text = RunRegex(_pattern.Text ?? string.Empty, _input.Text ?? string.Empty,
                _ignoreCase.IsChecked == true, _multiline.IsChecked == true, out var summary);
            _status.Foreground = Brushes.Gray;
            _status.Text = summary;
        }
        catch (Exception exception)
        {
            _status.Foreground = Brushes.IndianRed;
            _status.Text = "执行失败：" + exception.Message;
        }
    }

    internal static string RunRegex(string pattern, string text, bool ignoreCase, bool multiline,
        out string summary)
    {
        summary = string.Empty;
        if (pattern.Length == 0) throw new InvalidDataException("请先填写正则表达式。");
        var options = RegexOptions.None |
            (ignoreCase ? RegexOptions.IgnoreCase : 0) |
            (multiline ? RegexOptions.Multiline : 0);
        var regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        var matches = regex.Matches(text);
        summary = $"共 {matches.Count} 处匹配。";
        if (matches.Count == 0) return "(无匹配)";

        var builder = new StringBuilder();
        for (var index = 0; index < Math.Min(matches.Count, MaxListedMatches); index++)
        {
            var match = matches[index];
            builder.Append('[').Append(index + 1).Append("] @").Append(match.Index)
                .Append("..").Append(match.Index + match.Length)
                .Append(": ").AppendLine(match.Value);
            for (var group = 1; group < match.Groups.Count; group++)
                if (match.Groups[group].Success)
                    builder.Append("    组").Append(regex.GroupNameFromNumber(group)).Append(": ")
                        .AppendLine(match.Groups[group].Value);
        }
        if (matches.Count > MaxListedMatches)
            builder.AppendLine($"… 其余 {matches.Count - MaxListedMatches} 处未列出。");
        return builder.ToString().TrimEnd();
    }

    private static Control Field(string label, Control editor) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, editor } };
}
