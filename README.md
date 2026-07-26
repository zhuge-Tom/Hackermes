# Hookmes

本地桌面端网页调试自动化工具。内置浏览器、页面内 Hook、交互式终端与 AI 辅助,在一个窗口里完成前端调试的完整闭环。

基于 .NET 10 + Avalonia,面向 Windows。

---

## 它解决什么

前端调试的日常是这样的:打开 DevTools、点点页面、翻网络面板、看 console、猜哪个请求出了问题。信息散落在多个面板里,操作无法复现,更没法交给别人(或 AI)代劳。

Hookmes 把这套流程变成可编程、可录制、可自动化的工作台:

**零配置的完整流量视图** — 通过 CDP 直接观测,不需要配置代理,也不需要安装根证书就能看到 HTTPS 明文。

**页面内 Hook** — 文档级预注入的 Page Agent 包装 `fetch` / `XMLHttpRequest` / `WebSocket` / storage / 路由,记录**发起处的调用栈**。这是 DevTools 网络面板给不了的信息:协议层能告诉你发生了什么请求,告诉不了你哪行代码发起的。

```
net/fetch → {"url":"/api/user",  "stack":"at .../checkout.js:142:9"}
net/xhr   → {"url":"/api/cart",  "stack":"at .../legacy.js:88:5"}
```

**统一动作模型** — 人工点击、终端命令、AI 工具调用、脚本回放,四条路径收敛成同一种可序列化的动作。因此录制、审计、回放都是架构的自然结果,而非额外功能。

**交互式终端** — 既是真 PTY(cmd / pwsh / bash),也是操作页面的领域 REPL:

```
open https://example.com
click #submit
net ls --status=4xx
eval document.title
rec start
```

**AI 辅助** — AI 能截图、点击、输入、读 console、查网络、Mock 响应,和人走完全相同的动作路径,并受策略闸门约束。

---

## 架构

```
┌──────────────────────────────────────────────────────────┐
│  Hookmes.App          宿主 / 启动装配 / 主窗口             │
├──────────────────────────────────────────────────────────┤
│  功能模块层(互不引用,靠事件与注册表通信)                  │
│  Browser  Inspector  Automation  Terminal  AiPanel        │
├──────────────────────────────────────────────────────────┤
│  能力层                                                    │
│  Cdp(会话/域封装/事件泵)   PageAgent(页面内驻留 JS)      │
│  Dock(布局/Tab 保活)      Editor      DataTable          │
├──────────────────────────────────────────────────────────┤
│  Hookmes.Platform     应用平台层:配置/事件词典/注册表/存储  │
├──────────────────────────────────────────────────────────┤
│  Hookmes.Base         契约与基础设施                       │
└──────────────────────────────────────────────────────────┘
```

几条贯穿全局的约定:

- **模块间零项目引用。** 横向通信走事件总线与四个注册表(Dock / 菜单 / 设置 / 内容工厂)。检查面板与自动化模块都要操作页面,但都不引用浏览器模块——三者通过 CDP 会话注册表相遇。
- **两段式模块装配。** 第一趟只注册服务,容器构建后第二趟才向注册表登记 Tab、菜单与 AI 工具。
- **启动路径为观感调优。** 空窗先显示,DI 容器在后台线程构建,布局稳定后才关遮罩,WebView 创建与工作区恢复都往后推。

详见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

---

## 当前状态

按五个阶段推进,每阶段以"可运行、可验证"为终点。

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 骨架:模块契约、事件总线、五区域布局、Tab 保活、设置持久化 | ✅ 完成 |
| 1 | CDP 通道:COM 互操作、请求-响应、事件泵、浏览器多标签 | ✅ 完成 |
| 2 | Page Agent 与检查面板:页面内 Hook、网络面板、控制台面板 | ✅ 完成 |
| 3 | 统一动作模型:执行器、选择器引擎、录制回放、交互式终端 | 进行中 |
| 4 | AI 集成:工具集、策略闸门、MCP | 待开始 |

已可用:内置浏览器多标签、CDP 请求-响应与事件订阅、页面内 Hook 与调用栈捕获、网络面板(协议数据与调用栈合并)、控制台面板(console / 未捕获异常 / 浏览器级日志三源合并)、五区域布局与布局持久化。

冷启动到界面就绪约 350 ms,CDP 会话在标签页创建后约 500 ms 就绪。

### 项目构成

| 项目 | 职责 |
|---|---|
| `Hookmes.Base` | 模块契约、事件总线、ViewModel 基类、脚本闸门、日志抽象 |
| `Hookmes.Platform` | 注册表、共享事件词典、UI 线程桥、设置持久化、DPAPI 密钥库、工作区 |
| `Hookmes.Cdp` | COM vtable 互操作、CDP 会话(请求-响应 + 事件泵)、会话注册表 |
| `Hookmes.PageAgent` | 页面内驻留脚本(TypeScript),经 binding 回传 |
| `Hookmes.Dock` | Tab 保活控件、布局 ViewModel、懒物化 |
| `Hookmes.Browser` | 浏览器标签页、WebView 生命周期、Agent 装配 |
| `Hookmes.Inspector` | 网络面板、控制台面板 |
| `Hookmes.App` | 启动装配、主窗口、视图定位 |

---

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10/11 x64 |
| .NET SDK | 10.0 — [下载](https://dotnet.microsoft.com/download) |
| WebView2 Runtime | 任意近期版本 — [下载](https://developer.microsoft.com/microsoft-edge/webview2/) |
| Node.js | 18+,**仅在修改 Page Agent 源码时需要** |

前端产物以生成的 C# 源文件形式提交进仓库,因此常规构建不需要 Node.js。

## 构建与运行

```bash
dotnet restore
dotnet build Hookmes.slnx
dotnet run --project src/Hookmes.App/Hookmes.App.csproj
```

修改 Page Agent 的 TypeScript 源码后需重新生成:

```bash
cd src/Hookmes.PageAgent
npm install
npm run build      # esbuild 打包 → 生成 Generated/PageAgentScript.g.cs
```

### 诊断开关

| 环境变量 | 作用 |
|---|---|
| `HOOKMES_LOG_LEVEL` | `Debug` / `Info` / `Warn` / `Error`,默认 `Info` |
| `HOOKMES_AUTOOPEN_URL` | 启动后自动打开该地址,用于无人值守验证 |
| `HOOKMES_SELFTEST` | 设为 `1` 时,CDP 就绪后自动跑一次自检并把结果写进日志 |

日志位于 `%LocalAppData%\Hookmes\logs\`。

---

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 分层、模块划分、CDP 层设计、Page Agent 协议、AI 工具规划、实施路线 |
| [`docs/DESIGN-NOTES.md`](docs/DESIGN-NOTES.md) | 关键设计决策、平台陷阱清单、明确的取舍、待偿还的技术债 |

---

## 免责声明

本工具用于**自己拥有或已获授权**的网页与应用的调试。Page Agent 会在页面中注入代码、CDP 会读取页面数据,请勿用于未授权的目标。使用者需遵守当地法律法规并对自身行为负责。
