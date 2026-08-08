using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Linq;

namespace Hackermes.AiPanel.Views;

public sealed class AiSettingsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly OpenAiCompatibleClient _client;
    private readonly DefaultToolPolicyGate _policy;
    private readonly ComboBox _provider = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _model = new();
    private readonly TextBox _apiKey = new() { PasswordChar = '●' };
    private readonly CheckBox _trusted = new() { Content = "信任模式（允许工具修改页面；请谨慎启用）" };
    private readonly NumericUpDown _rounds = new() { Minimum = 1, Maximum = 50, Increment = 1 };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public string SavedModel { get; private set; } = string.Empty;

    public AiSettingsWindow(ISettingsService settings, ISecretStore secrets, OpenAiCompatibleClient client, DefaultToolPolicyGate policy)
    {
        _settings = settings; _secrets = secrets; _client = client; _policy = policy;
        Title = "AI 助手设置"; Width = 560; Height = 520; MinWidth = 480; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _provider.ItemsSource = AiProviderPresets.All;
        _provider.SelectionChanged += (_, _) => ApplyPreset();
        Content = BuildContent();
        LoadCurrent();
    }

    private Control BuildContent()
    {
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(Field("API 类型", _provider));
        form.Children.Add(Field("API Endpoint", _endpoint));
        form.Children.Add(Field("模型名称", _model));
        form.Children.Add(Field("API Key", _apiKey));
        form.Children.Add(new TextBlock { Text = "API Key 使用当前 Windows 用户的 DPAPI 加密保存，不会写入 settings.json。", Opacity = .65, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        form.Children.Add(Field("最大工具轮数", _rounds));
        form.Children.Add(_trusted);
        form.Children.Add(_error);
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "保存", MinWidth = 80 };
        save.Click += (_, _) => Save();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } };
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = form });
        return root;
    }

    private static Control Field(string label, Control editor) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold }, editor }
    };

    private void LoadCurrent()
    {
        var value = _settings.Load().Ai;
        _provider.SelectedItem = AiProviderPresets.All.FirstOrDefault(p =>
            value.Endpoint.TrimEnd('/').Equals(p.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ?? AiProviderPresets.All[^1];
        // Selection applies a preset, so restore the user's persisted values afterwards.
        _endpoint.Text = value.Endpoint; _model.Text = value.Model;
        _apiKey.Text = _secrets.Get("ai.apiKey") ?? string.Empty;
        _trusted.IsChecked = value.TrustedMode; _rounds.Value = value.MaxToolRounds;
    }

    private void ApplyPreset()
    {
        if (_provider.SelectedItem is not AiProviderPreset preset || preset.Id == "custom") return;
        _endpoint.Text = preset.Endpoint; _model.Text = preset.DefaultModel;
    }

    private void Save()
    {
        try
        {
            var endpoint = (_endpoint.Text ?? string.Empty).Trim();
            var model = (_model.Text ?? string.Empty).Trim();
            if (model.Length == 0) throw new ArgumentException("模型名称不能为空。");
            var resolved = AiProviderPresets.ResolveChatEndpoint(endpoint);
            var rounds = (int)(_rounds.Value ?? 12);
            _settings.Update(s =>
            {
                s.Ai.Endpoint = endpoint.TrimEnd('/'); s.Ai.Model = model;
                s.Ai.TrustedMode = _trusted.IsChecked == true; s.Ai.MaxToolRounds = rounds;
            }, SettingsSection.Ai);
            _secrets.Set("ai.apiKey", _apiKey.Text);
            _client.Endpoint = resolved; _client.ApiKey = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text;
            _policy.SetTrustedMode(_trusted.IsChecked == true);
            SavedModel = model; Close(true);
        }
        catch (Exception exception) { _error.Text = exception.Message; }
    }
}
