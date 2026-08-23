using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hackermes.Inspector.Services;
using Hackermes.Inspector.ViewModels;
using System;

namespace Hackermes.Inspector.Views;

public sealed partial class DomInspectorView : UserControl
{
    private DomInspectorViewModel? _viewModel;

    public DomInspectorView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.NodeRevealRequested -= RevealNode;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as DomInspectorViewModel;
        if (_viewModel is not null) _viewModel.NodeRevealRequested += RevealNode;
    }

    private void RevealNode(DomTreeNodeViewModel node) =>
        Dispatcher.UIThread.Post(() => PART_DomTree.ScrollIntoView(node), DispatcherPriority.Loaded);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not DomInspectorViewModel viewModel) return;
        viewModel.FindNextCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDomNodePointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is Control { DataContext: DomTreeNodeViewModel node } && DataContext is DomInspectorViewModel viewModel)
            viewModel.PreviewNode(node);
    }
}

public sealed class StorageInspectorView : UserControl
{
    public StorageInspectorView() => Content = InspectorViewFactory.CreateGrid(
        [new DataGridTextColumn { Header = "Area", Width = new DataGridLength(110), Binding = new Binding(nameof(PageStorageItem.Area)) },
         new DataGridTextColumn { Header = "Key", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(PageStorageItem.Key)) },
         new DataGridTextColumn { Header = "Value", Width = new DataGridLength(2, DataGridLengthUnitType.Star), Binding = new Binding(nameof(PageStorageItem.Value)) }]);
}

public sealed class ResourceExplorerView : UserControl
{
    private ResourceExplorerViewModel? _viewModel;

    public ResourceExplorerView()
    {
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 0, 8, 0) };
        refresh.Bind(Button.CommandProperty, new Binding("RefreshCommand"));
        var open = new Button { Content = "Open in browser", Margin = new Thickness(0, 0, 6, 0) };
        open.Bind(Button.CommandProperty, new Binding("OpenSelectedResourceCommand"));
        var copy = new Button { Content = "Copy URL", Margin = new Thickness(0, 0, 6, 0) };
        copy.Bind(Button.CommandProperty, new Binding("CopySelectedResourceCommand"));
        var highlight = new Button { Content = "Highlight element" };
        highlight.Bind(Button.CommandProperty, new Binding("HighlightSelectedResourceCommand"));
        var toolbar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(6), Children = { refresh, open, copy, highlight } };
        var summary = new SelectableTextBlock { Margin = new Thickness(6, 0, 6, 6), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        summary.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(ResourceExplorerViewModel.SelectedSummary)));
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        grid.Bind(DataGrid.ItemsSourceProperty, new Binding(nameof(ResourceExplorerViewModel.Items)));
        grid.Bind(DataGrid.SelectedItemProperty, new Binding(nameof(ResourceExplorerViewModel.SelectedItem)) { Mode = BindingMode.TwoWay });
        grid.DoubleTapped += (_, _) => _viewModel?.OpenSelectedResourceCommand.Execute(null);
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Width = new DataGridLength(80), Binding = new Binding(nameof(PageResourceItem.Type)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Resource", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(PageResourceItem.Name)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Element", Width = new DataGridLength(110), Binding = new Binding(nameof(PageResourceItem.ElementSummary)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Bytes", Width = new DataGridLength(75), Binding = new Binding(nameof(PageResourceItem.TransferSize)) });
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        layout.Children.Add(toolbar);
        Grid.SetRow(summary, 1); layout.Children.Add(summary);
        Grid.SetRow(grid, 2); layout.Children.Add(grid);
        Content = layout;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CopyRequested -= CopyResource;
        }
        base.OnDataContextChanged(e);
        _viewModel = DataContext as ResourceExplorerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CopyRequested += CopyResource;
        }
    }

    private async void CopyResource(string url)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(url);
    }
}

internal static class InspectorViewFactory
{
    public static Control CreateGrid(DataGridColumn[] columns)
    {
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 0, 8, 0) };
        refresh.Bind(Button.CommandProperty, new Binding("RefreshCommand"));
        var status = new SelectableTextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(SelectableTextBlock.TextProperty, new Binding("Status"));
        var toolbar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(6), Children = { refresh, status } };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        grid.Bind(DataGrid.ItemsSourceProperty, new Binding("Items"));
        foreach (var column in columns) grid.Columns.Add(column);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(toolbar); Grid.SetRow(grid, 1); layout.Children.Add(grid);
        return layout;
    }
}
