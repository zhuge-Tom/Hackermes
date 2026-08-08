using Avalonia.Controls;
using Avalonia.Interactivity;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.ViewModels;
using Hackermes.Platform.Services;

namespace Hackermes.AiPanel.Views;

public partial class AiChatView : UserControl
{
    private readonly ISettingsService? _settings;
    private readonly ISecretStore? _secrets;
    private readonly OpenAiCompatibleClient? _client;
    private readonly DefaultToolPolicyGate? _policy;

    public AiChatView() => InitializeComponent();

    public AiChatView(ISettingsService settings, ISecretStore secrets, OpenAiCompatibleClient client, DefaultToolPolicyGate policy)
    {
        _settings = settings; _secrets = secrets; _client = client; _policy = policy;
        InitializeComponent();
    }

    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_settings is null || _secrets is null || _client is null || _policy is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new AiSettingsWindow(_settings, _secrets, _client, _policy);
        if (await dialog.ShowDialog<bool>(owner) && DataContext is AiChatViewModel viewModel)
            viewModel.Model = dialog.SavedModel;
    }
}
