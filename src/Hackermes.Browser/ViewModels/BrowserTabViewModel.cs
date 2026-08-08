using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using System;
using System.Collections.Generic;

namespace Hackermes.Browser.ViewModels;

/// <summary>单个浏览器标签页的状态。</summary>
public sealed record BrowserDeviceProfile(string Name, int Width, int Height, double DeviceScaleFactor, bool Mobile);

public partial class BrowserTabViewModel : ViewModelBase
{
    public BrowserTabViewModel(string pageId, string initialUrl)
    {
        PageId = pageId;
        _addressText = initialUrl;
        _currentUrl = initialUrl;
        _selectedDeviceProfile = DeviceProfiles[0];
    }

    /// <summary>页面标识,同时是 CDP 会话在注册表中的键。</summary>
    public string PageId { get; }

    /// <summary>地址栏里的文本,可能是用户正在编辑的中间状态。</summary>
    [ObservableProperty]
    private string _addressText;

    /// <summary>页面实际所在的地址。</summary>
    [ObservableProperty]
    private string _currentUrl;

    [ObservableProperty]
    private string _title = "新标签页";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCdpReady;

    /// <summary>Page Agent 是否已装配。未装配时仍可用 CDP 只读能力,属于降级而非失效。</summary>
    [ObservableProperty]
    private bool _isAgentReady;

    [ObservableProperty]
    private string _statusText = "正在初始化…";

    /// <summary>
    /// 已收到的 <c>Network.responseReceived</c> 事件数。
    /// 这个数字持续增长就证明 CDP 事件泵在工作 —— 阶段 1 最直接的验收信号。
    /// </summary>
    [ObservableProperty]
    private int _networkEventCount;

    [ObservableProperty]
    private string _selfTestResult = string.Empty;

    [ObservableProperty]
    private bool _isElementPickerActive;

    [ObservableProperty]
    private bool _isDeviceMode;

    [ObservableProperty]
    private BrowserDeviceProfile _selectedDeviceProfile;

    public IReadOnlyList<BrowserDeviceProfile> DeviceProfiles { get; } =
    [
        new("Responsive mobile", 390, 844, 3, true),
        new("iPhone 14", 390, 844, 3, true),
        new("Pixel 7", 412, 915, 2.625, true),
        new("iPad Mini", 768, 1024, 2, true)
    ];

    /// <summary>CDP 自检:走一遍请求-响应通道并把结果显示出来。</summary>
    public event Action? SelfTestRequested;

    /// <summary>
    /// 导航请求。由 View 处理 —— 初次导航时 CDP 会话尚未建立,
    /// 只能走 WebView 自身的 Navigate。
    /// </summary>
    public event Action<string>? NavigateRequested;

    public event Action? ReloadRequested;
    public event Action? BackRequested;
    public event Action? ForwardRequested;
    public event Action? DevToolsRequested;
    public event Action<bool>? ElementPickerRequested;
    public event Action<BrowserDeviceProfile?>? DeviceEmulationRequested;

    [RelayCommand]
    private void Navigate()
    {
        var url = NormalizeUrl(AddressText);
        if (string.IsNullOrEmpty(url))
            return;

        AddressText = url;
        NavigateRequested?.Invoke(url);
    }

    [RelayCommand]
    private void Reload() => ReloadRequested?.Invoke();

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void Forward() => ForwardRequested?.Invoke();

    [RelayCommand]
    private void OpenDevTools() => DevToolsRequested?.Invoke();

    [RelayCommand]
    private void SelfTest() => SelfTestRequested?.Invoke();

    [RelayCommand]
    private void ToggleElementPicker()
    {
        IsElementPickerActive = !IsElementPickerActive;
        ElementPickerRequested?.Invoke(IsElementPickerActive);
    }

    [RelayCommand]
    private void ToggleDeviceMode()
    {
        SetDeviceMode(!IsDeviceMode);
    }

    partial void OnSelectedDeviceProfileChanged(BrowserDeviceProfile value)
    {
        if (IsDeviceMode) DeviceEmulationRequested?.Invoke(value);
    }

    public void SetElementPickerState(bool active) => IsElementPickerActive = active;

    public void SetDeviceMode(bool enabled)
    {
        IsDeviceMode = enabled;
        DeviceEmulationRequested?.Invoke(enabled ? SelectedDeviceProfile : null);
    }

    /// <summary>
    /// 补全用户输入。没有协议前缀时:看起来像域名或本地地址的补 https,
    /// 其余当作搜索词 —— 直接把 "hello world" 拼成 URL 只会得到一个导航错误。
    /// </summary>
    public static string NormalizeUrl(string? input)
    {
        var text = input?.Trim();

        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("edge://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var looksLikeHost = !text.Contains(' ')
                            && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));

        return looksLikeHost
            ? "https://" + text
            : "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
    }
}
