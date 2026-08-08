using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Base.Mvvm;
using Hackermes.Dock.ViewModels;
using Hackermes.Platform.Events;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using System;
using System.Threading.Tasks;

namespace Hackermes.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IEventBus _eventBus;
    private readonly ISettingsService _settingsService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IAppLogger _logger;

    public MainWindowViewModel(
        DockLayoutViewModel dockLayout,
        IEventBus eventBus,
        ISettingsService settingsService,
        IWorkspaceService workspaceService,
        IAppLogger logger)
    {
        DockLayout = dockLayout;
        _eventBus = eventBus;
        _settingsService = settingsService;
        _workspaceService = workspaceService;
        _logger = logger.ForCategory(nameof(MainWindowViewModel));

        _isDarkMode = _settingsService.Load().General.IsDarkMode;

        SubscribeEvent<StatusMessageEvent>(_eventBus, OnStatusMessage);
        SubscribeEvent<ProjectOpenedEvent>(_eventBus, OnProjectOpened);
        SubscribeEvent<ProjectClosedEvent>(_eventBus, _ => OnProjectClosed());
    }

    public DockLayoutViewModel DockLayout { get; }

    /// <summary>由 View 注入 —— ViewModel 不认识 Window,选目录必须借 View 的 StorageProvider。</summary>
    public Func<string?, Task<string?>>? ShowOpenFolderDialog { get; set; }

    [ObservableProperty]
    private string _title = "Hackermes";

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _hasWorkspace;

    [ObservableProperty]
    private string _workspaceName = string.Empty;

    [ObservableProperty]
    private bool _isDarkMode;

    partial void OnIsDarkModeChanged(bool value)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;

        _settingsService.Update(s => s.General.IsDarkMode = value, SettingsSection.General);
        _eventBus.Publish(new ThemeChangedEvent(value));
    }

    [RelayCommand]
    private void ToggleTheme() => IsDarkMode = !IsDarkMode;

    /// <summary>新建浏览器标签页。只发事件 —— 宿主不需要认识浏览器模块。</summary>
    [RelayCommand]
    private void NewBrowserTab() => _eventBus.Publish(new OpenBrowserTabRequestedEvent());

    [RelayCommand]
    private void TogglePanel(string? region)
    {
        if (!Enum.TryParse<DockPosition>(region, ignoreCase: true, out var position))
            return;

        var panel = DockLayout.GetPanel(position);
        panel.IsVisible = !panel.IsVisible;
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (ShowOpenFolderDialog is null)
            return;

        var path = await ShowOpenFolderDialog("选择工作区目录");
        if (string.IsNullOrEmpty(path))
            return;

        OpenWorkspace(path);
    }

    private void OpenWorkspace(string path)
    {
        try
        {
            _workspaceService.Open(path);
            _settingsService.Update(s => s.General.LastProjectPath = path, SettingsSection.General);
        }
        catch (Exception ex)
        {
            _logger.Error($"打开工作区失败: {path}", ex);
            _eventBus.Publish(new StatusMessageEvent($"打开失败: {ex.Message}", StatusMessageKind.Error));
        }
    }

    /// <summary>
    /// 恢复上次工作区。刻意推迟到布局稳定之后执行 ——
    /// 打开工作区会触发一连串 store 建表,放在启动关键路径上会拖慢首屏。
    /// </summary>
    public void TryRestoreLastWorkspace()
    {
        var general = _settingsService.Load().General;

        if (!general.AutoOpenLastProject || string.IsNullOrEmpty(general.LastProjectPath))
            return;

        if (!System.IO.Directory.Exists(general.LastProjectPath))
        {
            _logger.Info($"上次的工作区已不存在,跳过恢复: {general.LastProjectPath}");
            return;
        }

        OpenWorkspace(general.LastProjectPath);
    }

    private void OnStatusMessage(StatusMessageEvent e) => StatusMessage = e.Message;

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        HasWorkspace = true;
        WorkspaceName = _workspaceService.Current?.Name ?? string.Empty;
        Title = $"{WorkspaceName} — Hackermes";
        StatusMessage = $"已打开 {e.Directory}";
    }

    private void OnProjectClosed()
    {
        HasWorkspace = false;
        WorkspaceName = string.Empty;
        Title = "Hackermes";
        StatusMessage = "就绪";
    }
}
