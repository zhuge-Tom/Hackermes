# Hackermes

![Hackermes icon](src/Hackermes.App/Assets/hackermes-icon.png)

Hackermes 是面向人工操作与 Agent 协作的桌面网页调试、流量分析和授权安全评估工作台。
项目基于 .NET 10 与 Avalonia，将内置浏览器、CDP、DOM 检查、HTTP 数据包处理、终端、
AI 助手和受控 ToolHost 集成在同一应用中。

> 当前版本：`0.11.0`，已发布 Windows 10/11 x64 自包含包。
> Windows 10/11 x64 是主要验证平台；Linux x64 为预览平台。

## 主要能力

- 内置多标签浏览器、地址栏、PC/移动视图切换，以及仅影响内部浏览器的代理模式。
- 类 DevTools 的 DOM 树、页面元素拾取、双向悬停/点击定位、样式查看与 CSS 规则编辑。
- 打开 DOM 标签时自动读取当前页面；页面变化后仍可使用 Refresh 手动刷新。
- CDP Network、Console、Storage、Timeline 和页面资源检查。
- HTTP 请求/响应捕获、拦截、原始编辑、重放、Comparer、规则与历史记录。
- Query、Form、Header、Cookie、有界 JSON Pointer 与 multipart 表单参数读取和修改。
- OpenAI 兼容 API、模型自动发现、三档 Agent 权限、上下文压缩、持久记忆与 Skill 工作流。
- AI 可对精确绑定的内置浏览器页面读取不含敏感值的 `page_security_snapshot`，输出有界观测码，再按证据选择只读检查、流量分析或受控授权评估工具，并写成待复核 Finding。
- 精确授权范围、固定计划、一次性审批票据和独立 ToolHost 执行链路。
- 授权评估工作区支持任务执行、取消/撤销、证据验证、Finding 创建与复核、HMAC 审计链验证，
  以及 JSON、Markdown、HTML 报告导出。
- 人工、CLI 与 Agent 共用同一个授权控制面；Agent 不获得任意 Shell。
- Agent 运行时融合 deepseek-harness 设计：turn/step 无头驱动器、append-only 会话事件流、
  转向指令队列与优先抢占、只读工具有界并行池、干净失败退避重试与上下文溢出自愈。
- 上下文三级保障：模型自主 ACP 压缩、压力触发的自动摘要压缩（含收缩守卫）、GC 墓碑兜底；
  支持 token 级预算、每模型压缩策略与 KV-cache 对齐的摘要调用。
- 思考流实时展示、任务清单面板、目标自动续跑、大结果外存分页读取（spill）、
  审批审计落日志、会话事件持久化与断点恢复、转录导出与会话分叉。
- Pre-step 拦截缝支持请求前脱敏与上下文注入；工具可声明输出模式并在违例时即时自纠。
- Assessment 任务以 coherent case 原子呈现；Traffic 工作台在最小窗口下保留常用操作，并把低频工具折叠到 `More tools`。

> v0.11.0：563/563 自动化测试通过，Windows x64 自包含打包。整套视觉门禁见 [NEXT-STAGE.md](NEXT-STAGE.md)。

## 下载与安装

最新版位于 [Hackermes v0.11.0](https://github.com/zhuge-Tom/Hackermes/releases/tag/v0.11.0)，也可查看
[GitHub Releases](https://github.com/zhuge-Tom/Hackermes/releases)。附件同时提供
`SHA256SUMS.txt` 用于校验下载完整性。

- [Windows 10/11 x64 ZIP](https://github.com/zhuge-Tom/Hackermes/releases/download/v0.11.0/Hackermes-0.11.0-windows-x64.zip)
- [SHA-256 校验值](https://github.com/zhuge-Tom/Hackermes/releases/download/v0.11.0/SHA256SUMS.txt)

v0.11.0 发布 Windows x64 已验证本地构建。Linux x64 因尚未完成真实 GUI 全链路验收而暂未附加；
可在能访问 NuGet 的 Linux/交叉构建环境中从源码发布。

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
G:\HackermesBuild\workspace\bin\Hackermes.App\Debug\net10.0
```

项目脚本会把所有可控的构建写入统一放到 G 盘：

```text
G:\HackermesBuild\
├─ workspace\                 # bin / obj / 默认开发构建
├─ shared\nuget-packages\    # NuGet 全局包缓存
├─ shared\dotnet-cli-home\   # .NET CLI 首次运行与工具状态
├─ shared\temp\              # 构建子进程 TEMP / TMP
├─ shared\python-cache\      # Python 字节码缓存
├─ evidence\                 # 自测与运行证据
└─ artifacts\release\        # 默认发布包
```

`scripts/initialize-build-environment.ps1` 默认只修改当前脚本进程及其子进程的环境。若希望以后新开的终端也复用 G 盘的 Hackermes/.NET 构建缓存，可执行：

```powershell
.\scripts\initialize-build-environment.ps1 -PersistUserEnvironment
```

该命令会创建 `G:\HackermesBuild\shared` 下的对应目录，并持久化 Hackermes/.NET 构建相关环境变量。`TEMP/TMP`、Python 字节码缓存和 XDG cache 只在项目脚本及其子进程内指向 G 盘，不再全局影响其他 Windows 程序。npm 缓存也不会被项目强制改写，避免多个项目共用一个缓存时的锁冲突。系统安装的 `.NET SDK` 仍从 `C:\Program Files\dotnet` 执行；这是程序安装位置，不是项目构建输出。

为控制空间占用，建议长期只保留关键 TRX、运行日志、视觉截图/元数据和发布产物；重复或失败构建、可再生 WebView2 profile 与临时目录可在进程退出后清理。当前保留的 Stage 9 证据集中在 `G:\HackermesBuild\evidence`。

### 本地创建 Windows/Linux 发布包

```powershell
.\scripts\package-release.ps1 -Version 0.11.0 -Platforms windows
```

Windows 完整发布验收（Release 构建、完整 TRX、真实 loopback、授权评估浅/深截图、打包与哈希校验）：

```powershell
.\scripts\invoke-release-acceptance.ps1 `
  -HackermesBuildRoot G:\HackermesBuild\release-acceptance `
  -RunResponsiveVisualMatrix
```

自动化运行可设置绝对路径 `HACKERMES_DATA_ROOT` 与 `HACKERMES_BROWSER_PROFILE_ROOT`，将产品数据和 WebView2 profile 隔离到验收目录；相对路径或盘根路径会被拒绝。

只生成单个平台时可将 `all` 改为 `windows` 或 `linux`。产物位于：

```text
G:\HackermesBuild\artifacts\release\0.11.0\
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
- 上下文压缩采用 ACP（主动上下文剪枝）：模型通过 `context_compress/decompress/search/status`
  自行决定何时压缩、保留什么；任务摘要和持久记忆由系统内部维护。
- Skill 可由用户创建，也可由 Agent 在权限允许且获得批准后创建和维护。

Agent 工具执行链：

```text
精确授权范围 → 固定计划 → 一次性审批 → 独立 ToolHost 校验 → 有界执行 → 证据与审计
```

## 内部浏览器与 Burp Suite

内部浏览器代理菜单支持直连、Burp `127.0.0.1:8080`、监听状态检测、已知页面遥测过滤和
打开 Burp CA 页面。代理设置只影响 Hackermes 内部浏览器，不修改系统代理。Burp Suite 不随项目分发。

## 安全工具与第三方组件

左侧安全工具菜单保留人工原生入口：CLI 工具打开教学终端，GUI 工具使用原生界面。未接入工具集中到
底部的默认折叠组；缺失、缺依赖、无效和未验证状态分别标注。目录数据由后台线程扫描缓存
（`ToolCatalogService`），切回面板不再触发 UI 线程磁盘探测；面板支持搜索过滤、最近使用置顶、
分类展开状态保存、右键复制路径/打开所在目录，带 `AdapterId` 的工具标注「受控」并可一键跳转授权评估。
设置窗口可选择主/次工具根目录、重新检测并查看最终解析路径。内置轻量工具（JWT 解码、URL 结构解析、
时间戳转换、正则测试器等）纯进程内计算。
在应用 `tools` 目录放置 `tools.json` 可接入自定义工具（路径必须位于内置根目录或已配置的授权根目录内），
无需改代码重编译。发布包优先使用应用内置副本；普通开发构建没有 `tools` 目录时，会回退到设置中明确
配置的主/次工具根目录。公开发布包只包含清单中具备来源与许可信息的组件；标记为
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

- [下一阶段目标](NEXT-STAGE.md)
- [开发状态](docs/DEVELOPMENT-STATUS.md)
- [架构说明](docs/ARCHITECTURE.md)
- [设计记录](docs/DESIGN-NOTES.md)
- [Agent Runtime](docs/AGENT-RUNTIME.md)
- [Stage 7 授权评估方案](docs/STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md)

## 当前限制

- Linux 是预览平台，真实 WebView2/CDP 桌面验收仅覆盖 Windows。
- Linux 安全工具运行环境和依赖仍需逐项适配。
- 隔离子代理、Linux GUI 转正与评估作业异步化见 [NEXT-STAGE.md](NEXT-STAGE.md)。评估报告已支持 ECDSA 签名导出与第三方离线验签。
- WebView2 使用多进程架构，打开复杂页面时内存占用会明显高于纯 Avalonia 界面。

## 安全声明

Hackermes 仅用于合法调试、研究和已经获得明确授权的安全测试。使用者必须遵守适用法律、目标系统规则
和第三方工具许可证，并对自己的操作负责。
