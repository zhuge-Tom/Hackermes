using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Hackermes.Inspector.Views;

public partial class NetworkPanelView : UserControl
{
    public NetworkPanelView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
