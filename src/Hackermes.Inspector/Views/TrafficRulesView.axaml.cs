using Avalonia.Controls;
using Hackermes.Inspector.ViewModels;

namespace Hackermes.Inspector.Views;

public partial class TrafficRulesView : UserControl
{
    public TrafficRulesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => InjectFileDialogs();
        InjectFileDialogs();
    }

    private void InjectFileDialogs()
    {
        if (DataContext is TrafficRulesViewModel model)
            model.FileDialogs = InspectorStorageDialogs.Create(this);
    }
}
