using Avalonia.Controls;
using Avalonia.Layout;
using Hackermes.Automation.Timeline;
using Hackermes.Platform.Registries;
using System.Linq;

namespace Hackermes.Automation.Views;

/// <summary>统一展示人工、REPL、AI 与脚本动作的轻量时间线。</summary>
public sealed class TimelineView : UserControl, ITabActivationAware
{
    private readonly ActionTimelineStore _store;
    private readonly ListBox _list = new();
    private readonly TextBlock _summary = new() { Opacity = 0.65 };

    public TimelineView(ActionTimelineStore store)
    {
        _store = store;
        var refresh = new Button { Content = "刷新", HorizontalAlignment = HorizontalAlignment.Right };
        refresh.Click += (_, _) => Refresh();
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(8),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _summary, refresh }
                },
                _list
            }
        };
        Grid.SetRow(_list, 1);
        Refresh();
    }

    public void OnTabActivated() => Refresh();

    private void Refresh()
    {
        var entries = _store.Snapshot(last: 500);
        _summary.Text = $"共 {_store.Count} 条动作（显示最近 {entries.Count} 条）";
        _list.ItemsSource = entries.Reverse().Select(entry =>
            $"{entry.Timestamp:HH:mm:ss.fff}  {entry.Action.Origin,-6}  " +
            $"{(entry.Result.Success ? "✓" : "✗")}  {entry.Action.Describe()}" +
            (entry.Result.Success ? string.Empty : $" — {entry.Result.Error}"));
    }
}
