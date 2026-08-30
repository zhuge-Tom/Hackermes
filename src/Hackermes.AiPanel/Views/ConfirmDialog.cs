using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Views;

/// <summary>Small modal yes/no confirmation used before destructive session actions.</summary>
public sealed class ConfirmDialog : Window
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmDialog(string title, string message, string confirmLabel = "删除")
    {
        Title = title;
        Width = 440;
        MinHeight = 160;
        MaxHeight = 160;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var confirm = new Button { Content = confirmLabel, MinWidth = 84 };
        confirm.Click += (_, _) => { _accepted = true; Close(); };
        var cancel = new Button { Content = "取消", MinWidth = 84 };
        cancel.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { cancel, confirm }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                actions
            }
        };

        Closed += (_, _) => _completion.TrySetResult(_accepted);
    }

    private bool _accepted;

    /// <summary>True when the user confirmed; false when cancelled or the window was dismissed.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmLabel = "删除")
    {
        var dialog = new ConfirmDialog(title, message, confirmLabel);
        await dialog.ShowDialog(owner);
        return await dialog._completion.Task;
    }
}
