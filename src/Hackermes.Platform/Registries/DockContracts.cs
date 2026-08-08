using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace Hackermes.Platform.Registries;

/// <summary>
/// 五个固定区域。区域是编译期常量,只有 Tab 是动态的 —— 不做可拖拽 Dock 树,
/// 可拖拽 Dock 树的复杂度与收益不成正比,故不提供。
/// </summary>
public enum DockPosition
{
    /// <summary>左侧:项目树 / 元素树 / 脚本库</summary>
    Left,

    /// <summary>右侧:AI 面板</summary>
    Right,

    /// <summary>底部:网络 / 控制台 / 存储 / 时间线 / 终端</summary>
    Bottom,

    /// <summary>中央:浏览器视图与编辑器</summary>
    Content,

    /// <summary>设置窗口内的页面</summary>
    Setting
}

public sealed class DockTabRegistration
{
    public required DockPosition Region { get; init; }
    public required string TabId { get; init; }
    public required string Title { get; init; }

    /// <summary>
    /// 内容工厂。<strong>不在注册时调用</strong> —— 启动只建标题壳,
    /// Tab 被选中或所在面板变可见时才物化,见 DockLayoutViewModel.EnsureTabMaterialized。
    /// </summary>
    public required Func<DockTabItemViewModel> CreateTab { get; init; }

    public string? IconKey { get; init; }
    public bool IsClosable { get; init; } = true;

    /// <summary>false 表示不进 Tab 栏,等待 <c>SwitchDockTabRequestedEvent</c> 时按需补壳。</summary>
    public bool IsDefaultVisible { get; init; } = true;

    /// <summary>同区域内的排序权重,小的在前。</summary>
    public int Order { get; init; }
}

public partial class DockTabItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private StreamGeometry? _icon;

    /// <summary>站点 favicon;有值时 Tab 头优先显示它,否则回退 <see cref="Icon"/>。</summary>
    [ObservableProperty]
    private IImage? _faviconImage;

    /// <summary>
    /// Tab 内容。两种形态:普通 <see cref="Control"/>(可重载),
    /// 或实现 <see cref="Services.INonReloadableTabShell"/> 的占位壳(叠层保活)。
    /// </summary>
    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private bool _isClosable = true;

    /// <summary>Tab 是否有未保存的更改,用于头部显示脏标记。</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// 关闭命令,由所属面板在建壳时注入。
    /// 放在 Tab 上而不是让模板去 RelativeSource 找面板,是因为 TabStrip 的项模板
    /// 处在独立的名称作用域里,向上查找既脆弱又难调试。
    /// </summary>
    [ObservableProperty]
    private ICommand? _closeCommand;
}

/// <summary>
/// Tab 内容可实现此接口,在该 Tab 激活时向 Dock 宿主上交一个工具栏控件。
/// 内容页只负责提供控件,<strong>挂载与卸载由宿主统一处理</strong> —— 避免同一控件出现双父级。
/// </summary>
public interface IDockTabToolPanelProvider
{
    Control? GetDockTabToolPanel();
}

/// <summary>Tab 被激活时需要做一次刷新的内容(例如表格重算列宽)可实现此接口。</summary>
public interface ITabActivationAware
{
    void OnTabActivated();
}

public interface IDockLayoutRegistry
{
    void RegisterTab(DockTabRegistration registration);
    IReadOnlyList<DockTabRegistration> GetRegistrations();
    IReadOnlyList<DockTabRegistration> GetRegistrationsForRegion(DockPosition region);
}

public sealed class SettingsPageEntry
{
    public required string Title { get; init; }
    public required string IconKey { get; init; }
    public required int Order { get; init; }
    public required Func<Control> CreateView { get; init; }
}

public interface ISettingsRegistry
{
    void Register(SettingsPageEntry entry);
    IReadOnlyList<SettingsPageEntry> GetPages();
}

public sealed class MenuItemEntry
{
    public required string Header { get; init; }

    /// <summary>顶层菜单路径,例如 "文件" / "视图"。</summary>
    public required string MenuPath { get; init; }

    public int MenuGroupOrder { get; init; }
    public int Order { get; init; }
    public ICommand? Command { get; init; }
    public object? CommandParameter { get; init; }
    public string? IconKey { get; init; }
    public string? InputGesture { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsCheckable { get; init; }
}

public interface IMenuRegistry
{
    void Register(MenuItemEntry entry);
    IReadOnlyList<MenuItemEntry> GetItems();
    IReadOnlyList<MenuItemEntry> GetItemsForMenu(string menuPath);
}
