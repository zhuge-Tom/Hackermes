# Hackermes 架构说明

> 当前源码基线：`main` / `0403672`（Stage 7 正式基线）。本文只描述当前仓库中可验证的结构；尚未落地的设计统一标为“计划中”。实现过程中的取舍见 [`DESIGN-NOTES.md`](DESIGN-NOTES.md)，阶段验收见 [`DEVELOPMENT-STATUS.md`](DEVELOPMENT-STATUS.md)。

## 一、产品边界

Hackermes 是基于 .NET 10 与 Avalonia 的桌面网页调试、流量分析和**授权安全评估**工作台。它把内置浏览器、CDP、DOM 检查、HTTP 数据包工作台、终端、AI 助手和受控 ToolHost 放在同一应用中。

安全边界是架构的一部分：安全评估只面向用户明确授权、精确限定且有时效的目标。AI 不获得任意 Shell；受控外部工具必须经过目标范围、不可变计划、一次性审批票据和独立 ToolHost 校验。

当前平台状态：Windows 10/11 x64 是完整验证平台；Linux x64 为预览平台，发布包可构建，但真实 Linux GUI/WebView 全链路尚未完成验收。

## 二、解决方案与真实依赖

`Hackermes.slnx` 当前包含 **14 个生产项目**和 **1 个测试项目**。下面只列项目文件中的直接 `ProjectReference`，箭头表示“引用”。

```text
Base          → （无）
Platform      → Base
Cdp           → Base, Platform
PageAgent     → （无；TypeScript 构建产物生成 C# 源）
Dock          → Base, Platform
Browser       → Base, Platform, Cdp, Dock, PageAgent
Traffic       → Base, Cdp
Inspector     → Base, Platform, Cdp, Dock
Automation    → Base, Platform, Cdp, Traffic
Terminal      → Base, Platform, Dock, Automation
AiPanel       → Base, Platform, Automation
Assessment    → Base, Platform
ToolHost      → Assessment
App           → Assessment, Dock, Platform, Browser, Inspector,
                Automation, Terminal, AiPanel, Traffic
```

`App` 还以 `ReferenceOutputAssembly="false"` 引用 `ToolHost`，用于确保独立可执行文件随构建/发布产生，而不是把它并入宿主进程。

测试项目 `tests/Hackermes.PacketTraffic.Tests` 覆盖 Traffic、Assessment、AI 工具策略和浏览器工具等组合场景。仓库中不存在旧架构稿曾列出的独立 `ToolGen`、`DataTable`、`Editor`、`Sidebar`、`Settings` 或 `DomToMarkdown` 项目；相关能力已由现有项目承载或尚未独立成工程。

## 三、运行时分层

```text
┌──────────────────────────────────────────────────────────────┐
│ Hackermes.App                                                │
│ 桌面宿主、模块装配、主窗口、Traffic/Assessment 集成          │
├──────────────────────────────────────────────────────────────┤
│ Browser  Inspector  Traffic  Automation  Terminal  AiPanel   │
│ Assessment                                                   │
├──────────────────────────────────────────────────────────────┤
│ Cdp  PageAgent  Dock  Platform                               │
├──────────────────────────────────────────────────────────────┤
│ Base：模块、事件、日志等共享契约                              │
├──────────────────────────────────────────────────────────────┤
│ ToolHost：独立、短生命周期、票据校验后的受控执行进程          │
└──────────────────────────────────────────────────────────────┘
```

宿主使用硬编码模块清单进行两阶段装配：先注册服务，再初始化 Tab、菜单和命令。当前实际装配顺序为 `Core`、`Dock`、`Browser`、`Traffic`、`Inspector`、`Automation`、`Terminal`、`TrafficIntegration`、`AssessmentIntegration`、`AiPanel`。不做程序集扫描，以保持启动路径清晰并避免 AOT/裁剪下的反射发现问题。

## 四、14 个生产项目的职责

| 项目 | 当前职责 |
| --- | --- |
| `Hackermes.Base` | `IModule`、事件总线、日志和 ViewModel 等共享契约。 |
| `Hackermes.Platform` | 配置、密钥、工作区、注册表、UI 线程桥，以及 Console/Network 页面查询契约。 |
| `Hackermes.Cdp` | WebView2 COM/CDP 会话、事件接收、域调用与会话注册。 |
| `Hackermes.PageAgent` | 页面内 Hook 与动作采集脚本，以及提交到仓库的生成产物。 |
| `Hackermes.Dock` | 固定区域布局、Tab 注册和需要保活的 Tab 控件。 |
| `Hackermes.Browser` | 内置多标签浏览器、地址导航、代理/设备视图、CDP 与 Page Agent 接入。 |
| `Hackermes.Traffic` | HTTP 捕获、拦截、规则、历史、重放、Comparer 与审计等底层服务。 |
| `Hackermes.Inspector` | DOM/样式、Network、Console、Storage、Timeline，以及 Traffic/Repeater/Comparer 工作台 UI。 |
| `Hackermes.Automation` | 统一动作模型与执行器、命令注册表、录制/回放，以及结构化数据包操作。 |
| `Hackermes.Terminal` | 系统 PTY 与领域命令 REPL；领域命令复用 `CommandRegistry`。 |
| `Hackermes.AiPanel` | OpenAI 兼容客户端、工具调度/策略、会话记忆、Skill 与 MCP 桥。 |
| `Hackermes.Assessment` | 授权范围、计划、审批、任务、证据、Finding、报告和 HMAC 审计链。 |
| `Hackermes.ToolHost` | 独立进程内验证票据、目标、计划哈希、nonce、时限和输出上限后执行固定 Adapter。 |
| `Hackermes.App` | 桌面入口、依赖注入、模块组合、主窗口与跨模块集成。 |

## 五、内置浏览器与 CDP

### 已实现

- Avalonia WebView 承载多标签页面；标签页切换时保留 WebView 生命周期。
- Windows 上通过 WebView2 原生句柄与 COM vtable 调用 CDP，不依赖 WPF WebView2 包。
- `ICdpSessionRegistry` 按 `pageId` 注册页面会话；Browser 创建会话，Inspector、Automation 和 AI 侧按页面标识消费。
- Page、Runtime、Network、Fetch、DOM、CSS、Input、Overlay、Storage 等实际需要的 CDP 能力由薄封装或原始调用提供。
- `WebViewCreationCoordinator` 串行化 WebView 创建；UI/CDP 调用使用现有线程桥与闸门，避免多个 WebView 争用 UI 线程。
- 自动打开浏览器页通过 `StartupPerformance.RunWhenLayoutReady` 等待主布局显式稳定，再在 UI 线程仅执行一次；这关闭了固定延时早于 Dock 订阅而丢失标签页事件的竞态。
- `IUiEventDispatcher` 是视图事件投递的显式 Platform 接缝；例如 Inspector 页面拾取结果经它回到 Avalonia UI 线程，业务服务不直接依赖静态 Dispatcher。
- 内部代理支持直连和 Burp `127.0.0.1:8080`，只影响 Hackermes 内置浏览器，不修改系统代理。
- 自动化验收可通过绝对路径环境变量 `HACKERMES_BROWSER_PROFILE_ROOT` 使用独立 WebView2 profile；显式隔离路径配置失败时 fail closed，禁止回退到用户默认 profile。配置、密钥、日志、Traffic、Assessment 与 ToolHost 状态可通过 `HACKERMES_DATA_ROOT` 一并定向到隔离目录，越界、相对路径和盘根路径都会被拒绝。

### Page Agent 当前事实

当前 Page Agent 的网络/存储/路由观测在页面主世界预注入，录制、selector 与 Inspector picker 位于随机命名隔离世界；两个 world 的 binding 均按页面随机化。主世界 Hook 可被页面检测、绕过或篡改，因此 CDP Network 仍是流量事实来源，Page Agent 只补充调用栈和页面内上下文。

录制、选择器和元素拾取已迁移到命名 `agent-iso` 隔离世界；主世界只保留网络、存储与路由 hook。Page Agent payload 使用 16 KiB 分片，宿主在 2 MiB/128 片/16 并发/10 秒的边界内严格按序重组，拒绝乱序、重复、过期与超限输入。更完整的按站点关闭/降级和兼容性策略仍是计划中能力。

## 六、统一页面动作与 AI 浏览器工具

页面写操作汇入 `Hackermes.Automation` 的 `CommandRegistry` 与 `ActionExecutor`。领域 REPL 和 AI 的 `CommandToolAdapter` 共享命令实现，避免为模型另写一套浏览器控制路径。每次 AI 调用都带当前活动 `pageId`；没有活动页面时页面相关调用失败。

### 已注册的浏览器 AI 工具

下表来自 `CommandToolAdapter`、`CommandRegistry` 与 `InspectionToolAdapter` 的当前注册代码，而不是规划清单。

| AI 工具 | 底层命令/服务 | 风险级别 | 当前行为 |
| --- | --- | --- | --- |
| `page_navigate` | `open` | Mutating | 导航活动标签页。 |
| `page_click` | `click` | Mutating | 按选择器定位并发送真实页面点击动作。 |
| `page_type` | `type` | Mutating | 清空后向目标输入文本。 |
| `page_hover` | `hover` | Mutating | 将指针悬停到目标。 |
| `page_press` | `press` | Mutating | 向页面发送按键。 |
| `page_eval` | `eval` | Mutating | 在活动页面执行 JavaScript；按写操作处理。 |
| `page_wait` | `wait` | ReadOnly | 等待选择器出现。 |
| `page_query` | `dom` | ReadOnly | 查询最多 10 个匹配元素的可见性、可交互性、尺寸与文本。 |
| `page_screenshot` | `snap` | ReadOnly | 截取活动页面。 |
| `page_assert` | `assert` | ReadOnly | 校验 exists/gone/text/expression 条件。 |
| `page_context` | `IPageContextQueryService` | ReadOnly | 读取活动标签页的精确 `pageId`、URL、标题及 CDP/Page Agent 就绪状态。 |
| `page_security_snapshot` | `IPageSecuritySnapshotService` | ReadOnly | 对精确当前页读取有界且不含值的表单/脚本、安全头/CSP 与 Cookie 属性聚合快照；页面变化或隔离世界不可用时失败。 |
| `script_record` | `rec` | Mutating | 开始、停止、查看或清理动作录制。 |
| `script_run` | `replay` | Mutating | 回放当前录制动作。 |
| `console_read` | `IConsoleQueryService` | ReadOnly | 读取活动页面的 Console、异常和浏览器日志。 |
| `network_list` | `INetworkQueryService` | ReadOnly | 读取活动页面最近网络请求，可只看失败项。 |

`console_read` 与 `network_list` 已强制要求活动 `pageId`，底层 Store 也按页面精确过滤，避免跨标签页混读。工具参数均有界：这两个读取工具的 `last` 为 1–1000。

### 工具策略

AI 设置提供“请求批准（默认）/帮我批准/完全访问权限”三档。所有工具调用进入 `AiToolDispatcher` 和 `IToolPolicyGate`；只读工具可按策略直接执行，Mutating/Dangerous 工具进入确认或拒绝路径。会话内的“记住批准”已绑定 `SessionId + ToolName + pageId + 参数指纹`：参数先做稳定 JSON 规范化，再只保存 SHA-256 摘要，不保存敏感参数明文。切换页面或更改参数必须重新批准；grant 还带 15 分钟绝对失效时间，过期后自动重新审批。

本地集成测试已覆盖完整模型工具循环：受控 `IOpenAiChatClient` 首轮产生 `page_click`，经 `AiChatViewModel → AiToolDispatcher → 人工批准 → CommandRegistry → ActionExecutor` 作用到精确 `pageId` 的 CDP 会话；工具结果进入第二轮模型上下文并生成最终总结。该测试不依赖外部 provider 或 API Key。

## 七、Traffic 与授权评估

### Traffic 已实现

Traffic 捕获基于 CDP Network/Fetch，支持请求/响应拦截、原始和结构化参数编辑、二进制 body 分块读取与改写、继续/丢弃/Fulfill、持久规则、历史保留策略、Repeater、Comparer、批量标注、审计和归档。人工工作台、CLI 与 AI 注册入口复用同一组服务。

Traffic AI 工具按功能族注册，包括 `packet_*`、`traffic_rule_*`、`repeater_*`、`comparison_session_*` 和 `traffic_history_*`。它们并非“浏览器页面动作工具”，但可处理由内置浏览器捕获的流量，并同样受统一 AI 工具策略约束。`packet_*` 的 AI JSON 与 CLI 参数解析现在都转换为共享 typed intent/outcome，AI 不再拼接或重新拆分命令字符串。

Traffic 工作台在最小窗口下把高频筛选、Request/Response 保留在首层，将 Archive/Annotation 收入默认关闭的 `More tools` flyout，并让底部操作区换行；这是 presentation 优化，不改变 typed intent/outcome、审计或拦截状态语义。

### Stage 7 授权评估已实现

Stage 7/7C 当前已形成完整控制面：

```text
精确、限时的授权范围
  → 固定 Adapter 与结构化输入形成不可变计划
  → 一次性审批 grant
  → DPAPI 保护的 HMAC 任务票据
  → 独立 ToolHost 再校验范围、计划哈希、nonce 和时限
  → 有界执行（取消、进程树终止、硬超时、输出上限）
  → 证据、Finding、人工复核、连续 HMAC 审计链与报告
```

当前 ToolHost 只暴露四个固定 Adapter：

- `recon.nmap.quick`
- `recon.nmap.service`
- `recon.dirsearch.quick`
- `recon.wafw00f.quick`

没有任意命令 Adapter，也未接入口令爆破、漏洞利用、规避或破坏工具。Nmap、Dirsearch 和 Wafw00f 已在 `127.0.0.1` loopback 靶场完成真实 ToolHost 调用验证。

授权评估 AI 工具当前包括：`assessment_scopes`、`assessment_tools`、`assessment_create_scope`、`assessment_create_scope_from_page`、`assessment_create_plan`、`assessment_approve`、`assessment_run`、`assessment_report`、`assessment_evidence`、`assessment_verify_evidence`、`assessment_findings`、`assessment_create_finding`、`assessment_review_finding`、`assessment_verify_audit`。其中浏览器绑定会话必须使用 `assessment_create_scope_from_page`：它不接收目标参数，而是通过精确 `pageId` 读取当前 HTTP(S) 页，拒绝用户信息 URL、未知页与已关闭页，并从 URL 派生 scope host 及回显 scheme/port/origin。派生页面绑定会在策略判断和人工确认前冻结并进入授权指纹，执行前再次核对；确认后导航或 remembered grant 后切换 origin 都不能静默复用旧授权。旧的自由目标入口只保留给没有浏览器上下文的 CLI/兼容调用。

Assessment 的 `ReadCases` / `ReadCase` 在同一控制面锁内形成 coherent case：原子组合 job、scope、plan、approval、evidence、finding、audit、审计验证与当前可用动作；缺失引用或不一致授权链 fail closed。人工工作区、CLI `assessment cases` 与 AI `assessment_cases` 复用这一读取边界，避免各入口各自拼接出不同时刻的状态。

## 八、数据与持久化

- 普通配置由 `ISettingsService` 管理；AI API Key 不写入普通设置文件。
- Windows 密钥使用当前用户 DPAPI；Linux 预览路径使用用户级 AES-256 密钥库。
- Traffic 历史、规则、Repeater、Comparer 和标注由各自服务持久化并带有容量/保留约束。
- Assessment 存储当前版本为 v2，使用临时文件写穿透、上一份有效备份、损坏文件保留与自动恢复。
- Assessment 审计使用连续 HMAC-SHA256 链，可检测参与者、动作、实体、详情或顺序被篡改；密钥轮换不允许静默破坏旧链。
- 启动时遗留的 Queued/Running 评估任务转为 Failed 并写入恢复审计，一次性审批保持已消费状态。

## 九、界面结构

主窗口采用固定区域布局：顶部导航和全局操作区、左侧工具/入口、中间工作区、右侧 AI 助手、底部终端/日志与状态。固定布局刻意不实现可拖拽 Dock 树；WebView 与 PTY 所在 Tab 通过保活控件避免切换时被销毁。

当前主窗口、顶栏、状态栏、加载态和 AI 对话面板已统一明暗主题表面层级、间距、按钮热区、空状态、错误状态和忙碌反馈。AI 面板会明确显示当前绑定的内置浏览器标题与 `pageId`，无活动页面时显示隔离提示。授权评估与 Traffic 工作区均已在隔离的 `127.0.0.1` 页面上建立真实宿主 DPI 的浅/深主题窗口截图门禁；Traffic 截图必须先完成真实捕获 5/5 并显示隔离 history。自动化还记录宿主实际 DPI/scale，并以 wide/medium/minimum 三档窗口验证响应式布局；窗口尺寸模拟不会被误称为操作系统 DPI 切换。

可控构建输出、依赖缓存、临时目录、证据与发布产物统一位于 `G:\HackermesBuild`。证据保留采用“关键 TRX/运行日志/视觉元数据与截图长期保留，重复或失败构建、可再生 profile 和临时目录及时清理”的策略，降低 G 盘占用；系统安装的 .NET SDK 不属于项目构建输出。

## 十、阶段状态

| 阶段 | 状态 | 可验证结果 |
| --- | --- | --- |
| 0–1 | 已实现 | 应用骨架、Dock、Browser 与 CDP 通道。 |
| 2 | 已实现 | Page Agent、Network、Console、DOM 树、页面拾取与样式检查。 |
| 3 | 已实现 | 统一动作、录制/回放、领域 REPL 与 PTY。 |
| 4–5 | 已实现 | AI 工具策略、MCP、数据包工作台及 CLI/Agent 共享服务。 |
| 6 | 已实现 | Traffic 捕获、拦截、编辑、规则、历史、Repeater、Comparer 与真实 WebView2 loopback 验收。 |
| 7/7C | 已实现 | 授权评估控制面、独立 ToolHost、证据/审计/Finding/复核/报告和发布安装回滚基线。 |
| 9（增量） | 进行中 | layout-ready 自动打开、UI dispatcher 接缝、只读安全快照、coherent assessment cases 与 Traffic 最小窗口优化；294/294 测试和定向桌面证据已保留，完整发布门禁未跑完。 |
| 后续增强 | 计划中 | Page Agent runtime 更深的 typed event/capability 消费、Traffic 工作台状态拆分、真实多显示器 DPI 切换与 Linux GUI 全链路。 |

当前 Stage 9 源码的完整测试 TRX 为 294/294（2026-08-13，0 failed），并保留 layout-ready 真实桌面 loopback 5/5 与 Traffic minimum 视觉证据。Stage 8 曾完整覆盖 Release 构建、真实桌面 loopback 5/5、授权评估与 Traffic 浅/深主题截图、Windows 包、逐文件 manifest、归档 SHA-256 和汇总 JSON；Stage 9 整套发布门禁按当前要求未跑完，后续发布前仍须重新运行，不能以定向证据替代。

## 十一、架构约束与下一步

后续开发应保持以下不变量：

1. 页面动作继续复用 `CommandRegistry` / `ActionExecutor`，不为 AI 建立旁路实现。
2. Console、Network、DOM 与动作执行均绑定当前活动 `pageId`，不得回退到跨标签页全局读取。
3. 安全评估必须先有明确范围、固定计划与审批；ToolHost 不接受任意 Shell 字符串。
4. Traffic 的人工、CLI 与 Agent 入口继续共用底层服务和审计语义。
5. 新增危险能力必须同时给出目标约束、参数上限、取消/超时、证据与审计路径。

优先优化项：

- 在现有响应式窗口矩阵之外，使用具备多显示器/系统缩放切换条件的 Windows 主机补充真实 100%/150%/200% DPI 验收；
- 保持测试、真实桌面 loopback、两类工作区截图和安装包校验为可追溯发布产物；
- 继续深化 Page Agent runtime seam，让更多消费者只接收完整 typed event/capability；
- 继续拆分 Traffic 工作台 presentation state，并逐步移除仅作为 capability tag 的运行时 casts；
- 完成真实 Linux GUI/WebView 全链路验收后，再调整 Linux 平台级别。
