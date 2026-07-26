using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Hookmes.Inspector.Views;

public partial class ConsolePanelView : UserControl
{
    public ConsolePanelView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
