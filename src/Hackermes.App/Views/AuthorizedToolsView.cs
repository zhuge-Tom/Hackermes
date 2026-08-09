using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Hackermes.App;
using Hackermes.Platform.Models;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Hackermes.App.Views;

public sealed class AuthorizedToolsView : UserControl, ITabActivationAware
{
    private readonly ISettingsService _settings;
    private readonly ToolLaunchService _launcher;
    private TextBlock _status = CreateStatus();

    public AuthorizedToolsView(ISettingsService settings, ToolLaunchService launcher)
    {
        _settings = settings; _launcher = launcher; Content = BuildContent();
    }

    public void OnTabActivated() => Content = BuildContent();

    public void RefreshCatalog() => Content = BuildContent();

    private Control BuildContent()
    {
        // A refreshed view gets a fresh visual. Reusing the previous TextBlock
        // would attach the same Avalonia control to two different parents.
        _status = CreateStatus();
        var groups = new StackPanel { Spacing = 8 };
        foreach (var group in DesktopToolCatalog.Describe(_settings.Load().SecurityTools).GroupBy(tool => tool.Category))
        {
            var tools = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 2) };
            foreach (var tool in group)
            {
                var detail = new StackPanel { Spacing = 1 };
                detail.Children.Add(new TextBlock { Text = tool.Name, FontWeight = FontWeight.SemiBold });
                detail.Children.Add(new TextBlock { Text = tool.Description, FontSize = 11, Opacity = .68, TextWrapping = TextWrapping.Wrap });
                if (!tool.Available)
                {
                    detail.Children.Add(new TextBlock
                    {
                        Text = tool.UnavailableReason ?? "未内置",
                        FontSize = 10,
                        Opacity = .55,
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                var launch = new Button
                {
                    Content = detail, IsEnabled = tool.Available, Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0), Padding = new Thickness(8, 7),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                launch.Click += async (_, _) => await LaunchAsync(tool);
                tools.Children.Add(launch);
            }
            groups.Children.Add(new Expander
            {
                Header = new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold },
                IsExpanded = false, Content = tools
            });
        }
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        body.Children.Add(groups); body.Children.Add(new Separator()); body.Children.Add(_status);
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private static TextBlock CreateStatus() =>
        new() { FontSize = 11, Opacity = .68, TextWrapping = TextWrapping.Wrap };

    private async Task LaunchAsync(DesktopToolEntry tool)
    {
        try
        {
            _status.Text = string.Empty;
            if (tool.Kind == DesktopToolKind.BuiltIn)
            {
                var window = new CodecWorkbenchWindow();
                if (TopLevel.GetTopLevel(this) is Window owner) await window.ShowDialog(owner); else window.Show();
            }
            else if (tool.Kind == DesktopToolKind.Gui)
            {
                _launcher.LaunchGui(tool.Path!); _status.Text = $"已启动：{tool.Name}";
            }
            else if (tool.Kind == DesktopToolKind.Shortcut)
            {
                _launcher.OpenDocument(tool.Path!); _status.Text = $"已打开：{tool.Name}";
            }
            else if (tool.Kind == DesktopToolKind.Batch)
            {
                _launcher.LaunchBatch(tool.Path!); _status.Text = $"已启动：{tool.Name}";
            }
            else if (tool.Kind == DesktopToolKind.TeachingTerminal)
            {
                _launcher.LaunchTeachingTerminal(tool, _settings.Load().SecurityTools);
                _status.Text = $"已打开教学终端：{tool.Name}";
            }
        }
        catch (Exception exception) { _status.Text = "启动失败：" + exception.Message; }
    }
}

public sealed class SecurityToolsSettingsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ComboBox _terminalMode = new();
    private readonly TextBox _wslDistribution = new();
    private readonly TextBox _workingDirectory = new();
    private readonly NumericUpDown _timeout = new() { Minimum = 10, Maximum = 120, Increment = 10 };
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private static readonly TerminalChoice[] TerminalChoices =
    [
        new("Auto", "自动（优先 Windows Terminal）"), new("WindowsTerminal", "Windows Terminal"),
        new("PowerShell", "PowerShell"), new("CommandPrompt", "命令提示符")
    ];

    public SecurityToolsSettingsWindow(ISettingsService settings)
    {
        _settings = settings; Title = "安全工具通用设置"; Width = 560; Height = 520; MinWidth = 480; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _terminalMode.ItemsSource = TerminalChoices;
        var value = settings.Load().SecurityTools;
        _terminalMode.SelectedItem = TerminalChoices.First(choice => choice.Value == value.TerminalMode);
        _wslDistribution.Text = value.WslDistribution; _workingDirectory.Text = value.WorkingDirectory; _timeout.Value = value.DefaultTimeoutSeconds;
        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock { Text = "工具由应用目录中的 tools 清单统一管理，不读取外部绝对路径。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "执行环境", FontSize = 16, FontWeight = FontWeight.SemiBold });
        form.Children.Add(Field("默认 Windows 终端", _terminalMode)); form.Children.Add(Field("WSL 发行版（Linux 工具使用，留空为默认）", _wslDistribution));
        form.Children.Add(Field("工具工作目录（留空使用工具自身目录）", _workingDirectory)); form.Children.Add(Field("默认超时（秒）", _timeout));
        form.Children.Add(_status);
        var cancel = new Button { Content = "取消", MinWidth = 80 }; cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "保存", MinWidth = 80 }; save.Click += (_, _) => Save();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } };
        var root = new Avalonia.Controls.DockPanel { Margin = new Thickness(18) };
        Avalonia.Controls.DockPanel.SetDock(actions, Avalonia.Controls.Dock.Bottom); root.Children.Add(actions); root.Children.Add(new ScrollViewer { Content = form }); return root;
    }

    private void Save()
    {
        if (_terminalMode.SelectedItem is not TerminalChoice terminal) { _status.Text = "请选择默认终端。"; return; }
        if (!_settings.Update(value =>
        {
            value.SecurityTools.TerminalMode = terminal.Value; value.SecurityTools.WslDistribution = (_wslDistribution.Text ?? string.Empty).Trim();
            value.SecurityTools.WorkingDirectory = (_workingDirectory.Text ?? string.Empty).Trim(); value.SecurityTools.DefaultTimeoutSeconds = (int)(_timeout.Value ?? 120);
        }, Hackermes.Platform.Events.SettingsSection.Security)) { _status.Text = "设置保存失败。"; return; }
        Close(true);
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
    private static readonly CodecOperation[] Operations =
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
        new("十六进制 → 十进制", text => ConvertRadix(text, 16, 10))
    ];

    public CodecWorkbenchWindow()
    {
        Title = "编码与哈希工作台"; Width = 680; Height = 610; MinWidth = 520; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _operation.ItemsSource = Operations; _operation.SelectedIndex = 0; Content = BuildContent();
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

    private sealed record CodecOperation(string Name, Func<string, string> Transform) { public override string ToString() => Name; }
    private static string ConvertRadix(string text, int sourceRadix, int targetRadix) =>
        Convert.ToString(Convert.ToInt64(text.Trim(), sourceRadix), targetRadix) ?? string.Empty;
    private static Control Field(string label, Control editor) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, editor } };
}
