<!-- markdownlint-disable MD013 -->

# Hackermes 开发基线

## 当前范围

构建环境已统一到 `G:\HackermesBuild`：默认 bin/obj、NuGet 包与 HTTP/plugin 缓存、.NET CLI home、TEMP/TMP、Python/XDG 缓存、运行证据和发布包均有独立子目录。npm prefix 位于 G 盘，npm cache 使用 npm 默认路径且不再被项目强制，避免跨项目共享锁冲突。仓库级 `NuGet.Config` 与所有正式 PowerShell 脚本共同执行其余 G 盘约束；系统安装的 .NET SDK 本体不迁移。

### 2026-08-13 Stage 9 增量推进

- 浏览器自动打开不再依赖固定延时：`StartupPerformance.RunWhenLayoutReady` 等待主布局显式就绪后，在 UI 线程且仅执行一次地创建标签页，避免 Dock 尚未订阅时丢失 add-tab 事件。保留证据 `stage9-layout-ready-runtime-r3` 证明当前源码下 Page Agent 主/隔离世界均 Ready，真实 Traffic loopback 为 5/5。
- Platform 新增 `IUiEventDispatcher` 作为视图事件回到 Avalonia UI 线程的显式接缝；`PageInspectionService` 通过该契约投递 UI 事件，测试可用同步实现替换，不再把静态线程桥藏在业务服务中。
- AI 新增只读 `page_security_snapshot`：严格绑定精确 `pageId`，在读取前后复核页面与 URL；仅返回有界 URL/origin/title、表单/外链脚本元数据、安全响应头/CSP 标志及 Cookie 属性聚合，不返回 Cookie、令牌、表单值、存储值、正文或内联脚本内容。未知、关闭、导航中的页面和不可用隔离世界均 fail closed。
- Assessment 控制面新增原子 `ReadCases` / `ReadCase`，在同一锁内组合 job、scope、plan、approval、evidence、finding、audit 与可用动作；引用缺失或授权链不一致时 fail closed。工作区、CLI `assessment cases` 与 AI `assessment_cases` 已消费该一致快照。
- Traffic 最小窗口界面完成压缩：常用筛选与 Request/Response 保持首层可见，低频 Archive/Annotation 收入默认关闭的 `More tools`，底部操作区可换行。保留的 880×560 请求尺寸、125% 真实宿主 DPI 浅/深截图和 5/5 loopback 元数据位于 `stage9-traffic-minimum-final`；该证据验证响应式窗口，不声称切换了系统 DPI。
- 当前源码完整测试 TRX 为 **294/294**（0 failed），权威文件为 `G:\HackermesBuild\evidence\stage9-full-tests-final\stage9-full-tests.trx`。为控制 G 盘占用，仅保留完整 TRX、layout-ready runtime、Traffic minimum 和响应式矩阵等关键证据，重复/失败构建及临时 profile 已清理。
- **Stage 9 完整发布门禁未运行完**：按当前要求未重新执行包含 Release 构建、全部桌面视觉、Windows 打包/manifest/归档哈希的整套验收，因此不能把 294/294 或定向运行证据表述为 Stage 9 发布通过。Stage 8 的完整发布门禁事实仍有效，但不替代对当前 Stage 9 源码重新门禁。

### 2026-08-12 增量推进

- AI 浏览器工具链继续沿用“当前选中页面快照 → 统一工具策略 → CommandRegistry / 检查查询服务”的单一执行路径。导航、点击和输入仍按写操作进入确认策略；DOM、Console 和 Network 读取保持只读。
- 修复 Console / Network 的跨标签页数据混读：AI 查询现在必须携带活动 `pageId`，底层 Store 按页面精确过滤；没有活动页面时明确拒绝，不再把其他标签页的观测结果带入当前对话。
- “本会话记住批准”不再只按工具名缓存，现绑定会话、工具、活动 `pageId` 与规范化参数 SHA-256 指纹；切换页面或修改参数会重新进入策略检查，缓存不保存参数明文。
- 主窗口、顶栏、状态栏、加载态和 AI 聊天面板完成一轮视觉整理，统一明暗主题表面层级、间距、按钮热区、空状态、错误状态和忙碌反馈，保留原有命令与绑定。
- 新增浏览器 AI、细粒度授权、WebView2 profile/data 隔离、`page_context`、浏览器派生 Assessment scope、模型工具循环、10k 网络记录容量、Page Agent runtime/transport 与 Traffic typed-operation 回归；当前完整测试集已推进为 294/294，并输出 TRX。验证使用独立 `HackermesBuildRoot`，避免并行开发争用默认中间输出目录。
- Windows 真实桌面验收改用独立 `HACKERMES_BROWSER_PROFILE_ROOT` 与纯 `127.0.0.1` 页面；显式隔离路径无效时 fail closed，不再回退用户默认 WebView2 profile。真实 App/CDP loopback 的捕获、重放、拦截、请求改写和响应 Fulfill 5/5 通过。
- 已使用 `PrintWindow` 只捕获隔离 profile/data root 下的 Hackermes 窗口，生成授权评估与 Traffic 工作区 125% DPI 浅/深主题证据；Traffic 必须先完成真实 loopback 5/5 并显示 8 条请求。截图与 SHA-256 元数据均纳入正式发布门禁。
- AI 新增只读 `page_context`，精确返回活动标签页 URL、标题与 CDP/Page Agent 状态；未知、相似或已关闭 `pageId` 不会回退到其他标签页。AI 面板同时显示当前绑定目标或明确的未绑定状态。
- 授权评估新增 `assessment_create_scope_from_page`：浏览器绑定会话不能再由模型替换 `targets`，范围 host 只能从当前页 HTTP(S) URL 派生，scheme/port/origin 会随结果回显；带用户信息的 URL、未知页和关闭页均失败。页面绑定在策略与人工确认前冻结并进入授权指纹，执行前复核，可阻断确认后导航竞态，并使 remembered grant 在 origin 变化后重新确认。该链与浏览器隔离、工具确认、细粒度授权缓存及模型工具循环的定向回归为 23/23 通过。
- 授权评估工作区完成视觉与可用性整理：范围、计划、审批形成三阶段卡片；任务、证据、发现和审计都有明确空态；撤销/取消集中到风险操作区，术语与详情排版统一。现有控制面调用和事件行为未改变。
- 本地假模型已完成真实工具循环：首轮生成 `page_click`，经过策略批准和统一动作执行器命中指定 CDP 页面，工具结果回到第二轮并得到最终总结。会话 grant 具有 15 分钟绝对有效期。

当前 P0 已继续推进：Page Agent 主世界缩减到网络/存储/路由 hook，录制与 selector 迁入命名隔离世界；16 KiB 分片及宿主有界重组已覆盖乱序、重复、超时、并发与容量保护。Browser-owned runtime 现按精确 `pageId` 管理主/隔离世界 capability 与 context 生命周期，Inspector picker 不再自行注入主世界。Traffic 的 AI 与 CLI 入口也已统一到 typed intent/outcome，不再做字符串命令往返。发布门禁现同时捕获授权评估和 Traffic 浅/深主题；视觉基线继续扩展为真实宿主 DPI 记录加 wide/medium/minimum 响应式窗口矩阵。

- 阶段 0–1：应用骨架、Dock、浏览器和 CDP 通道已落地。
- 阶段 2：Page Agent、Network、Console 已接通；DOM 已改为树形结构并补齐页面拾取器、页面/树双向悬停与点击定位、父级展开、树项滚动、计算样式/匹配规则编辑和导航后陈旧节点清理。页面资源不再占用左侧栏，相关 src/href 在 DOM 详情中查看。
- 阶段 3：动作描述、执行器、选择器、录制、保存、加载、回放、领域 REPL 和 PTY 已接通。此次复核确认模块已注册，不是空壳。
- 阶段 4–5：AI 工具策略、MCP 桥、数据包工作台、CLI 与 Agent 共用的数据包服务保留。
- 阶段 6：基础功能与运行验收均已完成。Traffic 捕获启动已串行化，避免同一页面重复注册 `Fetch.requestPaused` 后对同一 requestId 重复 Continue/GetBody；Continue/Fulfill 只发送实际设置的 CDP 可选字段。请求/响应二进制 body 编辑、草稿回滚、独立拦截模式、规则、审计、归档、历史、Comparer 和三端（工作台 / CLI / Agent）入口均已落地。Repeater 已支持 0.1–600 秒超时和取消并持久化结果；Annotation 已支持精确标签/复核状态筛选、清除筛选与删除；Windows 当前用户 DPAPI 密钥复用、损坏恢复、指纹固定及轮换拒绝均有定向验收用例。真实 WebView2/CDP loopback 已连续两次通过捕获、重放、暂停继续、请求二进制改写和响应 Fulfill 的 5 个闭环；默认 DPAPI 审计密钥指纹在两个独立桌面进程间保持一致。完整测试集 201/201 通过。

- 阶段 7：授权评估控制面基础已落地。AI 设置提供“请求批准（默认）/帮我批准/完全访问权限”三档统一策略；Skill 工作流、压缩上下文、持久记忆、受控 HTTPS 工具缓存与人工 CLI 管理入口已接入。`assessment` CLI 与 Agent 工具共享持久化的目标范围、计划、审批、任务取消、范围撤销、证据、Finding 与审计记录。嵌套 JSON Pointer（深度/条目上限）和最多 500 条的批量标注也已加入底层服务，其中批量标注可由 CLI/Agent 调用。完整方案见 [`STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md`](STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md)，使用说明见 [`AGENT-RUNTIME.md`](AGENT-RUNTIME.md)。

- 阶段 7 ToolHost 基线：已新增独立 `Hackermes.ToolHost` 进程、DPAPI 保护的 HMAC 任务票据、短时效与一次性 nonce、防重放记录、精确目标范围、不可变计划哈希、一次性批准、批准/范围撤销、进程树取消、硬超时、输出上限、证据和审计。Agent 侧注册 `recon.nmap.quick`、`recon.nmap.service`、`recon.dirsearch.quick` 和 `recon.wafw00f.quick` 四个固定参数 Adapter；未接入口令爆破、漏洞利用、规避或破坏工具。工具与便携 Python 从应用相对 `tools` 目录解析，Nmap、Dirsearch、Wafw00f 均已在 `127.0.0.1` loopback 靶场完成真实 ToolHost 调用；完整测试集 220/220 通过。

- 阶段 7C / 阶段 7 正式基线：审计、证据、Finding 与人工复核闭环已落地。中央“授权评估”工作区现在可由人工完成范围创建、固定计划、一次性审批、执行、取消/撤销、证据验证、Finding 创建/复核、审计链验证和 JSON/Markdown/HTML 报告导出。CLI 与 Agent 已通过各自真实注册入口完成同一全链路验收。Assessment 存储升级到版本 2，采用写穿透临时文件、上一份有效备份、损坏文件留存与自动恢复；重启时 Queued/Running 任务会转为 Failed 并写入 `job.recover`，一次性审批保持已消费。连续 HMAC-SHA256 审计链可检测操作者、动作、实体、详情或顺序被修改。Windows 安装器增加逐文件 SHA-256 校验、暂存安装、原子升级、上一版本保留和回滚，默认保留用户配置。完整测试集 249/249 通过；Windows 安装→升级→回滚→卸载临时目录验收通过。本轮按决定不执行 Linux 验证。

## 明确延期

已实现有界嵌套 JSON Pointer 的读取与修改：对象/数组定位遵循 RFC 6901 转义规则，默认深度上限为 32、条目上限为 2000，调用方可在更严格的 1–64 层与 1–10000 条范围内收紧限制。现有顶层 JSON、query、form、header 和 cookie 能力保持不变。

## 命名

源码目录、项目文件、程序集、命名空间、配置键和文档品牌已统一迁移为 `Hackermes`。当前磁盘工作区根目录仍沿用会话创建时的旧路径，它不属于产品命名或仓库内容。

## 已执行的运行验收

当前 Stage 9 完整测试集为 294/294，另有 layout-ready 真实桌面 loopback 5/5、Traffic 最小窗口浅/深主题和既有响应式矩阵证据。Stage 8 曾完整通过 Release 构建、真实 CDP loopback、授权评估与 Traffic 视觉、Windows 包 manifest/归档 SHA-256 及清理检查；但 Stage 9 整套发布门禁按当前要求未跑完，不能沿用 Stage 8 结果宣称当前源码已发布验收。Linux 验证仍按当前决定跳过。
