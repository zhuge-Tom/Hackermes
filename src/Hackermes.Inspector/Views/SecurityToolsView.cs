using Avalonia;
using Avalonia.Controls;

namespace Hackermes.Inspector.Views;

public sealed class SecurityToolsView : UserControl
{
    public SecurityToolsView()
    {
        var title = new TextBlock { Text = "Security Tools", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(12, 12, 12, 6) };
        var description = new TextBlock
        {
            Text = "Reserved for target reconnaissance and testing tools.\n\nPlanned: Nmap integration, DNS/HTTP reconnaissance, directory discovery, encoding helpers, and reusable scan profiles.\n\nPage inspection is available in the bottom DOM, Network, Console, and packet workbenches.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        var layout = new StackPanel();
        layout.Children.Add(title);
        layout.Children.Add(description);
        Content = layout;
    }
}
