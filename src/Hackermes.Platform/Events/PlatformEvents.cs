using Hackermes.Platform.Registries;
using System;

namespace Hackermes.Platform.Events;

// ─────────────────────────────────────────────────────────────────────────────
// 共享事件词典。功能模块之间零项目引用,全靠这里的类型互相理解。
//
// 命名约定:
//   *RequestedEvent — 表达意图,可能被拒绝或忽略
//   *ChangedEvent   — 状态已经变了,通知既成事实
//   *Event          — 单纯的事实通知
//
// 需要携带可写结果(例如取消标志)的用 class,其余一律 record。
//
// 注意:EventBus 是同步派发的。从后台线程发布的事件,订阅方要自己切回 UI 线程。
// ─────────────────────────────────────────────────────────────────────────────

#region 工作区

/// <summary>
/// 工作区打开。这是整个数据层的枢纽 —— 各 store 订阅它拿到数据库路径并惰性建表。
/// </summary>
public sealed record ProjectOpenedEvent(string Directory, string DatabasePath);

public sealed record ProjectClosedEvent;

/// <summary>侧边栏文件监视器发出,已做防抖。</summary>
public sealed record WorkspaceFileChangedEvent(string FullPath, WorkspaceFileChangeKind Kind);

public enum WorkspaceFileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

#endregion

#region Dock 布局

/// <summary>向指定区域添加一个已经构造好的 Tab。同 Id 已存在时应激活而非重复添加。</summary>
public sealed record AddDockTabEvent(DockPosition Region, DockTabItemViewModel Tab);

public sealed record RemoveDockTabEvent(DockPosition Region, string TabId);

public sealed record UpdateDockTabTitleEvent(string TabId, string Title);

/// <summary>请求切换到某个 Tab。目标若是按需注册的隐藏 Tab,布局层会先补一个壳。</summary>
public sealed record SwitchDockTabRequestedEvent(DockPosition Region, string TabId);

/// <summary>
/// Tab 即将关闭。订阅方可置 <see cref="Cancel"/> 阻止(例如有未保存内容需先确认)。
/// 因为要携带可写字段,这里用 class 而非 record。
/// </summary>
public sealed class TabCloseRequestedEvent(DockPosition region, string tabId)
{
    public DockPosition Region { get; } = region;
    public string TabId { get; } = tabId;
    public bool Cancel { get; set; }
}

public sealed record TabClosedEvent(DockPosition Region, string TabId, object? Content);

public sealed record ActiveContentTabChangedEvent(string? TabId, string? Title);

/// <summary>请求新建浏览器标签页。发布方不需要认识浏览器模块。</summary>
public sealed record OpenBrowserTabRequestedEvent(string? Url = null);

/// <summary>
/// 页面内 Agent 的回传消息。
/// <para>
/// <paramref name="Kind"/> 取 net / storage / route / lifecycle,
/// <paramref name="PayloadJson"/> 是 Agent 发出的原始 JSON ——
/// 不在事件层解析成强类型,因为消费方各自关心的字段差别很大。
/// </para>
/// </summary>
public sealed record PageAgentMessageEvent(string PageId, string Kind, string? SubKind, string PayloadJson);

/// <summary>Reports a committed main-frame navigation so inspector views can discard stale page objects.</summary>
public sealed record BrowserPageNavigatedEvent(string PageId, string Url);

/// <summary>Requests the inspector-owned page picker for one browser tab.</summary>
public sealed record ElementPickerToggleRequestedEvent(string PageId, bool Enabled);

/// <summary>Reports whether the picker was installed or rejected by the inspected page.</summary>
public sealed record ElementPickerStateChangedEvent(string PageId, bool Enabled, string? Error = null);

/// <summary>Requests desktop/mobile viewport emulation for a browser page.</summary>
public sealed record BrowserDeviceModeToggleRequestedEvent(string PageId, bool Enabled);

/// <summary>Reports the result of applying browser device emulation.</summary>
public sealed record BrowserDeviceModeStateChangedEvent(string PageId, bool Enabled, string? ProfileName = null, string? Error = null);

public sealed record DockPanelVisibilityChangedEvent(DockPosition Region, bool IsVisible);

#endregion

#region 应用级

/// <summary>状态栏消息。这是本应用的 Toast 替代品。</summary>
public sealed record StatusMessageEvent(string Message, StatusMessageKind Kind = StatusMessageKind.Info);

public enum StatusMessageKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record ThemeChangedEvent(bool IsDarkMode);

/// <summary>设置整体保存完成。需要针对某一节做副作用的模块请订阅下面的分节事件。</summary>
public sealed record AppSettingsSavedEvent;

/// <summary>
/// 分节设置变更。统一成强类型分节通知,而不是"一个全局事件 + 各模块自己再发专用事件"
/// 的混合模型 —— 后者在新增设置项时很容易漏接线。
/// </summary>
public sealed record SettingsSectionChangedEvent(SettingsSection Section);

public enum SettingsSection
{
    General,
    Browser,
    Automation,
    Terminal,
    Ai,
    Security
}

#endregion
