using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hookmes.Terminal.ViewModels;
using System.Collections.Specialized;
using Hookmes.Platform.Services;

namespace Hookmes.Terminal.Views;

public partial class ConsoleReplView : UserControl, ITabContentReleasable
{
    private ScrollViewer? _scroll;
    private TextBox? _input;
    private ConsoleReplViewModel? _vm;

    public ConsoleReplView()
    {
        InitializeComponent();

        _scroll = this.FindControl<ScrollViewer>("PART_Scroll");
        _input = this.FindControl<TextBox>("PART_Input");

        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
            _vm.Lines.CollectionChanged -= OnLinesChanged;

        _vm = DataContext as ConsoleReplViewModel;

        if (_vm is not null)
            _vm.Lines.CollectionChanged += OnLinesChanged;
    }

    /// <summary>新输出后滚到底部。用 Background 优先级等布局完成,否则滚不到真正的底。</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _scroll?.ScrollToEnd(), DispatcherPriority.Background);

    /// <summary>
    /// 上下键翻历史。用 Tunnel 阶段处理,抢在 TextBox 的光标移动之前。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !ReferenceEquals(e.Source, _input))
            return;

        switch (e.Key)
        {
            case Key.Up:
                _vm.HistoryPrevious();
                MoveCaretToEnd();
                e.Handled = true;
                break;

            case Key.Down:
                _vm.HistoryNext();
                MoveCaretToEnd();
                e.Handled = true;
                break;
        }
    }

    private void MoveCaretToEnd()
    {
        if (_input is not null)
            _input.CaretIndex = _input.Text?.Length ?? 0;
    }

    public void ReleaseTabResources()
    {
        if (_vm is not null)
            _vm.Lines.CollectionChanged -= OnLinesChanged;

        _vm?.Dispose();
        _vm = null;
        DataContext = null;
    }
}
