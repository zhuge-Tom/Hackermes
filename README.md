# Hackermes

![Hackermes icon](src/Hackermes.App/Assets/hackermes-icon.png)

Hackermes 是面向人工操作与 Agent 协作的桌面网页调试、流量分析和授权安全评估工作台。
项目基于 .NET 10 与 Avalonia，将内置浏览器、CDP、DOM 检查、HTTP 数据包处理、终端、
AI 助手和受控 ToolHost 集成在同一应用中。

> 当前版本：`0.7.0`。Windows 10/11 x64 是完整验证平台；Linux x64 为预览平台。

## 主要能力

- 内置多标签浏览器、地址栏、PC/移动视图切换，以及仅影响内部浏览器的代理模式。
- 类 DevTools 的 DOM 树、页面元素拾取、双向悬停/点击定位、样式查看与 CSS 规则编辑。
- 打开 DOM 标签时自动读取当前页面；页面变化后仍可使用 Refresh 手动刷新。
- CDP Network、Console、Storage、Timeline 和页面资源检查。
- HTTP 请求/响应捕获、拦截、原始编辑、重放、Comparer、规则与历史记录。
- Query、Form、Header、Cookie 和有界 JSON Pointer 参数读取与修改。
- OpenAI 兼容 API、模型自动发现、三档 Agent 权限、上下文压缩、持久记忆与 Skill 工作流。
- 精确授权范围、固定计划、一次性审批票据和独立 ToolHost 执行链路。
- 授权评估工作区支持任务执行、取消/撤销、证据验证、Finding 创建与复核、HMAC 审计链验证，
  以及 JSON、Markdown、HTML 报告导出。
- 人工、CLI 与 Agent 共用同一个授权控制面；Agent 不获得任意 Shell。

## 下载与安装

发布包位于 [GitHub Releases](https://github.com/zhuge-Tom/Hackermes/releases)，附件同时提供
`SHA256SUMS.txt`。

### Windows 10/11 x64

1. 下载 `Hackermes-<version>-windows-x64.zip` 并解压。
2. 在 PowerShell 中运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Hackermes.ps1
   ```

3. 从开始菜单启动 Hackermes。

Windows 包自包含 .NET Runtime。内置浏览器需要系统安装 Microsoft Edge WebView2 Runtime。
安装器会验证逐文件 SHA-256、暂存新版本并原子切换；上一版本保留在 `.previous` 目录，可运行
`Install-Hackermes.ps1 -RestorePrevious` 回滚。安装、升级、回滚和普通卸载默认保留用户数据。

### Linux x64（预览）

1. 下载 `Hackermes-<version>-linux-x64.tar.gz` 并解压。
2. 执行：

   ```bash
   chmod +x install.sh
   ./install.sh
   ```

3. 如果 `~/.local/bin` 已加入 `PATH`，运行 `hackermes`。

Linux 包自包含 .NET Runtime，但仍需要发行版提供 Avalonia 桌面依赖和 WebKitGTK。Ubuntu/Debian
常见依赖如下，不同发行版的软件包名称可能不同：

```bash
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libwebkit2gtk-4.1-0
```

WebView2 专属 CDP 能力目前仅在 Windows 提供。Linux 包已完成交叉构建与归档校验，但尚未完成
真实 Linux GUI 全链路验收。

## 从源码构建

### 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10/11 x64；Linux x64 为预览目标 |
| .NET SDK | 10.0 |
| WebView2 Runtime | Windows 内置浏览器需要 |
| Node.js | 仅修改 Page Agent TypeScript 时需要 |
| Python 3 | 本地验收或生成发布归档时需要 |

本项目不要求单独安装 Windows 10 SDK。

### Windows 开发构建

构建与启动严格分离。修改源码后执行一次：

```powershell
.\scripts\build-hackermes.ps1
```

之后快速启动已有构建，不会再次编译：

```powershell
.\scripts\run-hackermes.ps1
```

开发运行目录：

```text
%LOCALAPPDATA%\Hackermes\Build\bin\Hackermes.App\Debug\net10.0
```

### 本地创建 Windows/Linux 发布包

```powershell
.\scripts\package-release.ps1 -Version 0.7.0 -Platforms all
```

只生成单个平台时可将 `all` 改为 `windows` 或 `linux`。产物位于：

```text
artifacts/release/0.7.0/
```

发布脚本生成：

- Windows x64 自包含 ZIP 与安装/卸载脚本。
- Linux x64 自包含 `tar.gz` 与安装/卸载脚本。
- 逐文件 `release-manifest.json` 和归档级 `SHA256SUMS.txt`。

## AI、Skill 与权限

AI 设置包含“模型与 API”和“Skills”两个页面：

- API URL 使用 OpenAI 兼容格式，连接测试成功后自动获取模型列表。
- API Key 不写入 `settings.json`。Windows 使用当前用户 DPAPI；Linux 使用用户级 AES-256 密钥库。
- Agent 默认使用“请求批准”模式，也可以切换为“帮我批准”或“完全访问权限”。
- 上下文压缩、任务摘要和持久记忆由系统内部维护。
- Skill 可由用户创建，也可由 Agent 在权限允许且获得批准后创建和维护。

Agent 工具执行链：

```text
精确授权范围 → 固定计划 → 一次性审批 → 独立 ToolHost 校验 → 有界执行 → 证据与审计
```

## 内部浏览器与 Burp Suite

内部浏览器代理菜单支持直连、Burp `127.0.0.1:8080`、监听状态检测、已知页面遥测过滤和
打开 Burp CA 页面。代理设置只影响 Hackermes 内部浏览器，不修改系统代理。Burp Suite 不随项目分发。

## 安全工具与第三方组件

左侧安全工具菜单保留人工原生入口：CLI 工具打开教学终端，GUI 工具使用原生界面。未内置工具会显示
用途和缺失原因。公开发布包只包含清单中具备来源与许可信息的组件；标记为
`redistribution-unverified` 的本地工具不会进入附件。详情见
[`third_party/tools/manifest.json`](third_party/tools/manifest.json)。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `Hackermes.App` | 桌面宿主、模块装配、主窗口和安全工具菜单 |
| `Hackermes.Base` | 公共契约、事件和日志抽象 |
| `Hackermes.Platform` | 配置、密钥库、注册表和工作区服务 |
| `Hackermes.Dock` | Dock、标签页和布局持久化 |
| `Hackermes.Cdp` | CDP 会话、事件和 WebView2 COM 交互 |
| `Hackermes.PageAgent` | 页面内 Hook 与动作采集脚本 |
| `Hackermes.Browser` | 浏览器标签、代理、设备视图和页面拾取 |
| `Hackermes.Inspector` | DOM、网络、控制台、数据包和工作台界面 |
| `Hackermes.Traffic` | 捕获、拦截、规则、历史、Repeater 与 Comparer |
| `Hackermes.Automation` | 动作执行、脚本、参数读写、归档与审计 |
| `Hackermes.Terminal` | System Shell 与领域 CLI |
| `Hackermes.AiPanel` | OpenAI 兼容客户端、Agent、记忆和 Skill |
| `Hackermes.Assessment` | 授权范围、计划、审批、证据、Finding 与审计 |
| `Hackermes.ToolHost` | 独立、短生命周期的受控工具执行进程 |

## 文档

- [开发状态](docs/DEVELOPMENT-STATUS.md)
- [架构说明](docs/ARCHITECTURE.md)
- [设计记录](docs/DESIGN-NOTES.md)
- [Agent Runtime](docs/AGENT-RUNTIME.md)
- [Stage 7 授权评估方案](docs/STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md)

## 当前限制

- Linux 是预览平台，真实 WebView2/CDP 桌面验收仅覆盖 Windows。
- Linux 安全工具运行环境和依赖仍需逐项适配。
- 多用户身份提供方、复杂规则表单和外部签名式评估报告属于后续增强项。
- WebView2 使用多进程架构，打开复杂页面时内存占用会明显高于纯 Avalonia 界面。

## 安全声明

Hackermes 仅用于合法调试、研究和已经获得明确授权的安全测试。使用者必须遵守适用法律、目标系统规则
和第三方工具许可证，并对自己的操作负责。
