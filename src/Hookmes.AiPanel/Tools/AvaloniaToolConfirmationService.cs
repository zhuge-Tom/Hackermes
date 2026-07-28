using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.AiPanel.Tools;

/// <summary>默认保守策略的交互确认窗口。关闭窗口等同拒绝。</summary>
public sealed class AvaloniaToolConfirmationService : IToolConfirmationService
{
    public async ValueTask<ToolConfirmation> ConfirmAsync(
        ToolInvocation invocation, string reason, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<ToolConfirmation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Window? dialog = null;
        using var registration = ct.Register(() =>
        {
            completion.TrySetCanceled(ct);
            Dispatcher.UIThread.Post(() => dialog?.Close());
        });

        Dispatcher.UIThread.Post(() => dialog = ShowDialog(invocation, reason, completion, ct));
        return await completion.Task.ConfigureAwait(false);
    }

    private static Window? ShowDialog(
        ToolInvocation invocation,
        string reason,
        TaskCompletionSource<ToolConfirmation> completion,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested || completion.Task.IsCompleted) return null;
        var remember = new CheckBox { Content = "本会话记住此类操作" };
        var approve = new Button { Content = "允许", MinWidth = 80 };
        var reject = new Button { Content = "拒绝", MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { reject, approve }
        };
        var dialog = new Window
        {
            Title = "确认 AI 操作",
            Width = 460,
            Height = 250,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"AI 请求执行：{invocation.ToolName}", FontSize = 16 },
                    new TextBlock { Text = reason, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = invocation.Arguments.ToString(), MaxHeight = 70,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 },
                    remember,
                    buttons
                }
            }
        };

        void Finish(bool allowed)
        {
            completion.TrySetResult(new ToolConfirmation(allowed, allowed && remember.IsChecked == true));
            dialog.Close();
        }

        approve.Click += (_, _) => Finish(true);
        reject.Click += (_, _) => Finish(false);
        dialog.Closed += (_, _) => completion.TrySetResult(new ToolConfirmation(false));

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
            _ = dialog.ShowDialog(owner);
        else
            completion.TrySetResult(new ToolConfirmation(false));
        return dialog;
    }
}
