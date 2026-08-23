using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Hackermes.App.ViewModels;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Dock.Controls;
using Hackermes.Dock.ViewModels;
using Hackermes.Platform.Services;
using System;
using System.ComponentModel;
using System.Linq;

namespace Hackermes.App.Views;

public partial class MainContentView : UserControl
{
    private Grid? _regionGrid;
    private Grid? _centerGrid;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;
    private GridSplitter? _bottomSplitter;
    private PersistTabControl? _bottomPanel;
    private BottomBrowserToolsView? _bottomBrowserTools;

    private MainWindowViewModel? _viewModel;

    // 折叠前的尺寸,展开时原样恢复。
    private double _leftWidth = 320;
    private double _rightWidth = 380;
    private double _bottomHeight = 260;

    public MainContentView()
    {
        InitializeComponent();

        _regionGrid = this.FindControl<Grid>("PART_RegionGrid");
        _centerGrid = this.FindControl<Grid>("PART_CenterGrid");
        _leftSplitter = this.FindControl<GridSplitter>("PART_LeftSplitter");
        _rightSplitter = this.FindControl<GridSplitter>("PART_RightSplitter");
        _bottomSplitter = this.FindControl<GridSplitter>("PART_BottomSplitter");
        _bottomPanel = this.FindControl<PersistTabControl>("PART_BottomPanel");

        if (_bottomPanel is not null
            && App.Services?.GetService(typeof(IEventBus)) is IEventBus eventBus)
        {
            _bottomBrowserTools = new BottomBrowserToolsView(eventBus);
            _bottomPanel.TabStripRightContent = _bottomBrowserTools;
        }

        // 窗口变窄时把两侧面板压回预算内,保住中央内容列的最小宽度(见 RegionLayout)。
        _regionGrid?.SizeChanged += (_, _) => ReclampVisibleSideColumns();

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Unsubscribe();
            _bottomBrowserTools?.Dispose();
            _bottomBrowserTools = null;
        };
        Loaded += (_, _) =>
        {
            // DataContextChanged 时 Bounds 还是 0,夹取无从谈起 —— 到这里才有真实尺寸。
            if (DataContext is MainWindowViewModel vm)
                ApplyAllPanelVisibility(vm.DockLayout);

            LogLayoutDiagnostics();
        };
    }

    private void LogLayoutDiagnostics()
    {
        if (App.Services?.GetService(typeof(IAppLogger)) is not IAppLogger logger)
            return;

        var log = logger.ForCategory("Layout");

        if (_regionGrid is null)
        {
            log.Warn("PART_RegionGrid 未找到 —— 面板折叠与尺寸恢复全部失效");
            return;
        }

        // Debug 级别:默认不落盘,排查布局问题时把日志阈值调低即可看到。
        var cols = string.Join(" | ", _regionGrid.ColumnDefinitions
            .Select((c, i) => $"[{i}] {c.Width} actual={c.ActualWidth:F0}"));

        log.Debug($"RegionGrid 宽度={_regionGrid.Bounds.Width:F0} 列: {cols}");

        if (_centerGrid is not null)
        {
            var rows = string.Join(" | ", _centerGrid.RowDefinitions
                .Select((r, i) => $"[{i}] {r.Height} actual={r.ActualHeight:F0}"));
            log.Debug($"CenterGrid 高度={_centerGrid.Bounds.Height:F0} 行: {rows}");
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        if (DataContext is not MainWindowViewModel vm)
            return;

        _viewModel = vm;

        var layout = vm.DockLayout;
        layout.Left.PropertyChanged += OnPanelPropertyChanged;
        layout.Right.PropertyChanged += OnPanelPropertyChanged;
        layout.Bottom.PropertyChanged += OnPanelPropertyChanged;

        RestoreSizesFromSettings();
        ApplyAllPanelVisibility(layout);
    }

    private void Unsubscribe()
    {
        if (_viewModel is null)
            return;

        var layout = _viewModel.DockLayout;
        layout.Left.PropertyChanged -= OnPanelPropertyChanged;
        layout.Right.PropertyChanged -= OnPanelPropertyChanged;
        layout.Bottom.PropertyChanged -= OnPanelPropertyChanged;

        _viewModel = null;
    }

    private void RestoreSizesFromSettings()
    {
        if (App.Services?.GetService(typeof(ISettingsService)) is not ISettingsService settings)
            return;

        var layout = settings.Load().Layout;
        _leftWidth = layout.LeftPanelWidth;
        _rightWidth = layout.RightPanelWidth;
        _bottomHeight = layout.BottomPanelHeight;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DockPanelViewModel.IsVisible))
            return;

        if (sender is DockPanelViewModel panel)
            ApplyPanelVisibility(panel);
    }

    private void ApplyAllPanelVisibility(DockLayoutViewModel layout)
    {
        ApplyPanelVisibility(layout.Left);
        ApplyPanelVisibility(layout.Right);
        ApplyPanelVisibility(layout.Bottom);
    }

    /// <summary>
    /// 折叠面板。
    /// <para>
    /// <strong>只改尺寸,不动 IsVisible。</strong>
    /// 被折叠的面板里可能驻留着 WebView2 或 PTY,把它们从可视树摘下来会直接销毁会话 ——
    /// 这正是叠层保活想避免的事。宽高归零在视觉上等价,代价却小得多。
    /// </para>
    /// </summary>
    private void ApplyPanelVisibility(DockPanelViewModel panel)
    {
        switch (panel.Position)
        {
            case Platform.Registries.DockPosition.Left:
                SetColumn(0, _leftSplitter, panel.IsVisible, ref _leftWidth);
                break;

            case Platform.Registries.DockPosition.Right:
                SetColumn(4, _rightSplitter, panel.IsVisible, ref _rightWidth);
                break;

            case Platform.Registries.DockPosition.Bottom:
                SetRow(2, _bottomSplitter, panel.IsVisible, ref _bottomHeight);
                break;
        }

        PersistSizes();
    }

    /// <summary>底部面板最多占中央区高度的这个比例。</summary>
    private const double MaxBottomPanelRatio = 0.45;

    private void SetColumn(int index, GridSplitter? splitter, bool visible, ref double remembered)
    {
        if (_regionGrid is null || index >= _regionGrid.ColumnDefinitions.Count)
            return;

        var definition = _regionGrid.ColumnDefinitions[index];

        if (visible)
        {
            // 记忆值可能来自更大的窗口(或更早的默认值),必须按当前窗口夹一次,
            // 否则换到小屏上会出现"两侧面板占满、中间只剩一条缝"。
            // 夹取同时保证中央内容列的最小宽度(RegionLayout),最小窗口下四个
            // 内容标签才能完整可见。
            var available = _regionGrid.Bounds.Width;
            var applied = available > 0
                ? RegionLayout.ClampSidePanelWidth(available, remembered)
                : remembered;

            definition.MinWidth = Math.Min(160, applied);
            definition.Width = new GridLength(applied, GridUnitType.Pixel);
        }
        else
        {
            if (definition.Width.IsAbsolute && definition.Width.Value > 1)
                remembered = definition.Width.Value;

            definition.MinWidth = 0;
            definition.Width = new GridLength(0, GridUnitType.Pixel);
        }

        if (splitter is not null)
            splitter.IsVisible = visible;
    }

    /// <summary>
    /// 窗口尺寸变化后把展开中的两侧面板压回当前预算。只改列宽,不动记忆值 ——
    /// 记忆值仍保存用户拖拽/设置给出的期望宽度,恢复大窗口时原样回来。
    /// </summary>
    private void ReclampVisibleSideColumns()
    {
        if (_regionGrid is null || _regionGrid.Bounds.Width <= 0)
            return;

        foreach (var (index, splitter) in new[] { (0, _leftSplitter), (4, _rightSplitter) })
        {
            if (splitter is null || !splitter.IsVisible)
                continue;

            var definition = _regionGrid.ColumnDefinitions[index];
            if (!definition.Width.IsAbsolute || definition.Width.Value <= 1)
                continue;

            var applied = RegionLayout.ClampSidePanelWidth(_regionGrid.Bounds.Width, definition.Width.Value);
            if (Math.Abs(applied - definition.Width.Value) < 0.5)
                continue;

            definition.MinWidth = Math.Min(160, applied);
            definition.Width = new GridLength(applied, GridUnitType.Pixel);
        }
    }

    private void SetRow(int index, GridSplitter? splitter, bool visible, ref double remembered)
    {
        if (_centerGrid is null || index >= _centerGrid.RowDefinitions.Count)
            return;

        var definition = _centerGrid.RowDefinitions[index];

        if (visible)
        {
            var available = _centerGrid.Bounds.Height;
            var applied = available > 0
                ? Math.Min(remembered, available * MaxBottomPanelRatio)
                : remembered;

            definition.MinHeight = Math.Min(100, applied);
            definition.Height = new GridLength(applied, GridUnitType.Pixel);
        }
        else
        {
            if (definition.Height.IsAbsolute && definition.Height.Value > 1)
                remembered = definition.Height.Value;

            definition.MinHeight = 0;
            definition.Height = new GridLength(0, GridUnitType.Pixel);
        }

        if (splitter is not null)
            splitter.IsVisible = visible;
    }

    private void PersistSizes()
    {
        if (App.Services?.GetService(typeof(ISettingsService)) is not ISettingsService settings)
            return;

        settings.Update(s =>
        {
            s.Layout.LeftPanelWidth = _leftWidth;
            s.Layout.RightPanelWidth = _rightWidth;
            s.Layout.BottomPanelHeight = _bottomHeight;
        });
    }
}
