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

按阶段推进,每阶段以"可运行、可验证"为终点。

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 骨架:模块契约、事件总线、五区域布局、Tab 保活、设置持久化 | ✅ 完成 |
| 1 | CDP 通道:COM 互操作、请求-响应、事件泵、浏览器多标签 | ✅ 完成 |
| 2 | Page Agent 与检查面板:页面内 Hook、网络面板、控制台面板 | ✅ 完成 |
| 3 | 统一动作模型:执行器、选择器引擎、录制回放、交互式终端 | ✅ 完成 |
| 4 | AI 集成:工具集、策略闸门、MCP | ✅ 完成 |
| 5 | 数据包工作台:拦截、分析、编辑、丢弃、响应替换与重放 | ✅ 完成 |
| 6 | 专业化流量工程:持久规则、响应拦截、HAR/JSON 归档、可重复测试 | 🚧 进行中 |

已可用:内置浏览器多标签、CDP 请求-响应与事件订阅、页面内 Hook 与调用栈捕获、网络面板(协议数据与调用栈合并)、控制台面板(console / 未捕获异常 / 浏览器级日志三源合并)、五区域布局与布局持久化。

阶段 3 已接通 `ActionDescriptor → ActionExecutor → CDP` 的统一执行路径、领域 REPL、真实 PTY System Shell、统一动作时间线和版本化 JSON 脚本。Page Agent 会把人工点击、输入、选择与关键按键转换成带候选选择器的动作，可用 `rec start` / `rec stop` 捕获，并用 `save`、`load`、`replay` 保存和回放；`assert` 支持元素存在、消失、文本和表达式断言。

阶段 4 已接入 OpenAI 兼容流式对话和多轮工具调用。页面工具复用阶段 3 的命令注册表，AI 可查询页面、点击、输入、截图、读取 console 与网络流；所有调用统一经过默认保守的策略闸门，写操作弹窗确认，危险工具拒绝，也可显式启用信任模式。MCP 支持配置 stdio server，自动发现并注册其工具。

阶段 5 新增基于 CDP `Fetch` 域的数据包工作台，无需系统代理或根证书即可对内置浏览器的 HTTP(S) 请求/响应执行类似 Burp Intercept / Repeater 的操作。人工可在底部“数据包”页签编辑原始 HTTP，CLI 可使用 `packet ls|query|show|analyze|diff|replay|intercept|continue|drop|edit`；内部 Agent 共享同一核心服务，并按只读、修改和高风险分级确认。Agent 查看原始包时默认遮蔽认证头与 Cookie。

阶段 6 已开始：请求和响应现在可以独立拦截；持久化流量规则支持增删改查、启停、排序及 JSON 导入导出，人工、CLI 与 Agent 可共同管理；人工可在 Rules Workbench 通过文件路径执行规则 JSON replace/merge import 与 export。Traffic Workbench 的 `Archive` 路径栏以及 `packet export` / `packet import` 支持 Hookmes JSON v1 与 HAR 1.2，并以 Base64 元数据无损保存二进制 body。大包可先查询长度/SHA-256，再以最大 256 KiB 的范围分块读取；二进制编辑支持 Hex/Base64 的 Replace/Insert/Delete 及 Content-Length 规范化。三端还可结构化读取并修改 query、form 与顶层 JSON 参数：人工使用 `Parameters` 页，CLI 使用 `packet param-list/param-set`，Agent 使用 `packet_parameters/packet_parameter_set`。数据包可持久保存 starred、tags、note 与 review status 标注，对应人工 `Annotation` 页、CLI `annotation` 命令和 Agent `packet_annotation_*` 工具。独立 Repeater 支持命名草稿、编辑、多轮发送历史以及耗时/大小/状态记录；Comparer 提供起始行、重复 Header 和二进制 body 摘要差异；历史列表支持复合筛选和分页。Traffic 历史使用版本化压缩文件落盘，Repeater/Comparer/Annotation 使用版本化文件，均具备原子替换、备份恢复和跨重启加载。上述源码仍按当前开发安排等待后续统一构建验收。

流量元数据查询现由有界 `PacketQuery` 契约统一：人工工作台与 `packet query [text|*] [method|*] [status|*] [resourceType|*] [held|all] [offset] [limit]`、Agent `packet_query` 具有相同的文本/方法/状态/资源类型/暂停状态复合筛选和 offset/limit 分页语义。CLI 返回稳定分页头，Agent 返回结构化 camelCase 页面；两者都不返回 Header 或 Body 值。

新增的三端对等入口包括四态拦截 `packet intercept-mode` / `packet_intercept_mode`、持久比较会话 `compare-session` / `comparison_session_*`、Repeater rename/clear-history，以及 Agent 标注组合查询与 prune。

二进制编辑现在以公共草稿契约保存首次编辑前快照、前后长度/SHA-256/Content-Length 和最近提交失败；人工 Binary editor、CLI `packet draft-*` 与 Agent `packet_edit_*` 均可查询或 Discard，提交失败时保留草稿供重试。Comparer 工作台也已支持从当前 Traffic/Repeater 来源创建、重命名、重算和删除持久 Session。

Agent 归档使用 `packet_archive_export/import` 直接交换最多 500 条、2 MiB 的 Hookmes JSON/HAR 内容，不接受任意文件路径；批量导出可能包含敏感包数据，按 Dangerous 确认，导入按 Mutating 确认。Repeater Workbench 可选择每次发送的稳定历史轮次，查看该轮请求、响应、耗时和大小，把任意两轮直接比较并一键保存为持久 Comparison Session。数据包修改、Discard、继续、丢弃、Fulfill、重放及规则自动命中统一写入持久审计；规则审计不保存 URL 查询、路径原文、Header 值或 Body，只保留规范元数据摘要。人工、CLI `packet audit` 和 Agent `packet_audit` 均可查询；三端还共享 ECDSA P-256 签名导出与离线验签，人工使用 Audit 页，CLI 使用 `packet audit-export/audit-verify`，Agent 使用 `packet_audit_export/packet_audit_verify`。签名文档仅含有界元数据，内嵌 SPKI 公钥及 SHA-256 指纹，并可通过期望指纹固定信任身份；私钥由平台密钥存储保护。Traffic 历史除全局条数、容量、保留期和自动清理外，还支持精确主机或 `*.domain` 配额，人工、CLI `traffic-history site-*` 与 Agent `traffic_history_site_quota_*` 共用同一策略。Binary editor 现有固定长度/SHA-256 摘要、64 KiB 分块导航、字节范围和进度；协议与敏感数据分析返回统一的结构化 Finding，CLI/Agent 可获得 Header 重复项或 UTF-8 body 字节定位，人工选择 Finding 可精确选中 Header/StartLine 或跳转 Body 字节范围。`IPacketCommitService` 将 Continue、Drop、Edit/Fulfill 与 Discard 统一为包含最终状态、前后摘要、审计 ID 和安全错误码的结果；人工显示摘要，CLI 输出稳定 `key=value`，Agent 返回 camelCase JSON，旧后端继续使用兼容路径。Traffic Archive 与 Rules Workbench 已使用系统文件选择器，提供 HAR/JSON 过滤、保存覆盖确认、规则 replace 确认及最近目录恢复；设置仅保存规范化文件路径，不保存包或规则内容。

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
| `Hookmes.Inspector` | 网络/控制台、数据包、规则、Repeater 与 Comparer 工作台 |
| `Hookmes.Automation` | 统一动作、录制回放、CLI、HTTP 包编解码、归档、分块读取与二进制编辑 |
| `Hookmes.Terminal` | 领域 REPL 与真实 PTY System Shell |
| `Hookmes.AiPanel` | OpenAI 兼容对话、AI 工具编排、策略闸门和 MCP stdio 桥接 |
| `Hookmes.Traffic` | CDP Fetch 捕获/拦截/重放、持久规则与历史、Repeater、Comparer、查询分页 |
| `Hookmes.App` | 启动装配、主窗口、视图定位 |

---

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10/11 x64 |
| .NET SDK | 10.0 — [下载](https://dotnet.microsoft.com/download) |
| WebView2 Runtime | 任意近期版本 — [下载](https://developer.microsoft.com/microsoft-edge/webview2/) |
| Node.js | 18+,**仅在修改 Page Agent 源码时需要** |
| Python | 3.10+,**仅运行 `scripts/run-traffic-selftest.ps1` 的本地 HTTP 验收服务时需要** |

前端产物以生成的 C# 源文件形式提交进仓库,因此常规构建不需要 Node.js。

## 构建与运行

```bash
dotnet restore
dotnet build Hookmes.slnx
dotnet run --project src/Hookmes.App/Hookmes.App.csproj
```

流量核心测试与真实桌面 CDP 验收：

```powershell
dotnet test tests/Hookmes.PacketTraffic.Tests/Hookmes.PacketTraffic.Tests.csproj
powershell -ExecutionPolicy Bypass -File scripts/run-traffic-selftest.ps1
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
| [`docs/STAGE6-GAP-MATRIX.md`](docs/STAGE6-GAP-MATRIX.md) | 人工、CLI、Agent 流量分析/修改能力对照、下一阶段缺口与验收门槛 |

---

## 免责声明

本工具用于**自己拥有或已获授权**的网页与应用的调试。Page Agent 会在页面中注入代码、CDP 会读取页面数据,请勿用于未授权的目标。使用者需遵守当地法律法规并对自身行为负责。
