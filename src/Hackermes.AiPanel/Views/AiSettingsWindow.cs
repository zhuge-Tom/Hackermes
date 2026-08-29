using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Linq;
using System.Threading;

namespace Hackermes.AiPanel.Views;

public sealed class AiSettingsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly OpenAiCompatibleClient _client;
    private readonly DefaultToolPolicyGate _policy;
    private readonly IAgentSkillStore _skills;

    private readonly ComboBox _provider = new();
    private readonly TextBox _endpoint = new() { PlaceholderText = "https://example.com/v1" };
    private readonly ComboBox _model = new() { PlaceholderText = "请先测试连接并获取模型" };
    private readonly TextBox _apiKey = new() { PasswordChar = '●' };
    private readonly ComboBox _permission = new();
    private readonly ComboBox _webProvider = new();
    private readonly TextBox _webApiKey = new() { PasswordChar = '●', PlaceholderText = "Brave 或 Serper 的搜索 API Key" };
    private readonly TextBox _nvdApiKey = new() { PasswordChar = '●', PlaceholderText = "可选，提升 NVD 查询限额" };
    private readonly NumericUpDown _rounds = new() { Minimum = 1, Maximum = 256, Increment = 1 };
    private readonly NumericUpDown _contextCharacters = new() { Minimum = 4_000, Maximum = 1_200_000, Increment = 10_000 };
    private readonly NumericUpDown _toolResultCharacters = new() { Minimum = 1_000, Maximum = 100_000, Increment = 1_000 };
    private readonly NumericUpDown _toolTimeout = new() { Minimum = 5, Maximum = 3_600, Increment = 5 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _testButton = new() { Content = "测试连接", MinWidth = 92 };

    public string SavedModel { get; private set; } = string.Empty;

    public AiSettingsWindow(
        ISettingsService settings,
        ISecretStore secrets,
        OpenAiCompatibleClient client,
        DefaultToolPolicyGate policy,
        IAgentSkillStore skills)
    {
        _settings = settings;
        _secrets = secrets;
        _client = client;
        _policy = policy;
        _skills = skills;

        Title = "AI 助手设置";
        Width = 650;
        Height = 760;
        MinWidth = 520;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _provider.ItemsSource = AiProviderPresets.All;
        _permission.ItemsSource = PermissionOption.All;
        _webProvider.ItemsSource = WebProviderOption.All;
        _provider.SelectionChanged += (_, _) => ApplyPreset();
        _testButton.Click += async (_, _) => await TestConnectionAsync();

        Content = BuildContent();
        LoadCurrent();
    }

    private Control BuildContent()
    {
        var tabs = new TabControl
        {
            ItemsSource = new object[]
            {
                new TabItem { Header = "模型与 API", Content = BuildApiPage() },
                new TabItem { Header = "Skills", Content = new AgentSkillSettingsView(_skills) }
            }
        };

        var cancel = new Button { Content = "取消", MinWidth = 80 };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "保存", MinWidth = 80 };
        save.Click += (_, _) => Save();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, save }
        };

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(tabs);
        return root;
    }

    private Control BuildApiPage()
    {
        var form = new StackPanel { Spacing = 9, Margin = new Thickness(4, 10, 4, 4) };
        form.Children.Add(new TextBlock
        {
            Text = "选择常用服务，或使用“自定义”填写任意 OpenAI 兼容 Base URL。Chat 与模型接口按 OpenAI 标准自动解析。",
            Opacity = .68,
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(Field("API 类型", _provider));
        form.Children.Add(Field("API URL", _endpoint));
        form.Children.Add(Field("API Key", _apiKey));
        form.Children.Add(new TextBlock
        {
            Text = "API Key 使用当前 Windows 用户的 DPAPI 加密保存，不写入 settings.json。",
            Opacity = .65,
            TextWrapping = TextWrapping.Wrap
        });

        var testRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _testButton, _status }
        };
        form.Children.Add(testRow);
        form.Children.Add(Field("可用模型", _model));
        form.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4), Background = Brushes.Gray, Opacity = .18 });
        form.Children.Add(new TextBlock { Text = "Agent 运行", FontWeight = FontWeight.SemiBold, FontSize = 15 });
        form.Children.Add(Field("最大工具轮数", _rounds));
        form.Children.Add(Field("权限模式", _permission));
        form.Children.Add(new TextBlock
        {
            Text = "请求批准：网络和修改操作会询问；帮我批准：仅风险操作询问；完全访问：允许已注册工具。不可恢复的破坏操作仍会被拒绝。",
            Opacity = .65,
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(Field("上下文上限（字符）", _contextCharacters));
        form.Children.Add(Field("单条工具结果上限（字符）", _toolResultCharacters));
        form.Children.Add(Field("工具调用超时（秒）", _toolTimeout));
        form.Children.Add(new TextBlock
        {
            Text = "上下文压缩与持久记忆由系统内部自动管理，不保存 API Key 或工具原始敏感数据。超长工具结果会在截断后附截断标记；超时调用会取消并把原因返回给模型。",
            Opacity = .65,
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4), Background = Brushes.Gray, Opacity = .18 });
        form.Children.Add(new TextBlock { Text = "联网情报", FontWeight = FontWeight.SemiBold, FontSize = 15 });
        form.Children.Add(Field("搜索方式", _webProvider));
        form.Children.Add(Field("搜索 API Key", _webApiKey));
        form.Children.Add(Field("NVD API Key", _nvdApiKey));
        form.Children.Add(new TextBlock
        {
            Text = "web_search 与 CVE 查询只取回资料（不执行任何内容）。未配置 Key 时搜索降级为内置浏览器驱动 Bing；两个 Key 均以 DPAPI 加密保存，不写入 settings.json。",
            Opacity = .65,
            TextWrapping = TextWrapping.Wrap
        });

        return new ScrollViewer { Content = form };
    }

    private static Control Field(string label, Control editor) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, editor }
    };

    private void LoadCurrent()
    {
        var value = _settings.Load().Ai;
        _provider.SelectedItem = AiProviderPresets.All.FirstOrDefault(p =>
            p.Endpoint.Length > 0 && value.Endpoint.TrimEnd('/').Equals(p.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            ?? AiProviderPresets.All.First(p => p.Id == "custom");

        // Selecting a preset updates the fields, so persisted values are restored afterwards.
        _endpoint.Text = value.Endpoint;
        SetModels([value.Model], value.Model);
        _apiKey.Text = _secrets.Get("ai.apiKey") ?? string.Empty;
        _permission.SelectedItem = PermissionOption.All.First(option => option.Mode == value.PermissionMode);
        _rounds.Value = value.MaxToolRounds;
        _contextCharacters.Value = value.MaxContextCharacters;
        _toolResultCharacters.Value = value.MaxToolResultCharacters;
        _toolTimeout.Value = value.ToolCallTimeoutSeconds;
        _webProvider.SelectedItem = WebProviderOption.All.First(option =>
            option.Id.Equals(value.WebSearchProvider, StringComparison.OrdinalIgnoreCase));
        _webApiKey.Text = _secrets.Get("ai.webSearchApiKey") ?? string.Empty;
        _nvdApiKey.Text = _secrets.Get("ai.nvdApiKey") ?? string.Empty;
    }

    private void ApplyPreset()
    {
        if (_provider.SelectedItem is not AiProviderPreset preset) return;
        if (preset.Id != "custom") _endpoint.Text = preset.Endpoint;
        SetModels([], null);
        SetStatus(string.Empty, false);
    }

    private async System.Threading.Tasks.Task TestConnectionAsync()
    {
        try
        {
            var modelsEndpoint = AiProviderPresets.ResolveModelsEndpoint((_endpoint.Text ?? string.Empty).Trim());
            _testButton.IsEnabled = false;
            _status.Foreground = Brushes.DodgerBlue;
            _status.Text = "正在连接并读取模型…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var previous = _model.SelectedItem as string;
            var models = await _client.ListModelsAsync(modelsEndpoint, EmptyToNull(_apiKey.Text), timeout.Token);
            if (models.Count == 0) throw new InvalidOperationException("连接成功，但服务没有返回可用模型。");
            SetModels(models, previous);
            SetStatus($"连接成功，已获取 {models.Count} 个模型。", true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("连接超时，请检查地址、网络或服务状态。", false);
        }
        catch (Exception exception)
        {
            SetStatus("连接失败：" + exception.Message, false);
        }
        finally
        {
            _testButton.IsEnabled = true;
        }
    }

    private void Save()
    {
        try
        {
            var endpoint = (_endpoint.Text ?? string.Empty).Trim().TrimEnd('/');
            var resolved = AiProviderPresets.ResolveChatEndpoint(endpoint);
            var model = RequiredModel();
            var rounds = (int)(_rounds.Value ?? 48);
            var contextCharacters = (int)(_contextCharacters.Value ?? 400_000);
            var permission = (_permission.SelectedItem as PermissionOption)?.Mode ?? AiPermissionMode.RequestApproval;

            _settings.Update(s =>
            {
                s.Ai.Endpoint = endpoint;
                s.Ai.ChatCompletionsPath = "/chat/completions";
                s.Ai.Model = model;
                s.Ai.TrustedMode = false;
                s.Ai.PermissionMode = permission;
                s.Ai.MaxToolRounds = rounds;
                s.Ai.MaxContextCharacters = contextCharacters;
                s.Ai.MaxToolResultCharacters = (int)(_toolResultCharacters.Value ?? 12_000);
                s.Ai.ToolCallTimeoutSeconds = (int)(_toolTimeout.Value ?? 120);
                s.Ai.WebSearchProvider = (_webProvider.SelectedItem as WebProviderOption)?.Id ?? "auto";
                // Memory is an internal invariant, no longer a user-facing toggle.
                s.Ai.MemoryEnabled = true;
                s.Ai.NvdApiKeyConfigured = !string.IsNullOrWhiteSpace(_nvdApiKey.Text);
            }, SettingsSection.Ai);
            _secrets.Set("ai.apiKey", _apiKey.Text);
            _secrets.Set("ai.webSearchApiKey", _webApiKey.Text);
            _secrets.Set("ai.nvdApiKey", _nvdApiKey.Text);
            _client.Endpoint = resolved;
            _client.ApiKey = EmptyToNull(_apiKey.Text);
            _policy.SetMode(permission);
            SavedModel = model;
            Close(true);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, false);
        }
    }

    private string RequiredModel()
    {
        var model = (_model.SelectedItem as string ?? string.Empty).Trim();
        if (model.Length == 0) throw new ArgumentException("请先测试连接并选择一个可用模型。");
        return model;
    }

    private void SetModels(System.Collections.Generic.IEnumerable<string> models, string? preferred)
    {
        var values = models.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        _model.ItemsSource = values;
        _model.SelectedItem = values.FirstOrDefault(value => string.Equals(value, preferred, StringComparison.Ordinal))
                              ?? values.FirstOrDefault();
    }

    private void SetStatus(string text, bool success)
    {
        _status.Text = text;
        _status.Foreground = success ? Brushes.SeaGreen : Brushes.IndianRed;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PermissionOption(AiPermissionMode Mode, string Label)
    {
        public static readonly PermissionOption[] All =
        [
            new(AiPermissionMode.RequestApproval, "请求批准（默认）"),
            new(AiPermissionMode.HelpApproval, "帮我批准"),
            new(AiPermissionMode.FullAccess, "完全访问权限（不再逐次确认）")
        ];

        public override string ToString() => Label;
    }

    private sealed record WebProviderOption(string Id, string Label)
    {
        public static readonly WebProviderOption[] All =
        [
            new("auto", "自动（有 Key 用 Brave API，否则内置浏览器）"),
            new("browser", "仅内置浏览器（降级方案，无需 Key）"),
            new("brave", "Brave Search API"),
            new("serper", "Serper（Google）API")
        ];

        public override string ToString() => Label;
    }
}
