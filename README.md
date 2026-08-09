# Hackermes

![Hackermes icon](src/Hackermes.App/Assets/hackermes-icon.png)

Hackermes 是一个面向人工操作与 Agent 协作的桌面网页调试、流量分析和授权安全评估工作台。
项目基于 .NET 10 与 Avalonia，将内置浏览器、CDP、DOM 检查、HTTP 数据包处理、终端、
AI 助手和受控 ToolHost 放在同一个应用中。

> 当前版本：`0.7.0-alpha.1`。Windows x64 是完整验证平台；Linux x64 为预览平台。

## 主要能力

- 内置多标签浏览器、地址栏、PC/移动视图切换和独立代理模式。
- 类 DevTools 的 DOM 树、页面元素拾取、双向高亮定位与 CSS 规则编辑。
- CDP 网络、Console、Storage、时间线和页面资源检查。
- 请求/响应独立拦截、原始 HTTP 编辑、重放、Comparer、规则与历史记录。
- Query、Form、Header、Cookie 和有界 JSON Pointer 参数读写。
- 人工、CLI 与 Agent 共用的数据包查询、标注、归档、审计和签名入口。
- OpenAI 兼容 API、模型自动发现、三档 Agent 权限、上下文压缩、持久记忆与 Skill 工作流。
- 授权范围、固定计划、一次性审批票据与独立 ToolHost 执行链路。

## 下载与安装

发布包位于 [GitHub Releases](https://github.com/zhuge-Tom/Hackermes/releases)。
所有附件都提供 SHA-256 校验值。

### Windows 10/11 x64

1. 下载 `Hackermes-<version>-windows-x64.zip` 并解压。
1. 在 PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Hackermes.ps1
```

1. 从开始菜单启动 Hackermes。

Windows 包是自包含程序，不需要单独安装 .NET Runtime；内置浏览器需要系统存在
Microsoft Edge WebView2 Runtime。

### Linux x64（预览）

1. 下载 `Hackermes-<version>-linux-x64.tar.gz` 并解压。
1. 执行：

```bash
chmod +x install.sh
./install.sh
```

1. 如果 `~/.local/bin` 已加入 `PATH`，运行 `hackermes`。

Linux 包自带 .NET Runtime，但仍需要发行版提供 Avalonia 桌面依赖与 WebKitGTK。
WebView2 专属 CDP 能力目前只在 Windows 上提供；Linux 版本主要用于界面、AI、
离线数据包和跨平台基础能力预览。

Ubuntu/Debian 的依赖名称通常包括：

```bash
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libwebkit2gtk-4.1-0
```

不同发行版的软件包名称可能不同。

## 从源码构建

### 环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10/11 x64；Linux x64 为预览目标 |
| .NET SDK | 10.0 |
| WebView2 Runtime | Windows 内置浏览器需要 |
| Node.js | 仅修改 Page Agent TypeScript 时需要 |
| Python 3 | 仅运行本地验收或创建发布压缩包时需要 |

### Windows 开发构建

构建与启动严格分离。修改源码后执行一次：

```powershell
.\scripts\build-hackermes.ps1
```

之后快速启动现有构建：

```powershell
.\scripts\run-hackermes.ps1
```

开发运行目录位于：

```text
%LOCALAPPDATA%\Hackermes\Build\bin\Hackermes.App\Debug\net10.0
```

### 创建 Windows/Linux 发布包

```powershell
.\scripts\package-release.ps1 -Version 0.7.0-alpha.1
```

产物位于：

```text
artifacts/release/0.7.0-alpha.1/
```

发布脚本会生成：

- Windows x64 自包含安装归档。
- Linux x64 自包含安装归档。
- `SHA256SUMS.txt`。

## AI 与 Skill

AI 设置包含“模型与 API”和“Skills”两个页面：

- API URL 使用 OpenAI 兼容格式，测试成功后自动获取模型列表。
- API Key 不写入 `settings.json`。Windows 使用当前用户 DPAPI；Linux 使用权限限制为
  当前用户的 AES-256 本地密钥库。
- Agent 默认使用“请求批准”模式，也可切换为“帮我批准”或“完全访问权限”。
- 上下文压缩、任务摘要和持久记忆由系统内部维护。
- Skill 可以由用户创建，也可以由 Agent 在批准后创建和维护。

## 内部浏览器与 Burp Suite

内部浏览器代理菜单支持：

- 直连。
- Burp 代理 `127.0.0.1:8080`。
- 监听状态检测。
- 已知页面遥测过滤。
- 打开 Burp CA 页面。

代理设置只影响 Hackermes 内部浏览器，不修改系统代理。Burp Suite 不随项目分发。

## 安全工具与 ToolHost

左侧安全工具菜单保留人工原生入口。CLI 工具打开教学终端，GUI 工具保持原生交互；没有内置的工具会显示功能描述和缺失原因。

Agent 不获得任意 Shell。主动调用必须经过：

```text
精确授权范围 → 固定计划 → 一次性审批 → 独立 ToolHost 校验 → 有界执行 → 证据与审计
```

公开发布包只包含清单中具备来源和许可信息的组件。标记为
`redistribution-unverified` 的本地工具不会进入公开附件，但其菜单说明会保留。
第三方清单位于
[`third_party/tools/manifest.json`](third_party/tools/manifest.json)。

所有扫描、检测和利用功能只能用于自己拥有或已获得明确授权的目标。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `Hackermes.App` | 桌面宿主、主窗口、模块装配与工具菜单 |
| `Hackermes.Base` | 公共契约、事件与日志抽象 |
| `Hackermes.Platform` | 配置、密钥库、注册表和工作区服务 |
| `Hackermes.Dock` | Dock、标签页和布局持久化 |
| `Hackermes.Cdp` | CDP 会话、事件与 WebView2 COM 互操作 |
| `Hackermes.PageAgent` | 页面内 Hook 与动作采集脚本 |
| `Hackermes.Browser` | 浏览器标签、代理、设备视图和页面拾取 |
| `Hackermes.Inspector` | DOM、网络、控制台、数据包与工作台界面 |
| `Hackermes.Traffic` | 抓包、拦截、规则、历史、Repeater 与 Comparer |
| `Hackermes.Automation` | 动作执行、脚本、参数读写、归档与审计 |
| `Hackermes.Terminal` | System Shell 与领域 CLI |
| `Hackermes.AiPanel` | OpenAI 兼容客户端、Agent、记忆和 Skill |
| `Hackermes.Assessment` | 授权范围、计划、审批、证据与适配器 |
| `Hackermes.ToolHost` | 独立、短生命周期的受控工具执行进程 |

## 文档

- [开发状态](docs/DEVELOPMENT-STATUS.md)
- [架构说明](docs/ARCHITECTURE.md)
- [设计记录](docs/DESIGN-NOTES.md)
- [Agent Runtime](docs/AGENT-RUNTIME.md)
- [Stage 7 授权评估方案](docs/STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md)

## 当前限制

- Linux 是预览平台，真实 WebView2/CDP 桌面验收只覆盖 Windows。
- 安全工具的 Linux 运行环境和依赖需要继续逐项适配。
- 当前自动化测试集覆盖数据包、流量、浏览器代理、ToolHost、密钥库和工作台基础契约；真实 Linux GUI 仍需在 Linux 桌面环境补充验收。
- 复杂规则表单、审计操作者身份与完整撤销工作流仍属于后续增强项。

## 免责声明

Hackermes 仅用于合法调试、研究和已授权安全测试。使用者必须遵守适用法律、目标系统规则和第三方工具许可证，并对自己的操作负责。
