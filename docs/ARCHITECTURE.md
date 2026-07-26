# Hookmes 架构方案

> 本地桌面端网页调试自动化工具 — 交互式命令终端 + 内置浏览器 + 人工操作 + AI 辅助前端调试

参考项目:`G:\Oofo\zerofall`(烬 ZeroFall)。本文先说明与参考项目的定位差异,再给出分层、模块划分、目录结构与关键机制设计。

---

## 一、定位:Hookmes 不是 ZeroFall

ZeroFall 是**安全攻防工作台**,其浏览器部分的本质是**旁路观测**:流量从 Fluxzy 本地代理经过并入库,页面内容靠 CDP 只读拉取(`DOM.getDocument` / `getOuterHTML`)。它刻意不进入页面内部——`OpenSourceBrowserPolicy.ProxyOnlyTrafficCapture` 甚至把 CDP 抓包路径整个短路,626 行的 `BrowserAiToolService` 在开源构建里根本不可达。

Hookmes 是**前端调试自动化工作台**,本质是**进入页面内部并驱动它**。这决定了两者在能力上几乎不重叠:

| 维度 | ZeroFall | Hookmes |
|---|---|---|
| 网络数据来源 | Fluxzy MITM 代理(需装根证书) | CDP `Network` + `Fetch` 域(零证书配置) |
| 页面读取 | 只读 DOM 快照 → Markdown | DOM / 可访问性树 / 计算样式 / Storage / 覆盖率 |
| 页面写入 | 无 | `Input.dispatch*` 真实输入事件、`Runtime.evaluate` |
| 页面内驻留代码 | 无 | Page Agent(文档级预注入,hook fetch/XHR/WS/事件) |
| 页面→宿主通道 | 无(单向 pull) | `Runtime.addBinding` 双向 |
| 交互定位 | 无 | Overlay 高亮 + 元素拾取 + 选择器生成 |
| 自动化 | Intruder(HTTP 层爆破) | 录制/回放脚本(UI 层) |
| AI 的角色 | 分析已捕获的数据 | 直接驱动页面并观察结果 |

一句话:**ZeroFall 看流量,Hookmes 动页面。**

### 从 ZeroFall 继承什么

四份源码分析确认了一批经过实战打磨、值得直接继承的设计(详见 `docs/INHERITED-PATTERNS.md`):

1. **两段式 `IModule` + 注册表插件化** — 模块清单硬编码(不反射扫描),`RegisterServices` / `Initialize` 两趟循环。
2. **同步 EventBus + 共享事件词典** — 功能模块之间零项目引用。
3. **`PersistTabControl` 双模式 Tab 保活** — 切页不卸载可视树,否则 WebView2 与 PTY 会被销毁。这是 Hookmes 的刚需。
4. **`[AiTool]` 源生成器** — 从 C# 方法签名生成 OpenAI tool schema 与 DI 执行器闭包,零反射。
5. **Vite singlefile → C# raw string literal** — Web 前端零依赖烧进 .NET,产物入库,构建机不需要 Node。
6. **流式 Markdown 双段渲染** — 稳定块用 HTML、活跃尾用纯文本,避免半截语法闪烁。
7. **API 载荷从 SQLite 重建而非从 UI 内存拼** — 撤销、压缩、子 Agent 都成为同一份数据的不同投影。
8. **WebView2 创建互斥 + UI 线程脚本闸门** — 多个 WebView 共用 UI 线程,交叉调用会死锁。

### 明确改进什么

ZeroFall 的四个已知短板,在 Hookmes 里从一开始就避开:

| 问题 | ZeroFall 现状 | Hookmes 方案 |
|---|---|---|
| 包版本管理 | 无中央管理,十余个版本号手工重复 | `Directory.Packages.props` 中央包管理 |
| AI 工具安全闸门 | **完全没有**。AI 可无审批执行任意 shell 命令、ssh、输密码 | `IToolPolicyGate` 在唯一收口处强制策略,危险动作需确认 |
| 敏感信息存储 | API Key 明文存 JSON | Windows DPAPI(`ProtectedData` + `CurrentUser`) |
| 日志 | 自写静态类 + 散落的 `Debug.WriteLine` | `ILogger` 抽象 + 轻量文件 sink |

---

## 二、总体架构

### 2.1 分层

```
┌─────────────────────────────────────────────────────────────┐
│  Hookmes.App          宿主 / 启动装配 / 主窗口 / ViewLocator  │
├─────────────────────────────────────────────────────────────┤
│  功能模块层(互不引用,靠事件与注册表通信)                      │
│  Browser  Inspector  Automation  Terminal  AiPanel           │
│  Sidebar  Settings                                           │
├─────────────────────────────────────────────────────────────┤
│  能力层                                                       │
│  Cdp(会话/域封装/事件流)   PageAgent(页面内驻留 JS)          │
│  Dock(布局/Tab 保活)      Editor      DataTable              │
├─────────────────────────────────────────────────────────────┤
│  Hookmes.Platform     应用平台层:配置/事件词典/注册表/存储     │
├─────────────────────────────────────────────────────────────┤
│  Hookmes.Base         契约与基础设施:IModule/EventBus/AiTool  │
├─────────────────────────────────────────────────────────────┤
│  Hookmes.ToolGen(Roslyn 源生成器)  Hookmes.DomToMarkdown     │
└─────────────────────────────────────────────────────────────┘
```

依赖方向严格自上而下,无环。与 ZeroFall 一致,`Platform` 是"应用平台层"而非 OS 封装层——不含 P/Invoke。

### 2.2 依赖图

```
Base                 ← (无项目引用)
ToolGen              ← (无,netstandard2.0 源生成器)
DomToMarkdown        ← (无,零依赖纯净库)
Platform             → Base
Cdp                  → Base, Platform
PageAgent            → (无,产物是生成的 C# 源文件)
DataTable            → Base, Platform
Dock                 → Base, Platform, DataTable
Editor               → Base, Platform
Browser              → Base, Platform, Cdp, Dock, DataTable, DomToMarkdown, PageAgent
Inspector            → Base, Platform, Cdp, Dock, DataTable, Editor
Automation           → Base, Platform, Cdp, Dock, Editor
Terminal             → Base, Platform, Dock
AiPanel              → Base, Platform, Dock
Sidebar              → Base, Platform, Dock
Settings             → Base, Platform
App                  → 全部功能模块
```

`ToolGen` 统一以 `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` 被声明 AI 工具的模块引用,不进运行时依赖。

**关键解耦点**:`Inspector` 与 `Automation` 都需要操作页面,但它们**不引用 `Browser`**。三者通过 `Cdp` 层的 `ICdpSessionRegistry` 相遇——Browser 创建标签页时注册会话,其余模块按 `pageId` 取用。

---

## 三、核心设计:统一动作模型

这是 Hookmes 区别于普通"内嵌浏览器 + AI 聊天"的关键,也是整个架构的灵魂。

**问题**:同一个"点击按钮"动作有四个来源——用户手点、终端敲 `click #submit`、AI 调 `page_click` 工具、回放脚本执行。如果四条路径各写一份实现,行为必然发散,录制也无从下手。

**方案**:所有页面动作统一收敛为 `ActionDescriptor`,由单一执行器 `ActionExecutor` 落地。

```
   人工点击(Page Agent 捕获)  ┐
   终端 REPL 命令            ├──→ ActionDescriptor ──→ ActionExecutor ──→ CDP
   AI 工具调用               │         (可序列化)          │
   脚本回放                  ┘                            ├──→ Timeline(录制/审计)
                                                          └──→ IToolPolicyGate(策略闸门)
```

```csharp
// Hookmes.Automation/Model/ActionDescriptor.cs
public sealed record ActionDescriptor(
    ActionKind Kind,              // Navigate/Click/Type/Select/Scroll/Hover/Press/Wait/Eval/Assert
    TargetSelector? Target,       // 选择器 + 备选策略链
    IReadOnlyDictionary<string, string?> Args,
    ActionOptions Options);       // 超时、重试、等待条件、是否滚动到可视区
```

由此得到三个"免费"的能力:

- **录制即真实动作**:Page Agent 捕获的用户操作直接是 `ActionDescriptor`,无需从 DOM 事件反推。
- **AI 与人工完全同构**:AI 做的每一步都能回放、能导出成脚本,人做的每一步 AI 都看得懂。
- **单一审计与策略点**:所有动作经过一个闸门,安全策略只需实现一次。

### 选择器策略链

单一 CSS 选择器在真实页面里非常脆弱。`TargetSelector` 携带一条按稳定性排序的候选链,执行时依次尝试:

```
1. data-testid / data-test / data-cy      (最稳定,若存在)
2. #id                                     (排除明显随机的 id)
3. role + accessible name                  (可访问性树,对 React/Vue 友好)
4. 文本内容精确匹配                          (按钮/链接)
5. 结构化 CSS 路径(带 nth-child 锚点)      (兜底)
```

生成逻辑在 Page Agent 内(能看到运行时 DOM),评分与降级逻辑在 `Hookmes.Automation/Selectors/`。

---

## 四、CDP 层设计(`Hookmes.Cdp`)

### 4.1 取得 CDP 通道

沿用 ZeroFall 验证过的路径,但把封装做厚:

```
NativeWebView (Avalonia.Controls.WebView)
  → TryGetPlatformHandle() as IWindowsWebView2PlatformHandle
  → ICoreWebView2 裸指针
  → 手写 COM vtable 调用(AOT 友好,不依赖 Microsoft.Web.WebView2.Core 托管封装)
  → CallDevToolsProtocolMethod / AddDevToolsProtocolEventReceiver
```

ZeroFall 只做了 `CallDevToolsProtocolMethod`(单向请求-响应)。Hookmes 必须额外接上 **`GetDevToolsProtocolEventReceiver`**(`ICoreWebView2_11` 起的官方 API)——没有事件订阅就没有 `Network.responseReceived`、`Runtime.consoleAPICalled`、`Runtime.bindingCalled`,整个实时能力无从谈起。

事件订阅的机制有一个重要约束:**receiver 是按事件名创建的**,不是一个总线。流程为

```
1. CallDevToolsProtocolMethod("Network.enable", "{}")     启用域
2. GetDevToolsProtocolEventReceiver("Network.responseReceived")   取该事件的 receiver
3. receiver.add_DevToolsProtocolEventReceived(handler)    挂处理器
   → handler 收到 ParameterObjectAsJson(事件参数 JSON)+ SessionId(事件来源)
```

因此 `CdpEventPump` 需要维护一张 `事件名 → receiver + handler token` 的注册表,负责去重订阅、引用计数(多个模块订阅同一事件只建一个 receiver)、以及页面关闭时统一解绑。这是 CDP 层最主要的新增工作量,但**属于常规工程量而非技术未知**。

### 4.2 域封装

不做全量 CDP 强类型生成(微软的 `DevToolsProtocolExtension` 已停止维护,且体积巨大)。只对实际用到的域写薄封装,其余留 `RawAsync(method, json)` 出口:

| 域 | 用途 |
|---|---|
| `Page` | 导航、生命周期事件、截图、`addScriptToEvaluateOnNewDocument`(Page Agent 注入) |
| `Runtime` | `evaluate`、`callFunctionOn`、`addBinding`(页面→宿主通道)、`consoleAPICalled` |
| `DOM` | 节点树、`querySelector`、`getBoxModel`、属性读写 |
| `CSS` | 计算样式、匹配规则、样式表编辑 |
| `Network` | 请求/响应事件、`getResponseBody`、请求头改写、Cookie |
| `Fetch` | **请求拦截与 Mock**——暂停请求、改写、伪造响应 |
| `Input` | `dispatchMouseEvent` / `dispatchKeyEvent` / `insertText`(真实输入,非 JS 触发) |
| `Overlay` | `highlightNode`、`setInspectMode`(元素拾取) |
| `Log` | 浏览器级日志(网络错误、CSP 违规) |
| `Emulation` | 设备/视口/网络限速/地理位置 |
| `Accessibility` | 可访问性树 —— 给 AI 的**首选**页面表示 |
| `Storage` | localStorage / sessionStorage / IndexedDB / Cookie |
| `Performance` | 指标采样 |

### 4.3 会话与并发

```csharp
public interface ICdpSessionRegistry          // 单例
{
    ICdpSession? Get(string pageId);
    IReadOnlyList<ICdpSession> All { get; }
    IDisposable Register(string pageId, ICdpSession session);
    event Action<string> SessionOpened;
    event Action<string> SessionClosed;
}
```

**必须继承的两条硬约束**(ZeroFall 踩坑后的产物):

- `WebViewCreationCoordinator` — 同一时刻只允许一个 WebView2 初始化,带看门狗超时。多实例并发初始化会卡死。
- `UiScriptGate` — 浏览器 WebView 与 AI 面板 WebView 共用 UI 线程,脚本调用需全局互斥,否则交叉调用死锁。

CDP 调用还需注意:**WebView2 按调用顺序派发但可能乱序完成**,因此每个请求自带 id 并用 `TaskCompletionSource` 配对,不能假设顺序。

---

## 五、Page Agent(`Hookmes.PageAgent`)

页面内驻留的 TypeScript,经 `Page.addScriptToEvaluateOnNewDocument` 在**任何页面脚本之前**执行。这是 ZeroFall 完全没有、而 Hookmes 赖以成立的部分。

### 职责

| 能力 | 实现 |
|---|---|
| 网络 hook | 包装 `fetch` / `XMLHttpRequest` / `WebSocket` / `EventSource`,记录调用栈——**CDP Network 域看不到发起代码的位置,这是补充而非重复** |
| 存储 hook | 包装 `localStorage` / `sessionStorage` / `document.cookie` 的读写 |
| 路由 hook | `history.pushState` / `replaceState` / `popstate`,识别 SPA 软导航 |
| 交互录制 | 捕获 `click` / `input` / `change` / `submit` / `keydown`(捕获阶段,不干扰页面) |
| 选择器生成 | 为目标元素生成候选选择器链并打分 |
| 元素拾取 | 悬停高亮 + 点击选定,回传选择器与元素信息 |
| 框架探针 | 识别 React / Vue / Angular 及其组件树与状态(可选,按需注入) |
| 变更观测 | `MutationObserver` 记录 DOM 变化,支持"动作前后 diff" |

### 回传通道

`Runtime.addBinding(name: "__hookmes__")` 在页面注入一个宿主函数。Page Agent 调用它,宿主收到 `Runtime.bindingCalled` 事件。这是页面→宿主的唯一通道,消息为 JSON 字符串,带序号与分片(单条 binding 调用有长度限制,大 payload 需分片重组)。

### 执行世界的划分(关键约束)

这里有一个无法回避的矛盾:**要 hook `fetch` / `XHR` 必须在主世界**——isolated world 有独立的 JS 全局对象,在其中包装 `fetch` 对页面代码毫无影响。而隔离性又只有 isolated world 能提供。因此 Agent 必须**拆成两部分**:

| 部分 | 世界 | 内容 | 理由 |
|---|---|---|---|
| `agent-main` | 主世界 | 网络 hook、storage hook、路由 hook | 必须包装页面实际使用的对象 |
| `agent-iso` | isolated world | 录制器、选择器生成、元素拾取、MutationObserver | 不需要改写主世界对象,隔离更安全,不受页面篡改 |

注入方式:两者都经 `Page.addScriptToEvaluateOnNewDocument`,`agent-iso` 额外指定 `worldName` 参数落到隔离世界。`Runtime.addBinding` 可绑定到指定的执行上下文,两部分各有自己的回传 binding。

**主世界部分的固有风险**:页面代码可以检测到 `fetch.toString()` 异常、可以保存原始引用绕过 hook、也可以反过来篡改 Agent。这是无法根除的——任何在主世界工作的 hook 都有此问题。应对是把主世界部分**做到最小**(只做 hook 与上报,不含任何逻辑),并接受"网络 hook 可能失效"这一现实:失效时 CDP `Network` 域仍然提供完整流量,只是丢失发起调用栈。

### 安全与透明性

- Hook 必须**完全透明**:保留原函数引用、正确转发 `this` 与参数、异常原样抛出、`toString()` 伪装、保持原型链与属性描述符。hook 引发页面行为改变是不可接受的 bug,不是可接受的副作用。
- binding 函数名在运行时随机化,不使用固定标识,减少被检测的面。
- 用户可按站点关闭 Agent(某些站点有反调试检测)。关闭后 CDP 只读能力完整可用——**降级而非失效**。

### 已知平台问题

WebView2 的 `AddScriptToExecuteOnDocumentCreated` 与 `NavigateWithWebResourceRequest` 同时使用时注入不生效(简单 `Navigate` 正常)。Hookmes 走 CDP 的 `Page.addScriptToEvaluateOnNewDocument` 而非 WebView2 托管方法,可规避此问题;但若某条路径退回托管 API,需注意这个坑。

### 构建

沿用 ZeroFall 的 `ai-chat-web` 手法:TypeScript → esbuild 单文件 IIFE → `generate.mjs` 写成 C# raw string literal(`PageAgentScript.g.cs`)→ **产物提交进 Git**。构建机无需 Node 即可 `dotnet build`。

---

## 六、模块划分

### Hookmes.Base
契约与基础设施,保持轻量(目标 < 40 个文件),不含任何业务模型。

`IModule`(两段式)、`IEventBus` / `EventBus`、`ViewModelBase`(带自动退订的 `SubscribeEvent`)、`[AiTool]` / `[ToolParam]` 特性、`AiToolRegistry`、`IToolPolicyGate`、`IAppLogger`、`IDataProvider`。

### Hookmes.ToolGen
Roslyn 增量源生成器(netstandard2.0)。扫描 `[AiTool]` 方法,生成 `AiToolRegistration_{ClassName}.Register(registry, serviceProvider)`,内含 JSON Schema 与参数绑定执行器闭包。

相对 ZeroFall 的改进:支持嵌套对象参数(ZeroFall 遇到非基元类型一律 fallback 成 string)、支持 `[ToolParam(Enum = ...)]` 显式枚举值、生成时校验工具名唯一性并在重复时报编译错误。

### Hookmes.Platform
应用平台层。`AppSettings`(源生成 `JsonSerializerContext`)、`PlatformEvents`(共享事件词典)、四个注册表(`IDockLayoutRegistry` / `IMenuRegistry` / `ISettingsRegistry` / `IContentFactoryRegistry`)、`UiThreadBridge`、`WorkspaceService`、`SqliteService`、`SecretStore`(DPAPI)、`OutboundHttpClientFactory`。

### Hookmes.Cdp
见第四节。COM 互操作、会话注册表、域封装、事件泵。

### Hookmes.PageAgent
见第五节。TypeScript 源码 + 构建脚本 + 生成的 C# 常量。

### Hookmes.Browser
浏览器视图与标签管理。地址栏、导航、多标签、favicon、缩放、DevTools 唤起、Page Agent 注入编排、元素拾取器 UI、设备模拟工具条。

### Hookmes.Inspector
类 DevTools 的检查面板,是"人工操作"的主阵地:

- **DOM 树** — 可展开、可编辑属性、悬停联动页面高亮
- **样式** — 计算样式、匹配规则、盒模型
- **网络** — 请求列表(CDP Network 域)、详情、时序、响应体预览、Mock 规则编辑
- **控制台** — 页面 console 输出 + 表达式求值 REPL
- **存储** — localStorage / sessionStorage / Cookie / IndexedDB 读写
- **时间线** — 统一动作流水(人工 + AI + 脚本),可回溯、可导出为脚本

### Hookmes.Automation
`ActionDescriptor` 模型、`ActionExecutor`、选择器引擎、录制器、脚本存储与回放、断言库、终端 REPL 的命令定义(与 AI 工具共用同一份定义)。

### Hookmes.Terminal
双会话类型:

- **System Shell** — PTY(`Iciclecreek.Avalonia.Terminal`,底层 XTerm.NET + Porta.Pty),跑 cmd / pwsh / bash
- **Hookmes Console** — 领域命令 REPL,直接操作页面

领域命令与 AI 工具**共用 `Hookmes.Automation` 中的同一份命令定义**,REPL 与 AI 只是同一组能力的两个前端。示例:

```
open https://example.com          导航
click #submit                     点击
type input[name=q] "hello"        输入
eval document.title               求值
dom .item                         查询元素
net ls --status=4xx               列出请求
net mock /api/user --file=./u.json 拦截并伪造响应
console tail 50                   最近日志
rec start / rec stop              录制
run ./scripts/login.hkm           回放脚本
snap --full                       截图
```

在 Hookmes Console 里以 `!` 前缀可直接执行系统命令,无需切换会话。

### Hookmes.AiPanel
AI 对话面板。Vue 3 + Vite singlefile 前端烧进 C#,WebView 承载。OpenAI 兼容 API 客户端(手写 `HttpClient` + SSE 解析,兼容各类网关差异)、工具调用编排、上下文压缩、MCP 桥接、子 Agent。

### Hookmes.Dock / DataTable / Editor / Sidebar / Settings / DomToMarkdown
UI 基础设施,基本沿用 ZeroFall 的成熟实现。`Dock` 中的 `PersistTabControl`(Tab 保活)是 WebView2 与 PTY 能正常工作的前提,必须优先移植。

`DomToMarkdown` 保持零依赖、直接消费 CDP 节点树的设计——ZeroFall 的洞察很关键:**绕过 HTML 字符串解析可以规避实体反转义导致的标签注入**(例如页面把 CSS 藏在 `<textarea>` 文本节点里,HTML 解析器会把它当成真标签)。

---

## 七、AI 工具集

工具按能力域分组,由各自模块声明(`[AiTool]`),编译期生成注册代码。

### 页面读取
| 工具 | 说明 |
|---|---|
| `page_snapshot` | 页面结构快照。默认返回**可访问性树**(比 DOM 更接近人的认知、token 更省),可选 markdown / 原始 DOM |
| `page_query` | 按选择器查元素,返回文本、属性、盒模型、可见性、是否可交互 |
| `page_eval` | 在页面执行 JS 表达式并返回序列化结果 |
| `page_screenshot` | 截图,支持全页 / 指定元素 |
| `page_styles` | 元素的计算样式与匹配规则 |

### 页面交互
| 工具 | 说明 |
|---|---|
| `page_navigate` | 导航,可等待 load / networkidle |
| `page_click` | 真实鼠标事件点击(经 `Input` 域,非 JS `.click()`) |
| `page_type` | 真实键盘输入,支持清空后输入 |
| `page_select` / `page_hover` / `page_scroll` / `page_press` | 其余交互 |
| `page_wait` | 等待选择器出现/消失、网络空闲、自定义表达式为真 |

### 调试观测
| 工具 | 说明 |
|---|---|
| `console_read` | 读取 console 输出,可按级别/时间过滤 |
| `network_list` / `network_detail` | 请求列表与详情(含 Page Agent 记录的发起调用栈) |
| `network_mock` | 拦截并伪造响应,用于验证前端异常分支 |
| `storage_read` / `storage_write` | 存储读写 |
| `dom_diff` | 某个动作前后的 DOM 变化,用于确认交互是否生效 |
| `perf_metrics` | 性能指标 |

### 自动化
| 工具 | 说明 |
|---|---|
| `script_record` | 开始/停止录制,返回生成的脚本 |
| `script_run` | 回放脚本 |
| `assert` | 断言(元素存在、文本匹配、请求发生、无 console 错误) |

### 通用
`shell_send` / `shell_read` / `shell_interrupt`(交互式终端,回合制而非一次性执行)、`look` / `write` / `replace`(文件)、`sql`(项目库)、`todo`、`ask`、`spawn_agent`。

### 安全闸门

ZeroFall 最明确的缺口是 AI 可以无审批执行任意 shell 命令。Hookmes 在 `AiToolDispatcher` 这个唯一收口处引入策略:

```csharp
public interface IToolPolicyGate
{
    ValueTask<ToolPolicyDecision> EvaluateAsync(ToolInvocation invocation, CancellationToken ct);
}
// Allow / RequireConfirmation(reason) / Deny(reason)
```

默认策略:

- **自动放行** — 所有只读工具(page_snapshot / console_read / network_list / look / sql SELECT …)
- **需确认** — `shell_send`、`write` / `replace`、`network_mock`、`storage_write`、目标为非白名单域名的 `page_navigate`
- **拒绝** — 命中危险模式的 shell 命令(格式化、大范围删除、权限变更、凭据外发)

确认走一次 UI 弹窗,支持"本会话记住此类操作"。策略可在设置中调整,包括切到"信任模式"全放行——但那是用户的显式选择,而非默认。

---

## 八、数据存储

工作区 = 一个目录;数据库 = 该目录下的 `.hookmes.db`。沿用 ZeroFall 的 `ProjectOpenedEvent` 枢纽模式:各 store 订阅事件拿到库路径,惰性建表,库文件由第一个写入者隐式创建。

裸 `Microsoft.Data.Sqlite` + 手写 SQL,不引 ORM。**但改进 schema 演进**:ZeroFall 完全靠 `CREATE TABLE IF NOT EXISTS` + `PRAGMA table_info` 补列,没有版本号,迁移逻辑分散在九个 store 里。Hookmes 使用 `PRAGMA user_version` + 集中的迁移脚本列表。

主要表:

| 表 | 内容 |
|---|---|
| `pages` | 页面会话(标签、URL、开始/结束时间) |
| `navigations` | 导航记录(含 SPA 软导航) |
| `network_events` | 请求/响应元数据 + 发起调用栈,body 分离存储 |
| `network_bodies` | 请求/响应体(按需,带大小上限) |
| `console_logs` | console 输出与页面异常 |
| `actions` | 统一动作时间线(来源:human / ai / script / repl) |
| `dom_snapshots` | DOM 快照(用于 diff) |
| `scripts` / `script_runs` / `script_steps` | 自动化脚本与执行记录 |
| `ai_chat_sessions` / `ai_chat_messages` | AI 会话 |
| `terminal_sessions` / `terminal_lines` | 终端记录 |

---

## 九、界面布局

五区域固定 Grid + 动态 Tab(区域编译期固定,不做可拖拽 Dock 树——ZeroFall 的取舍是对的,复杂度与收益不成正比):

```
┌──────────────────────────────────────────────────────────────┐
│ TopBar: 地址栏 · 导航 · 设备模拟 · 拾取器 · 录制 · 主题        │
├────────┬─────────────────────────────────────┬───────────────┤
│        │                                     │               │
│ Left   │  Content                            │  Right        │
│        │  ┌─────────────────────────────┐    │               │
│ 项目树 │  │  浏览器视图(多标签)         │    │  AI 面板      │
│ 元素树 │  │                             │    │               │
│ 脚本库 │  └─────────────────────────────┘    │  对话         │
│        ├─────────────────────────────────────┤  工具调用     │
│        │  Bottom                             │  待办         │
│        │  网络 │ 控制台 │ 元素 │ 存储 │       │               │
│        │  时间线 │ 终端                       │               │
└────────┴─────────────────────────────────────┴───────────────┘
                          StatusBar
```

三条 UI 约定继承自 ZeroFall:

- Tab **壳/内容两阶段懒物化** — 启动只建标题壳,选中时才构造 View。
- 浏览器与终端 Tab 标记 `NonReloadable`,由 `PersistTabControl` 叠层保活。
- 底部区折叠时**只改行高,不改 `IsVisible`** — PTY 与 WebView2 需要保持在可视树上。

---

## 十、文件目录结构

```
G:\Hookmes\
├─ Hookmes.slnx
├─ Directory.Build.props              公共属性(TFM/Nullable/Avalonia 编译绑定)
├─ Directory.Packages.props           中央包版本管理
├─ .gitignore
├─ README.md
│
├─ docs\
│  ├─ ARCHITECTURE.md                 本文
│  ├─ INHERITED-PATTERNS.md           从 ZeroFall 继承的设计与出处
│  ├─ CDP-LAYER.md                    CDP 域封装与事件流详解
│  ├─ PAGE-AGENT.md                   Page Agent 协议与 hook 清单
│  ├─ AI-TOOLS.md                     工具清单、schema、策略矩阵
│  └─ ROADMAP.md                      分阶段实施计划
│
├─ assets\
│  └─ fonts\                          HarmonyOS Sans SC
│
├─ scripts\                           构建/发布辅助脚本
│
└─ src\
   ├─ Hookmes.Base\
   │  ├─ IModule.cs
   │  ├─ Events\           IEventBus, EventBus, EventSubscription
   │  ├─ Mvvm\             ViewModelBase
   │  ├─ AiTools\          AiToolAttribute, AiToolRegistry, ToolDefinition,
   │  │                    IToolPolicyGate, AiToolExecutionQueue
   │  ├─ Data\             IDataProvider
   │  ├─ Diagnostics\      IAppLogger, AppDiagnostics, UiScriptGate
   │  └─ Converters\
   │
   ├─ Hookmes.ToolGen\                netstandard2.0 · IsRoslynComponent
   │  ├─ AiToolSourceGenerator.cs
   │  ├─ JsonSchemaMapper.cs
   │  └─ ExecutorEmitter.cs
   │
   ├─ Hookmes.Platform\
   │  ├─ Models\           AppSettings, Workspace, DeviceProfile
   │  ├─ Events\           PlatformEvents(共享事件词典)
   │  ├─ Registries\       IDockLayoutRegistry, IMenuRegistry,
   │  │                    ISettingsRegistry, IContentFactoryRegistry
   │  ├─ Services\         UiThreadBridge, WorkspaceService, SqliteService,
   │  │                    SettingsService, SecretStore(DPAPI),
   │  │                    OutboundHttpClientFactory, StartupPerformance,
   │  │                    WebViewCreationCoordinator, UiContextService
   │  ├─ Storage\          Migrations, DbGateway
   │  └─ Serialization\    AppSettingsJsonContext
   │
   ├─ Hookmes.Cdp\
   │  ├─ ComInterop\       WebView2ComVTable, WebView2NativeWrapper,
   │  │                    DevToolsEventReceiver
   │  ├─ Session\          ICdpSession, CdpSession, ICdpSessionRegistry,
   │  │                    CdpRequestPump, CdpEventPump
   │  ├─ Domains\          PageDomain, RuntimeDomain, DomDomain, CssDomain,
   │  │                    NetworkDomain, FetchDomain, InputDomain,
   │  │                    OverlayDomain, LogDomain, EmulationDomain,
   │  │                    AccessibilityDomain, StorageDomain
   │  ├─ Events\           CdpEvents(强类型事件 record)
   │  └─ Binding\          HostBinding(Runtime.addBinding 分片重组)
   │
   ├─ Hookmes.PageAgent\
   │  ├─ src\
   │  │  ├─ main\          主世界:net-hook.ts, storage-hook.ts,
   │  │  │                 route-hook.ts, entry-main.ts
   │  │  ├─ iso\           隔离世界:recorder.ts, selector.ts, picker.ts,
   │  │  │                 mutation-watch.ts, framework-probe.ts,
   │  │  │                 entry-iso.ts
   │  │  └─ shared\        transport.ts(binding 分片协议), types.ts
   │  ├─ package.json      esbuild + generate.mjs
   │  ├─ generate.mjs      两个产物 → C# raw string literal
   │  └─ Generated\        PageAgentScript.g.cs(含 MainWorld / IsolatedWorld
   │                       两个常量,提交进 Git)
   │
   ├─ Hookmes.Browser\
   │  ├─ Views\            BrowserTabView, BrowserToolbarView, PickerOverlay
   │  ├─ ViewModels\       BrowserTabViewModel, BrowserHostViewModel
   │  ├─ Services\         BrowserTabManager, PageAgentInjector,
   │  │                    ElementPickerService, DeviceEmulationService
   │  ├─ Tools\            PageReadAiTools, PageInteractAiTools
   │  └─ BrowserModule.cs
   │
   ├─ Hookmes.Inspector\
   │  ├─ Views\            DomTreeView, StylesView, NetworkView,
   │  │                    ConsoleView, StorageView, TimelineView
   │  ├─ ViewModels\
   │  ├─ Services\         NetworkStore, ConsoleStore, DomTreeService,
   │  │                    MockRuleEngine, TimelineStore
   │  ├─ Tools\            NetworkAiTools, ConsoleAiTools, StorageAiTools
   │  └─ InspectorModule.cs
   │
   ├─ Hookmes.Automation\
   │  ├─ Model\            ActionDescriptor, TargetSelector, ActionResult
   │  ├─ Execution\        ActionExecutor, WaitStrategies, RetryPolicy
   │  ├─ Selectors\        SelectorScorer, SelectorResolver
   │  ├─ Recording\        Recorder, ActionNormalizer
   │  ├─ Scripting\        ScriptModel, ScriptRunner, ScriptStore, Assertions
   │  ├─ Commands\         命令定义(REPL 与 AI 工具共用)
   │  ├─ Tools\            ScriptAiTools, AssertAiTools
   │  └─ AutomationModule.cs
   │
   ├─ Hookmes.Terminal\
   │  ├─ Views\            TerminalView, TerminalHostView
   │  ├─ ViewModels\
   │  ├─ Services\         ShellSessionService, HookmesConsoleService,
   │  │                    CommandParser, TranscriptService
   │  ├─ Tools\            ShellAiTools
   │  └─ TerminalModule.cs
   │
   ├─ Hookmes.AiPanel\
   │  ├─ chat-web\         Vue 3 + Vite singlefile(同 PageAgent 的产物策略)
   │  ├─ Views\            AiChatWebView, AiChatHtml.g.cs
   │  ├─ ViewModels\       AiPanelViewModel
   │  ├─ Services\         ChatClient(SSE), ToolDispatcher, ContextCompressor,
   │  │                    SessionStore, McpBridge, SubAgentRunner,
   │  │                    MarkdigBlockStreamer, SystemPrompt
   │  ├─ Tools\            FileAiTools, SqlAiTools, TodoAiTools, AskAiTools
   │  └─ AiPanelModule.cs
   │
   ├─ Hookmes.Dock\
   │  ├─ Controls\         PersistTabControl, TabContent, DockTabControl
   │  ├─ ViewModels\       DockLayoutViewModel, DockPanelViewModel
   │  ├─ Services\         UiLayoutService, AppDialogService, ContentCreation
   │  └─ DockModule.cs
   │
   ├─ Hookmes.DataTable\
   ├─ Hookmes.Editor\      AvaloniaEdit 封装、语法高亮(JS/JSON/HTML/CSS)
   ├─ Hookmes.Sidebar\
   ├─ Hookmes.Settings\
   ├─ Hookmes.DomToMarkdown\          零依赖,直接消费 CDP 节点树
   │
   └─ Hookmes.App\
      ├─ Program.cs                   异常钩子 + AppBuilder
      ├─ App.axaml(.cs)               主题装配 + 分阶段异步初始化
      ├─ AppModuleBootstrap.cs        硬编码模块清单,两趟循环
      ├─ ViewLocator.cs               手写字典(AOT 友好)
      ├─ CoreModule.cs
      ├─ Views\                       MainWindow, MainContentView,
      │                               TopBarView, StatusBarView
      ├─ ViewModels\                  MainWindowViewModel
      ├─ Styles\                      对 Semi 的微调
      └─ Assets\
```

---

## 十一、实施路线

分五个阶段,每阶段都以"可运行、可验证"为终点。

**阶段 0 — 骨架(基础设施)**
`Base` / `Platform` / `Dock` / `App` 四件套跑通。目标:能启动一个带五区域布局、可切 Tab、能存取设置的空壳。这里把 `PersistTabControl` 移植到位——它是后续一切的地基。

**阶段 1 — 浏览器与 CDP 通道**
`Cdp` COM 互操作 + 事件接收器,`Browser` 多标签浏览。目标:能打开网页、能调 `Runtime.evaluate` 拿返回值、能收到 `Network.responseReceived` 事件。**事件接收器是本阶段的主要风险点**,ZeroFall 没有现成实现可抄。

**阶段 2 — Page Agent 与检查面板**
Page Agent 注入 + binding 回传,`Inspector` 的网络/控制台/DOM/存储面板。目标:纯人工使用已经是一个可用的调试工具。

**阶段 3 — 统一动作模型与自动化**
`Automation` 的 `ActionDescriptor` / 执行器 / 选择器引擎 / 录制回放,`Terminal` 的双会话与 REPL。目标:能录一段登录流程并回放成功。

**阶段 4 — AI 集成**
`AiPanel` + 各模块 AI 工具 + 策略闸门 + MCP。目标:能对 AI 说"帮我看看这个表单为什么提交没反应",它能自主截图、点击、读 console、查网络请求并给出结论。

---

## 十二、关键风险

| 风险 | 说明 | 应对 |
|---|---|---|
| ~~**CDP 事件接收器需接入**~~ | **已解除(阶段 1 验证通过)。** 槽位与 IID 全部从 SDK 头文件提取:`GetDevToolsProtocolEventReceiver` = 42,`add_DevToolsProtocolEventReceived` = 3,IID `e2fda4be-5456-406c-a261-3d452138362c`。回调用 .NET 源生成 COM 互操作实现,无需手写 CCW | — |
| **Page Agent 与页面冲突** | 主世界 hook 无法完全隐藏,页面可检测、可绕过、可篡改;hook 不透明还会改变页面行为 | 主世界部分做到最小(只 hook 与上报);录制/拾取等逻辑放隔离世界;严格的透明性要求(原型链、属性描述符、`toString()`);按站点可关闭并降级到纯 CDP 只读 |
| **选择器脆弱** | 单一 CSS 选择器在真实站点极易失效 | 候选链 + 运行时评分 + 回放失败时自动尝试次优选择器并提示 |
| **WebView2 多实例死锁** | 浏览器与 AI 面板共用 UI 线程 | 直接继承 ZeroFall 的创建互斥与脚本闸门,不要重新发明 |
| **AI 破坏性操作** | 参考项目的实际教训 | 策略闸门默认保守,危险动作需确认 |
| **.NET 10 预览包** | 依赖多个 preview 版本包 | 中央包管理便于统一升级;`Fluxzy` 等 AOT 不友好的依赖已不在依赖树中 |

---

## 十三、环境前置

| 项 | 要求 | 本机现状 |
|---|---|---|
| 操作系统 | Windows 10/11 x64 | Windows 11 ✓ |
| .NET SDK | **10.0** | ✗ **仅有 .NET 8 运行时,无任何 SDK,需安装** |
| WebView2 Runtime | 任意近期版本 | 150.0.4078.99 ✓ |
| Node.js | 18+(仅修改 PageAgent / chat-web 时需要) | v24.14.0 ✓ |
| Git | — | 2.42.0 ✓ |
