using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Hackermes.Inspector.ViewModels;

namespace Hackermes.Inspector.Views;

public partial class TrafficWorkbenchView : UserControl
{
    public TrafficWorkbenchView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => InjectFileDialogs();
        InjectFileDialogs();
    }

    private void InjectFileDialogs()
    {
        if (DataContext is TrafficWorkbenchViewModel model)
            model.FileDialogs = InspectorStorageDialogs.Create(this);
    }
}
