# Hookmes

本地桌面端网页调试自动化工具。交互式命令终端 + 内置浏览器视图 + 人工操作 + AI 辅助前端调试,在一个窗口里完成。

## 它解决什么

前端调试的日常是:打开 DevTools、点点页面、看网络面板、翻 console、猜哪个请求出了问题。Hookmes 把这套流程搬进一个可编程、可录制、可交给 AI 的工作台:

- **内置浏览器** — 基于 WebView2,通过 CDP 全域控制。零代理配置、零证书安装即可看到完整网络流量。
- **页面内 Hook** — 文档级预注入的 Page Agent 包装 `fetch` / `XHR` / `WebSocket` / storage / 路由,记录**发起调用栈**——这是 DevTools 网络面板之外的信息。
- **统一动作模型** — 人工点击、终端命令、AI 工具调用、脚本回放四条路径收敛成同一种可序列化的动作,因此录制、审计、回放都是免费的。
- **交互式终端** — 既是真 PTY(cmd / pwsh / bash),也是操作页面的领域 REPL(`click #submit`、`net ls --status=4xx`)。
- **AI 辅助** — AI 能截图、点击、输入、读 console、查网络、Mock 响应,和人走完全相同的动作路径,并受策略闸门约束。

## 与 ZeroFall 的关系

架构参考 [ZeroFall(烬)](https://github.com/) 的模块化骨架,但定位不同:ZeroFall 是安全攻防工作台,靠 MITM 代理**旁路观测**流量;Hookmes 是前端调试工作台,靠 CDP 与页面内 Agent **进入并驱动**页面。继承其模块化、Tab 保活、AI 工具源生成等成熟设计,详见 `docs/INHERITED-PATTERNS.md`。

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 总体架构、分层、模块划分、目录结构、实施路线 |
| [`docs/INHERITED-PATTERNS.md`](docs/INHERITED-PATTERNS.md) | 从 ZeroFall 继承的设计与精确源码出处 |

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10/11 x64 |
| .NET SDK | 10.0 — [下载](https://dotnet.microsoft.com/download) |
| WebView2 Runtime | 任意近期版本 — [下载](https://developer.microsoft.com/microsoft-edge/webview2/) |
| Node.js | 18+,仅在修改 `Hookmes.PageAgent` 或 `Hookmes.AiPanel/chat-web` 时需要 |

前端产物(Page Agent 脚本、AI 聊天页面)以生成的 C# 源文件形式提交进仓库,因此**常规构建不需要 Node.js**。

## 构建

```bash
dotnet restore
dotnet build src/Hookmes.App/Hookmes.App.csproj
dotnet run --project src/Hookmes.App/Hookmes.App.csproj
```

修改 Page Agent 后需重新生成:

```bash
cd src/Hookmes.PageAgent
npm install
npm run build      # esbuild → generate.mjs → Generated/PageAgentScript.g.cs
```

## 状态

实施按 `docs/ARCHITECTURE.md` 第十一节的五个阶段推进。

**阶段 0(骨架)已完成并通过运行验证。** 已就位的四个项目:

| 项目 | 内容 |
|---|---|
| `Hookmes.Base` | `IModule` 两段式契约、同步 `EventBus`、`ViewModelBase`(订阅自动退订)、`UiScriptGate`、日志抽象 |
| `Hookmes.Platform` | 注册表契约、共享事件词典、`UiThreadBridge`、设置持久化(三级目录回退 + 原子写 + .bak)、DPAPI 密钥库、工作区服务、WebView2 创建互斥 |
| `Hookmes.Dock` | `PersistTabControl` 双模式 Tab 保活控件、面板与布局 ViewModel、懒物化、布局持久化 |
| `Hookmes.App` | 分阶段异步启动、模块装配、`ViewLocator`、五区域主界面 |

**阶段 1(CDP 通道与浏览器)已完成并通过运行验证。** 新增两个项目:

| 项目 | 内容 |
|---|---|
| `Hookmes.Cdp` | COM vtable 直调、源生成 CCW 回调、`CdpSession`(请求-响应 + 事件泵 + 域启用)、会话注册表、CDP JSON 辅助 |
| `Hookmes.Browser` | 浏览器标签页、WebView2 生命周期与适配器探测、CDP 会话建立、地址栏与导航、标签管理 |

**阶段 2(Page Agent 与检查面板)已完成并通过运行验证。** 新增两个项目:

| 项目 | 内容 |
|---|---|
| `Hookmes.PageAgent` | 页面内驻留脚本(TypeScript):`fetch` / `XHR` / `WebSocket` / storage / cookie / 路由 hook,经 `Runtime.addBinding` 回传。esbuild 打包后生成 C# 常量并提交进仓库 |
| `Hookmes.Inspector` | 网络面板(CDP Network 域 + Agent 调用栈合并)、控制台面板(console / 未捕获异常 / 浏览器级日志三源合并) |

当前可运行:五区域布局、Tab 懒物化、面板折叠、布局持久化、主题切换、工作区打开与恢复、内置浏览器多标签、CDP 请求-响应与事件订阅、**页面内 hook 与调用栈捕获、网络与控制台检查面板**。

下一步是阶段 3:统一动作模型(`ActionDescriptor` + 执行器 + 选择器引擎)、录制回放,以及交互式终端。

### Page Agent 能做什么

CDP 的 Network 域能告诉你"发生了什么请求",但告诉不了你"哪行代码发起的"。Page Agent 补上这一层:

```
net/fetch → {"url":"data.json","method":"GET","stack":"at http://127.0.0.1:8899/app.js:5:1"}
net/xhr   → {"url":"data.json?via=xhr","stack":"at http://127.0.0.1:8899/app.js:23:5"}
```

网络面板的"发起"列因此能区分 `fetch` 与 `xhr`,选中请求还能看到完整调用栈。

改动 TypeScript 源码后需重新生成:

```bash
cd src/Hookmes.PageAgent
npm install
npm run build      # esbuild → build.mjs → Generated/PageAgentScript.g.cs
```

### 诊断开关

| 环境变量 | 作用 |
|---|---|
| `HOOKMES_LOG_LEVEL` | `Debug` / `Info` / `Warn` / `Error`,默认 `Info` |
| `HOOKMES_AUTOOPEN_URL` | 启动后自动打开该地址,用于无人值守验证 |
| `HOOKMES_SELFTEST` | 设为 `1` 时,CDP 就绪后自动跑一次自检并把结果写进日志 |

## 免责声明

本工具用于**自己拥有或已获授权**的网页与应用的调试。Page Agent 会在页面中注入代码、CDP 会读取页面数据,请勿用于未授权的目标。使用者需遵守当地法律法规并对自身行为负责。
