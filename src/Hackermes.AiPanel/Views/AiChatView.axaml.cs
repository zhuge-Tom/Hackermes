using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.ViewModels;
using Hackermes.Platform.Services;
using System;

namespace Hackermes.AiPanel.Views;

public partial class AiChatView : UserControl
{
    private readonly ISettingsService? _settings;
    private readonly ISecretStore? _secrets;
    private readonly OpenAiCompatibleClient? _client;
    private readonly DefaultToolPolicyGate? _policy;
    private readonly IAgentSkillStore? _skills;
    private readonly IAgentMemoryStore? _memory;

    public AiChatView() => InitializeComponent();

    public AiChatView(ISettingsService settings, ISecretStore secrets, OpenAiCompatibleClient client, DefaultToolPolicyGate policy,
        IAgentSkillStore skills, IAgentMemoryStore memory)
    {
        _settings = settings; _secrets = secrets; _client = client; _policy = policy; _skills = skills; _memory = memory;
        InitializeComponent();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        // The TextBox class handler consumes Enter (AcceptsReturn) during BUBBLING, so an
        // XAML KeyDown instance handler never fires. Tunneling intercepts the key BEFORE
        // the TextBox sees it, letting us own the Enter/Ctrl+Enter contract.
        if (this.FindControl<TextBox>("PART_PromptInput") is { } prompt)
            prompt.AddHandler(InputElement.KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        // Some IMEs/platforms deliver Ctrl+Enter (and occasionally plain Enter) as LineFeed.
        var key = e.Key == Key.LineFeed ? Key.Enter : e.Key;
        if (key != Key.Enter || sender is not TextBox input) return;
        e.Handled = true;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0 || (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            var text = input.Text ?? string.Empty;
            var start = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, text.Length);
            var end = Math.Clamp(Math.Max(input.SelectionStart, input.SelectionEnd), start, text.Length);
            var newline = Environment.NewLine;
            input.Text = text[..start] + newline + text[end..];
            input.CaretIndex = start + newline.Length;
            return;
        }

        if (DataContext is AiChatViewModel viewModel && viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
    }

    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_settings is null || _secrets is null || _client is null || _policy is null || _skills is null || _memory is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new AiSettingsWindow(_settings, _secrets, _client, _policy, _skills);
        if (await dialog.ShowDialog<bool>(owner) && DataContext is AiChatViewModel viewModel)
            viewModel.Model = dialog.SavedModel;
    }

    private async void NewSession(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiChatViewModel viewModel) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = await PromptInputWindow.ShowAsync(owner, "新建会话", "会话名称",
            $"新会话 {DateTimeOffset.Now:MM-dd HH:mm}");
        if (name is null) return; // Cancelled: keep the current session untouched.
        viewModel.NewSessionCommand.Execute(name.Length == 0 ? null : name);
    }

    private async void RenameSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiChatViewModel viewModel) return;
        if ((sender as Avalonia.Controls.MenuItem)?.DataContext is not AgentSessionOption option) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = await PromptInputWindow.ShowAsync(owner, "重命名会话", "会话名称", option.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        viewModel.RenameSession(option.Id, name);
    }

    private void ForkSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiChatViewModel viewModel) return;
        if ((sender as Avalonia.Controls.MenuItem)?.DataContext is not AgentSessionOption option) return;
        // The fork opens immediately with the source's full transcript restored.
        if (!viewModel.ForkSession(option.Id)) return;
    }

    private async void DeleteSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiChatViewModel viewModel) return;
        if ((sender as Avalonia.Controls.MenuItem)?.DataContext is not AgentSessionOption option) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner, "删除会话", $"确定删除会话「{option.Name}」吗？该会话的消息与事件记录将一并清除，不可恢复。");
        if (!confirmed) return;
        viewModel.DeleteSession(option.Id);
    }

    private async void ClearSessionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiChatViewModel viewModel) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner, "清空全部会话", "确定清空所有会话吗？将删除全部会话的记录与消息，当前会话也会被重置为空会话，不可恢复。");
        if (!confirmed) return;
        viewModel.ClearSessions();
    }

    private async void ExportTranscript(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiChatViewModel viewModel) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var storage = top.StorageProvider;
        var markdown = viewModel.BuildTranscriptMarkdown();
        var suggested = $"hackermes-{viewModel.SessionLabel}-{DateTimeOffset.Now:yyyyMMdd-HHmm}.md";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            suggested = suggested.Replace(invalid, '_');
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出会话转录",
            SuggestedFileName = suggested,
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new Avalonia.Platform.Storage.FilePickerFileType("文本") { Patterns = ["*.txt"] },
            ],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(markdown);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_messagesSubscription is not null)
        {
            _messagesSubscription.CollectionChanged -= OnMessagesChanged;
            _messagesSubscription = null;
        }
        if (DataContext is AiChatViewModel viewModel && this.FindControl<ScrollViewer>("PART_TranscriptScroll") is { } scroll)
        {
            _messagesSubscription = viewModel.Messages;
            _messagesSubscription.CollectionChanged += OnMessagesChanged;
        }
    }

    private System.Collections.Specialized.INotifyCollectionChanged? _messagesSubscription;

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Keep the newest turn in view; cheap enough to always follow on a chat surface.
        if (this.FindControl<ScrollViewer>("PART_TranscriptScroll") is { } scroll
            && e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            scroll.ScrollToEnd();
    }
}
