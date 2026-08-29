# Agent 运行时与工作流

Hackermes 的内置 Agent 采用“模型只做计划、工具统一受策略门控制”的工作方式。权限模式、Skill、会话上下文和 CLI 共享同一套本地状态；模型不能通过提示词绕过策略。

## Agent 运行时（turn/step 驱动器）

核心循环位于 `Hackermes.AiPanel/Runtime`，借鉴 deepseek-harness 的 ReactLoopAgent /
session-event 设计，以 C# 无头服务实现：

- **`AgentTurnRunner`**：无头 turn/step 驱动器。一次操作者输入开启一个 turn；一个 step
  是“一次模型请求 + 其工具执行”。模型停止调用工具且转向队列清空时 turn 收束。
  ViewModel 只订阅事件渲染转录，不再内嵌控制流。
- **事件日志 `AgentSessionLog`**（append-only）：`turn/start|end`、`step/start|end`、
  `user/message`、`assistant/chunk|message`、`usage`、`tool/call|result`、`request/retry`、
  `context/compacted`。每个事实只落一次日志，UI、未来回放/分叉/遥测都从该流派生。
- **转向 inbox**：运行中提交的指令在 step 边界被认领并注入下一请求；
  操作者“优先指示”会抢占当前 step 剩余的工具调用（未执行的调用以合成结果留痕，
  协议保持完整——与 dsh 的 skipped-tool-call 语义一致）。
- **工具调度**：所有 `tool/call` 先于任何 dispatch 落日志；结果严格按模型顺序提交。
  连续的只读工具可进入有界并行池（`ai.maxParallelReadOnlyTools`，默认 1=串行），
  完成顺序不影响落盘顺序；修改型工具天然形成屏障。
- **干净失败重试**：仅"瞬态"失败按 dsh 默认曲线重试（2 次，0.5s→10s 指数退避±10% 抖动）。
  错误分类器（`AgentRequestError`）把无 `HTTP <code>` 前缀的传输异常、IO 流断裂与 408/429/5xx
  视为可重试；401/400/403 等终端错误直接收束，不再白烧重试。已产出部分内容则不重试以免重复
  输出；重试耗尽以 `turn/end(error)` 收束并给出错误详情。
- **上下文溢出自愈**：HTTP 400/413 且响应体命中上下文超限措辞时，绕过压力阈值与限速强制
  压缩一次（`AcpAutoCompactor.CompactNowAsync`），重建请求后自动重试一次；无可用压缩空间或
  未配置压缩器则照常失败。
- **取消的协议补全**：操作者停止时，已落日志但未完成的 `tool/call` 一律补合成结果
  （"因操作者停止而中止"占位），历史中不会留下孤立的 assistant tool_calls——否则下一次
  请求会被 OpenAI 兼容 API 以协议错误拒绝。并行池会先收割已完成的结果、提交就绪前缀，
  再为剩余调用补占位。
- **思考流（reasoning）**：SSE 的 `reasoning_content` / `reasoning` 字段解析为独立增量，
  落 `reasoning/chunk` 事件并在转录中以"思考"行实时展示；与 dsh 一致，思考内容**不进入**
  模型可见历史。
- **长度截断语义**：`finish_reason=length` 时丢弃本步残缺的工具调用片段（dsh assembler
  规则），保留已有文本；turn 收束理由记为粘性的 `LengthCapped`——后续步骤即使正常完成也
  不降级该判定，转录追加"长度截断"提示行。
- **回合上限**：`ai.maxToolRounds`（默认 48，1–256 夹取）仍生效，超限以
  `MaxRounds` 理由收束——这是 Hackermes 在 dsh 无上限设计上追加的安全阀。
- **会话事件持久化（日志即真相，`ai.sessionEvents`，默认关闭）**：开启后每个会话的
  durable 事件流（消息、工具协议、压缩块、审批审计）以 JSONL 追加到应用数据目录
  `agent-events/<sessionId>.jsonl`；流式增量不入盘。重启或切换回会话时从日志**回放**
  重建模型可见状态：历史消息原样回归，`context_compress` 调用与自动压缩按原始参数
  重放到 ACP 存档层（引用编号因顺序回放而天然一致），turn 计数延续，且持久化流会被
  **导入**内存日志——恢复前后的序列连续无断点，任何从日志派生的功能看到同一条流。
  写失败会一次性在错误条提示并暂停本实例的持久化（健康标志），不会静默丢事件或刷屏；
  开关运行时可即时生效。注意：这会把工具参数/输出写入磁盘，与记忆库的脱敏承诺不同，
  故默认 opt-in。

## 工具管线扩展与配套能力

- **大结果三级阶梯**：≤12K 字符原样进入上下文；12K–24K 首尾保留剪枝（中间显式省略）；
  超 24K 全文外存（spill）+ `read_spill` 分页读取——证据永不销毁。

- **任务清单 `todo_write`**（dsh tool-todo 血统）：整表快照写入，拒绝重复/空内容/
  多个 in_progress；空数组清空清单。清单在转录上方的"任务清单"面板实时展示，
  下一个 turn 开始时自动清空（完成的清单保持可见直到新一轮）。清单是会话级瞬态，
  不进入模型历史之外的记忆存储。
- **大结果外存 spill + `read_spill`**：超过阈值（24K 字符）的工具结果全文落盘
  （应用数据目录 `agent-spills/`），模型只看到首尾预览加不透明 locator
  （`spill:<32位hex>`，严格限定在存储根内解析，无法越界寻址）；需要时用
  `read_spill` 按 offset/limit 分页取回——证据不再被破坏性截断销毁。
- **管线附加语义**：工具可通过 `ToolResult.AdditionalContexts` 向下一 step 注入上下文
  （以"上下文注入"标记入列，不会伪装成操作者指示），通过 `ConcludesTurn` 在该 step
  收尾后提前结束 turn（已认领的转向仍会先执行，dsh conclusion 语义一致）。
- **审批审计落日志**：dispatcher 的每次确认批准（单次/会话级）、策略拒绝与操作者
  拒绝都以 `approval/audited` 事件进入会话日志，谁在哪个页面/范围批了什么可随
  会话持久化与回放，审计链不再只活在内存授权票据里。
- **收缩守卫全覆盖**：手动 `context_compress` 与自动压缩共用同一条硬性不变量——
  替换物必须严格小于被替换区间，否则拒绝并提示改写；legacy 无 ACP 路径的溢出
  恢复则以"修剪最旧完成回合"替代强制压缩。

## 第一梯队漏洞工具适配器（detect/exploit）

与既有 `recon.*`/`probe.*` 适配器共用同一套 ToolHost 管线（结构化 JSON 输入 →
`ArgumentList`，精确目标范围校验，超时/输出有界），已登记 8 个：

| 阶段 | AdapterId | 工具 | 说明 |
|---|---|---|---|
| 信息收集/泄露 | `recon.git_leak.scan` | GitHack | `/.git/` 源码恢复；副本已完成 Python 3 移植 |
| 信息收集/泄露 | `recon.svn_leak.scan` | SvnExploit | `/.svn/wc.db` 枚举受控文件 |
| 信息收集/泄露 | `recon.ds_store.scan` | ds_store_exp | `/.DS_Store` 目录结构还原 |
| 信息收集/泄露 | `recon.swagger_api.enum` | swagger-hack | Swagger 文档接口批量枚举；可选 `path` 指向 api-docs 地址（上游只探测传入 URL 本身），命中输出 `[SWAGGER]` 行 |
| 漏洞验证 | `detect.weblogic_t3.scan` | WeblogicScan | T3 端口（默认 7001）反序列化 CVE 检测，命中报 High |
| 漏洞验证 | `detect.fastjson_jndi.scan` | JsonExp | JSON payload 探测 Fastjson/Jackson；**必须提供 ldap/rmi 回连地址**（操作者起监听；实测无回调工具拒绝运行），有界 `host:port[/path]` 校验；stdout 无判定词即不产生 finding |
| 漏洞验证 | `exploit.heapdump.analyze` | JDumpSpider | 解析 heapdump 提取数据库连接、Shiro key、云 AK 等（实测按节内容判定，排除 "not found!" 节）；只接受 Hackermes 工件库（`agent-tools/`，由应用启动时经 `HACKERMES_AGENT_ARTIFACT_ROOT` 下发）内的文件名，严格拒绝路径逃逸；需 PATH/env 中的 Java 8+ |
| 漏洞利用 | `exploit.vcenter.verify` | VcenterKiller | vCenter CVE 验证；模式白名单（21972/21985/22005/22954/22972/log4center），动作仅 scan/upload（内置 `shell-verify.jsp` 验证载荷）/getcookie，命令 ≤128 字符且禁控制字符。实测工具对任意 2xx 端点都报 `[+] Upload success`，输出仅作为 **Medium 候选**（需人工复现），不作 High 依据 |

- **阶段判定**：系统提示词新增阶段指引（信息收集 → 检测 → 验证 → 利用），
  `assessment_tools` 返回本地可用性；Agent 按 dirsearch/httpx 的线索选择泄露枚举，
  按指纹结果选择中间件/反序列化检测，确认后再进入利用。
- **观察解析**：`ReconObservationParser` 为新适配器产出 finding 候选——泄露枚举
  Medium/Low；Weblogic CVE、heapdump 敏感分节、vCenter 正向输出 High；Fastjson
  回连确认 High、仅 payload 响应 Medium。证据全文仍照常落 evidence。
- **依赖打包**：Python 工具使用内置运行时；缺失的纯 Python 依赖（prettytable、
  loguru、ds_store 及其传递依赖）按工具目录 vendor 并登记进 `manifest.json`。
  GitHack 副本（BugScan 血统）以最小补丁完成 py2→py3 移植并经真实 git dumb-HTTP
  仓库克隆验证。
- **明确延后**：`cf`（云 AK/SK 利用）——凭证会明文持久化进 plan 存储，需先给
  ToolHost 设计密钥旁路（票证不落盘）再接入；VcenterKit 附带脚本——signxml/lxml/
  impacket 等依赖不在内置运行时内。GUI 型"综合漏洞利用工具"（Struts2/Tomcat/
  Nacos/Jenkins 等）无头不可驱动，待补 CLI 等价物后再评估。

## OA POC 探测（detect.oa_poc.*）

- **`detect.oa_poc.list`**：无网络操作，枚举内置 POC 库（泛微 e-cology/致远/通达/
  用友/蓝凌/ezOFFICE 等共 97 条 YAML POC，源自 OA-EXPTOOL 0.83，AGPL-3.0）。
- **`detect.oa_poc.probe`**：对授权 Web 端点探测指定 `module`（POC 子目录，可先
  list 枚举），可选 `poc` 限定单条。执行器 `oa_poc_runner.py`（Hackermes 自写的
  单发 runner）复刻 nuclei 风格 word/status 匹配器与请求序号提取器（如致远
  JSESSIONID 串接），仅输出 `[HIT]/[MISS]/[ERROR]/[SUMMARY]`；不写盘、不执行
  上游的利用/ getshell 载荷，命中映射为对应严重级的 finding 候选。
- 总控阶段划分与当前进度见 `docs/VULN-TOOL-INTEGRATION.md`。

## 本地靶场回归（阶段 3）

`tests/VulnTargetRangeTests` + `third_party/tools/_testrange/testrange_server.py`
提供不依赖外网的端到端回归：mock 靶标（通达/致远/swagger/git 哑协议仓库）经
`BuildInvocation` 生产参数面实跑，再断言解析器产出的 finding 级别。服务器自选
端口并回显 `RANGE_READY port=`，与并行测试无竞态。靶面覆盖进度见总控文档。

## 联网情报（web_search / vuln_cve_lookup / 工件阅读）

- **`web_search`**：有界搜索结果（1-10 条 title/url/snippet）。有 Key（DPAPI：
  `ai.webSearchApiKey`，AI 设置 → 联网情报 录入）走 Brave/Serper API；否则降级为
  内置浏览器驱动 Bing（CDP `Runtime.evaluate` 提取 `li.b_algo`，页签用完即关）。
- **`vuln_cve_lookup`**：单个 CVE 的有界摘要（描述/CVSS/引用 ≤8），NVD API 2.0
  优先、OSV 兜底；可选 NVD Key 提升限额。
- **`agent_artifact_list` / `agent_artifact_read`**：工件库列表与分页文本读取；
  二进制工件（扩展名 + NUL 双重判定）拒绝进入模型上下文。
- 三者全部 data-only：取回的任何内容都不执行，下载仍在 `agent_download_artifact`
  的授权约束内，执行仍只能经 ToolHost 授权管线。

## 利用前强制门控与 Skill 链（批次 C）

- **利用前门控**：`CreatePlan` 拒绝没有同目标检测证据的利用型适配器
  （`AuthorizedToolCatalog.IsExploitationStage`：`exploit.vcenter.verify`、
  `exploit.fastjson_payload.generate`）。解锁：同计划中该步骤之前的同目标检测步骤，
  或"活动 scope、job 已完成、目标覆盖"的检测证据（`IsDetectionStage` 集合）。跳阶段
  直接利用在建计划时报错，错误信息引导先跑检测阶段；系统提示词同步告知 agent。
- **内置 Skill 链**：`BuiltInSkillCatalog` 首次启动幂等播种 3 条工作流
  （信息泄露侦察链 / 国内 OA 探测链 / SpringBoot 堆转储分析链），默认**禁用**，
  操作者在 Skills 页启用；Skill 只收窄工具与附加工序说明，不放宽权限。
- 阶段总控见 `docs/VULN-TOOL-INTEGRATION.md`。

## JNDI 回连监听（检测闭环）

- **`jndi_listener_start` / `jndi_listener_hits` / `jndi_listener_stop`**：本地
  JNDI 回连监听服务。仅绑 127.0.0.1、自动端口、15 分钟自动过期、最多 4 个并发；
  任何入站 TCP 连接即记为 hit（连接本身就证明目标执行了注入的回调地址）。
- 典型闭环：`jndi_listener_start` → `detect.fastjson_jndi.scan(ldap=127.0.0.1:<port>)`
  → `jndi_listener_hits` 有记录即确认 fastjson 命中（High）。监听只记录、不回送
  对象，检测 only。
- Shiro（`detect.shiro.scan`）/ Struts2（`detect.struts2.scan`）/ Nacos
  （`detect.nacos.scan`）/ FastjsonExploit payload（`exploit.fastjson_payload.generate`）
  四个新适配器详见总控文档批次 D 记录。

## 云凭证暂存与只读验证（密钥零落盘）

- **`cloud_credential_stage`**（Dangerous）：把评估中发现的云 AK/SK 暂存进 DPAPI
  secret store（≤60 分钟自动过期，随时 `cloud_credential_clear`），只返回不透明
  token（`cc-<16hex>`）；密钥不进计划/票据/证据/日志。
- **`probe.cloud_aksk.verify`**：输入只含 token（计划持久化零密钥）；ToolHost 子进程
  按票证里的 `SecretReference` 自行从 DPAPI 解析，经 `CloudCredentialEnvironment`
  映射为 alibaba/aws/tencent/huawei 的 SDK 标准环境变量后注入 vendored cf.exe 进程。
  仅支持只读 `ls`/`perm`（列资源/列权限）；接管控制台等利用操作不做。
- 解析器保守：cf 正向输出 → Medium 候选（凭证有效），证据全文保留；输出定标待
  真实云账号。

## 请求纪元、Token 计量与目标续跑

- **请求头纪元（`request/header`，dsh 同名机制）**：每个 step 对请求的**稳定骨架**——
  模型、权限模式、ACP 开关、启用工作流签名与工具清单——计算指纹，仅在首次（`initial`）
  或变化（`change`）时落日志。记忆摘要、会话尾部等每轮必然变化的负载被刻意排除：
  它们造成的缓存失效是设计内行为，计入纪元只会用噪音淹没真实漂移。
- **KV-cache 对齐摘要**：自动压缩的辅助摘要调用复用主请求的同一系统提示与工具清单
  作为前缀（`CompactionPrefix`），与近期流量共享最长公共前缀，不再整体击穿缓存。
- **Token 级计量（`ai.maxContextTokens`，0 关闭）**：启用后 ACP 的条目计价换用混合
  脚本估算器（CJK≈1 token/字，其他≈4 字符/token），nudge/GC/自动压缩/用量条全部
  统一为 token 口径；收缩守卫同样经由存档层估算器定价，杜绝"字符摘要 vs token 区间"
  的混单位误判；0 保持旧的字符预算行为。
- **每模型压缩策略（`ai.compactionModelPolicies`）**：按模型名片段匹配的第一条策略
  覆盖全局 `autoCompactRatio`（ratio=0 表示对该模型关闭自动压缩）。
- **目标续跑 `goal_set` / `goal_clear`**（dsh goal-round driver 血统）：模型记录当前
  目标后，turn 收尾时运行时自动注入 `<goal_round>` 合成消息继续推进，直到目标被
  清除或到达单目标轮次上限（8 轮）。注入消息以"上下文注入"标记出现在转录中。
  注册表是会话级瞬态，持久目标应写入 Skill 或记忆。
- **MCP `readOnlyHint`**：tools/list 的 annotations.readOnlyHint=true 的远端工具映射
  为只读风险——可进入并行池并在请求批准模式下免确认；其余仍一律按 Mutating 处理。
- **会话自动命名（`ai.autoSessionNaming`，默认开启）**：首个完成 turn 后，默认名
  （"新会话 …"）的会话以首条用户消息前 18 字符重命名；显式改名不受影响。

## 明确延后的方向

完整子代理编排与后台任务系统（dsh 的 subagent providers / job_* 工具）需要与
ToolHost 隔离执行模型和授权范围深度整合，属于独立立项而非运行时补丁；影子价格
计量内部协议同批延后。

## Pre-step 拦截、投影器与会话分叉

- **Pre-step 瀑布（`IAgentPreStepHook`，dsh agent/pre-step 血统）**：每个模型 step 前
  按注册顺序评估钩子——`Reject` 直接以 Blocked 收束本 turn（不花模型调用、日志留痕）、
  `RewriteEntering` 链式改写刚认领的转向/注入消息（脱敏缝：如 password=… 打码后再落
  历史），`AppendEphemeral` 给本次请求附加临时消息（页面快照注入缝：不进历史、
  不污染 ACP 窗口）。抛异常的钩子被跳过而不是拖垮回合。
- **共享投影器 `AgentHistoryProjector`**：事件流 → 模型可见消息的唯一定义点，
  回放、导出与未来的 surface-replace 共用同一套折叠规则；不完整的工具协议尾部
  （崩溃孤儿）自动丢弃，不会产生悬空 tool_calls。
- **会话转录导出**：AI 面板"导出"按钮把 durable 日志渲染为 Markdown（操作者/
  助手/工具协议/审批审计/压缩标记分节），经系统保存对话框写盘。
- **会话分叉**：右键任一持久化会话 →"分叉此会话"，完整事件流复制到新 id 并立即
  打开——历史、压缩块与审批审计原样续跑，源会话不受影响。依赖
  `ai.sessionEvents`。
- **LLM 会话标题**：首个完成 turn 先以首条输入截断即时命名，后台一次 8 秒超时的
  小型模型调用精炼为 ≤12 字标题；失败静默保留截断名。
- **工具输出模式校验（`OutputSchema`，按工具可选声明）**：声明了输出模式的工具若
  返回非 JSON 或不符 schema 的结果，立即以 `INVALID_TOOL_OUTPUT` 失败反馈给模型
  自纠，避免在畸形证据上继续推理；未声明的纯文本工具不受影响。
- **UI 层级**：思考流使用独立暗色模板（左侧主题色竖条），与正文答案视觉分层；
  转录区随新行自动滚动到底；token/上下文用量合并为一行状态栏。

## 权限模式

| 模式 | 默认行为 |
|---|---|
| 请求批准（默认） | 只读工具直接运行；修改状态或使用网络的工具逐次请求人工批准。 |
| 帮我批准 | 正常工具自动运行；标记为高风险的工具才请求批准。 |
| 完全访问权限 | 已注册工具自动运行。不可恢复的系统破坏模式仍被硬性拒绝。 |

模式可在 AI 助手设置窗口选择，也可由人工 CLI 使用 `agent mode request|help|full` 切换。Agent 本身不能切换权限模式。

## Skill 工作流

Skill 是持久化的工作流说明，可包含名称、说明、启用状态和允许使用的工具列表。启用 Skill 后，工具列表只会**收窄**模型看到的工具，不能提升权限。

- 人工：AI 助手设置 → `管理 Skill 工作流`。
- Agent：`agent_skill_list`、`agent_skill_upsert`、`agent_skill_remove`；修改操作仍受权限模式约束。
- 本地存储：与应用设置同目录的 `agent-skills.json`，最多 64 个 Skill；每个说明和工具列表有长度上限。

## 压缩上下文与持久记忆

上下文管理采用 **ACP（Active Context Pruning，参考 opencode-acp / pai-acp）**：
压缩的时机与内容由模型自己决定，而不是后台静默截断。

- 会话中每条消息带稳定引用（如 `[m00012·2.3K·tool]`），模型用 `context_compress`
  把不再需要的区间替换为自包含摘要块（保留文件路径、决策、错误信息与用户目标），
  原文可 `context_decompress` 恢复、`context_search` 免解压检索、`context_status` 查看用量与建议区间。
- 保护规则：最近 4 条消息、最后一条用户消息和 `context_compress` 自身的结果不可被压缩或 GC；
  压缩区间不会把一次工具调用和它的结果拆开。
- 摘要块分层（T1 捕获 → T2 提炼 → T3 浓缩），高层层合并低层后仍可整体恢复；
  超出预算时 GC 兜底截断最旧内容并生成可检索的墓碑块，不静默丢失。
- **自动压缩**（`AcpAutoCompactor`，借鉴 deepseek-harness compaction-basic）：活动上下文
  达到预算的 `ai.autoCompactRatio`（默认 0.8，0 关闭）时，在每个请求组装前把最旧的安全
  可压缩区间交给辅助模型调用生成自包含摘要，并经存档层常规 `context_compress` 路径落地
  （保护区、工具配对完整性规则照常生效）。移植的不变量：摘要必须严格小于被替换区间
  （收缩守卫）；尝试按时间限速避免失败循环；所有失败都是尽力而为，nudge/GC 阶梯兜底。
- 工具结果在调度器出口做首尾保留剪枝（借鉴 dsh compaction-tool-result-pruner）：超限
  结果保留开头与结尾、中间以显式省略标记替代——证据常同时分布在开头的概要与结尾的
  总计/错误里。
- ACP 启用时是唯一的上下文管理器（`ai.acpEnabled`，默认开启）；关闭后回退到旧的
  "完成回合压缩为持久摘要"路径。跨重启只恢复最近的人类/助手消息，压缩块随应用会话存活。
- 不持久化工具调用参数或工具输出，避免把原始请求、认证头或密钥带入记忆。
- `agent-memory.json` 只存摘要、人工/Agent 备注与最近的人类/助手消息。
- 可在设置中关闭记忆、编辑备注或清除记忆；Agent 也可通过受策略约束的 `agent_memory_*` 工具管理备注。

## CLI

`agent` 是人工 CLI 的入口：

```text
agent status
agent mode request|help|full
agent skills
agent memory [show|clear]
agent download <https-url> [file] [sha256]
```

`agent download` 与 Agent 的 `agent_download_artifact` 只允许 HTTPS、禁止 URL 内嵌凭据、受大小限制，并写入 Hackermes 专用工具缓存。下载完成会计算 SHA-256；它**不会执行**下载文件。未来只有经过授权范围、审批和 ToolHost 隔离的 Adapter 才能使用外部工具。
