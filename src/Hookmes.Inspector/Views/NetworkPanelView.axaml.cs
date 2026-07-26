using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Hookmes.Inspector.Views;

public partial class NetworkPanelView : UserControl
{
    public NetworkPanelView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
