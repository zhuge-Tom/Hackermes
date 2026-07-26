using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Hookmes.App.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hookmes.App.Views;

public partial class MainWindow : Window
{
    private Panel? _contentHost;
    private Border? _loadingOverlay;
    private TextBlock? _loadingText;

    public MainWindow()
    {
        InitializeComponent();

        _contentHost = this.FindControl<Panel>("PART_ContentHost");
        _loadingOverlay = this.FindControl<Border>("PART_LoadingOverlay");
        _loadingText = this.FindControl<TextBlock>("PART_LoadingText");

        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 按屏幕工作区调整窗口大小。
    /// <para>
    /// XAML 里的固定尺寸是逻辑像素,在高 DPI 屏上乘以缩放后可能超出可视区
    /// (例如 1440×900 在 125% 缩放下是 1800×1125),导致窗口显示不全。
    /// 这里取工作区的一定比例并夹住上限,同时避开任务栏。
    /// </para>
    /// </summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;

        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
                return;

            // WorkingArea 是物理像素,换算成 Avalonia 使用的逻辑像素。
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var availableWidth = screen.WorkingArea.Width / scaling;
            var availableHeight = screen.WorkingArea.Height / scaling;

            var width = Math.Min(Width, availableWidth * 0.9);
            var height = Math.Min(Height, availableHeight * 0.9);

            Width = Math.Max(MinWidth, width);
            Height = Math.Max(MinHeight, height);

            // 改过尺寸后重新居中,CenterScreen 只在初次定位时生效。
            Position = new PixelPoint(
                screen.WorkingArea.X + (int)((availableWidth - Width) * scaling / 2),
                screen.WorkingArea.Y + (int)((availableHeight - Height) * scaling / 2));
        }
        catch
        {
            // 拿不到屏幕信息就保持 XAML 里的默认尺寸。
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // ViewModel 不认识 Window,选目录这类需要 TopLevel 的能力由 View 注入。
            vm.ShowOpenFolderDialog = PickFolderAsync;
        }
    }

    public void AttachMainContent(Control content)
    {
        _contentHost ??= this.FindControl<Panel>("PART_ContentHost");
        _contentHost?.Children.Add(content);
    }

    public void HideLoadingOverlay()
    {
        _loadingOverlay ??= this.FindControl<Border>("PART_LoadingOverlay");

        if (_loadingOverlay is not null)
            _loadingOverlay.IsVisible = false;
    }

    /// <summary>装配失败时把原因留在遮罩上,而不是留给用户一个永远转圈的窗口。</summary>
    public void ShowStartupFailure(Exception exception)
    {
        _loadingText ??= this.FindControl<TextBlock>("PART_LoadingText");

        if (_loadingText is not null)
            _loadingText.Text = $"启动失败:{exception.Message}\n详情见 %LocalAppData%\\Hookmes\\logs\\crash.log";
    }

    private async Task<string?> PickFolderAsync(string? title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title ?? "选择目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        return folder?.TryGetLocalPath();
    }
}
