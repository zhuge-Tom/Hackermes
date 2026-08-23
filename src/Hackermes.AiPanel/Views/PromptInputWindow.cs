using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Views;

/// <summary>Small modal text prompt used for naming or renaming AI chat sessions.</summary>
public sealed class PromptInputWindow : Window
{
    private readonly TextBox _input = new();
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cancelled = true;

    public PromptInputWindow(string title, string label, string? initialValue)
    {
        Title = title;
        Width = 440;
        MinHeight = 168;
        MaxHeight = 168;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var ok = new Button { Content = "确定", MinWidth = 84 };
        ok.Click += (_, _) => { _cancelled = false; Close(); };
        var cancel = new Button { Content = "取消", MinWidth = 84 };
        cancel.Click += (_, _) => Close();
        _input.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == global::Avalonia.Input.Key.Enter)
            {
                _cancelled = false;
                Close();
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { cancel, ok }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold },
                _input,
                actions
            }
        };

        if (!string.IsNullOrEmpty(initialValue)) _input.Text = initialValue;
        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
        Closed += (_, _) => _completion.TrySetResult(
            _cancelled ? null : (_input.Text ?? string.Empty).Trim());
    }

    /// <summary>Returns the entered text, an empty string when confirmed blank, or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(Window owner, string title, string label, string? initialValue)
    {
        var dialog = new PromptInputWindow(title, label, initialValue);
        await dialog.ShowDialog(owner);
        return await dialog._completion.Task;
    }
}
