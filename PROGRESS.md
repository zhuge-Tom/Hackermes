<!-- markdownlint-disable MD013 -->

# Hackermes 开发进度（2026-08-17/18 会话）

> 路线总览（已完成项与后续计划）见 [ROADMAP.md](ROADMAP.md)；本文是会话级实施记录。

本次会话目标：按 P0（补 Stage 9 发布门禁）→ P1（可行功能缺口）→ P2/P3（后续增强）的方案推进。
按此前决定：门禁第 4 步（视觉矩阵）与第 5 步（Windows 打包校验）暂缓执行。

> **2026-08-23 第十阶段（本轮）—— ACP 主动上下文剪枝（参考 opencode-acp / pai-acp）**：
> `AcpContextStore` 从未接线的骨架升级为完整机制并接入聊天链路：
> 每条消息带稳定引用（`[m00012·2.3K·tool]`），模型通过 `context_compress/decompress/search/status`
> 四个只读工具自行决定何时、压缩什么（单一上下文管理器原则：ACP 启用时跳过旧版回合压缩）。
> 机制移植：工具调用对完整性（区间自动吸附为完整 call+results 段，杜绝孤儿工具结果）、
> 三层保护（最近 4 条 + 预算 15% 软字符区仅非工具消息延伸 + 最后一条用户消息）、
> `context_compress` 结果硬保护（压缩摘要不可被后续压缩或 GC 吞掉）、T3 终端块重写拒绝、
> 摘要质量门 L1（过短摘要非阻塞警告）、批量区间 JSON 字符串容错、
> nudge 升级（构成分解 + 具体可压缩区间建议 + 无候选时静默 + 每轮注入一行稳定 philosophy）、
> GC 兜底改为可检索墓碑块（不再静默丢失）。`AiSettings.AcpEnabled`（默认开），
> 会话切换/新建重建存储，聊天底栏显示上下文占用。+12 测试，**409/409 回归通过**。
> **2026-08-22 第九阶段（本轮）— ✅ P2-1 多用户身份提供方（P2 全部完成）**：
> `OperatorIdentityDirectory` 本地多档案（版本化 JSON，名称遵循审计身份规则 ≤64 字符/无控制字符）
> + `identity list|adopt|use` CLI；审计链单一缝解析活动档案名，空目录回退
> `traffic.operatorName` → `Environment.UserName`（升级零惊扰）。管理面与 signing-keys 同理
> 刻意 CLI-only。+9 测试，**397/397 回归通过**。

> **2026-08-22 第八阶段 — ✅ Agent 工具运用能力强化 II**：
> 系统提示注入"Tool use protocol"（先读后改/确认成本、offset-limit-total 分页协议、
> 失败自纠禁止原样重试、id 精确引用）；调度器对重复的相同参数只读调用追加纠偏提示
> （截断之后追加，保证不被切掉；Mutating/Dangerous 不标注）；分页类工具 schema 参数描述。
> +5 测试。

> **2026-08-22 第七阶段 — ✅ P2-2 审计密钥治理（轮换/撤销/信任分发）**：
> `signing-keys adopt|rotate|revoke` CLI 治理流 + 本地信任文件 allowlist 验证 +
> ECDSA 密钥轮换（旧私钥销毁、旧文档仍可离线验签）。刻意不进 Agent 工具面。+8 测试。

> **2026-08-22 第六阶段 — ✅ P2-3 新增只读受控 Adapter**：
> `recon.http.get`（系统 curl 单次 GET：状态+响应头+有界正文，路径限绝对/无空白/≤256 字符）
> 与 `recon.dns.resolve`（系统 nslookup 解析授权内精确主机名）。零新增第三方依赖，
> 固定 argv + 精确目标范围 + 有界超时/输出约束不变。+2 测试，**375/375 回归通过**。

> **2026-08-22 第五阶段 — ✅ P2-4 Agent 大归档分批交换**：
> `packet_archive_export` 增加 offset/limit 分页 + total 信封，超限不再整体失败；
> 超大单批报错附带"调小 limit 重试"指引。+9 测试，**373/373 回归通过**。
> 整套发布门禁对 0.9.0 源码尚未重跑，出包前补跑。

> **2026-08-22 第四阶段 — ✅ P1 全部收尾 + 0.9.0 升版**：
> ① Comparer 对比快照路径接入 SHA-256 缓存（`BodySha256` 迁至 Base 层共享）；
> ② 工作区切换后历史统计自动刷新（新增 `HistoryPolicyChanged` 事件链，构造时预载统计）；
> ③ 最小窗口下第 4 个内容标签完整可见（`RegionLayout` 纯策略 + 窗口缩放重夹取，中央列保底 600px）；
> ④ 版本号 `0.8.0 → 0.9.0`（props 三处 + 打包/门禁脚本默认参数 + README）；
> ⑤ 回归：Release 构建 0 错误，**364/364 测试通过**。整套发布门禁对 0.9.0 源码尚未重跑，出包前补跑。

> **2026-08-22 第三阶段 — ✅ P1-2 复杂规则完整表单完成 + 门禁重跑**：
> - **P1-2 规则表单 UI：✅ 完成**——工作台高级编辑区支持 request rewrite / response fulfill
>   完整表单，加载选中规则回填 + 保存更新，+14 测试。
> - **发布门禁重跑：✅ 5/5 全量通过**（对含第二阶段四项 + P1-2 的源码；350/350 测试）。
>   **证据目录：`G:\HackermesBuild\release-acceptance-20260822\release-evidence\`**
>   （清单 `release-acceptance.json`：loopback/visual/trafficVisual/responsiveVisual/packaging 全 passed，
>   tests 350/350；Windows 包 `Hackermes-0.8.0-windows-x64.zip`（167MB）+ SHA256SUMS 校验通过）。

> **2026-08-21 增量（上轮，全部已验证）**：在 `G:\Hackmes` 重开会话后 Shell 恢复。
> ① 上轮未验证的改动（审计操作者身份 + 验收脚本修复）完成构建与测试验证；
> ② 修复上轮新增测试自身的目录缺失缺陷；
> ③ 完成 **Agent 能力强化**阶段（7 项优化，+16 测试）；
> ④ **Stage 9 发布门禁 5/5 全量通过**（含 minimum 档视觉矩阵与 Windows 打包）；
> ⑤ **Linux x64 交叉发布成功**。证据见"二、P0 余项"。

> **2026-08-21 第二阶段（后续选项）— ✅ 四项全部完成并回归**：
> - **P1-4 超大 body SHA-256 缓存：✅ 完成**——修复上轮遗漏的 `using System;` 编译错误后随本阶段回归通过，+2 测试。
> - **P1-3 multipart 参数编辑：✅ 完成**——`BoundedMultipartBody` 修复跳 part 缺陷后接线三端，+11 测试。
> - **P2-1 签名报告导出：✅ 完成**——Assessment 报告 ECDSA 签名服务 + CLI/Agent/ToolHost 三入口，+5 测试（另同步加固审计导出验证 +2）。
> - **P1-5 配额按工作区隔离：✅ 完成**——策略文件按工作区热替换 + 立即应用 + 来源标注，+3 测试。
> - 最终回归（2026-08-22 复核）：Release 构建 0 错误，**336/336 测试通过**。详见"一、已完成 0.1"。

## 一、已完成

### 0.-6 2026-08-22 第九阶段：P2-1 多用户身份提供方（✅ 完成，+9 测试，397/397 回归通过）

调研结论：审计链已有单一盖章缝（`PacketAuditTrail.Record` 的 provider），操作者身份来源是
`traffic.operatorName ?? Environment.UserName` 二段回退；评估域的 operator 是每操作的显式参数
（审批/复核归属语义），不需要活动档案概念。

- **`OperatorIdentityDirectory`**（App 层，独立版本化 JSON `operator-identities.v1.json`，
  tmp 原子写 + .bak 备份）：本地多档案 `{id, name, createdAt}` + activeId。名称在录入时即按
  审计链身份规则校验（trim、≤64 字符、禁控制字符）——目录里存不进审计链会拒绝的值。
  adopt 按大小写无关名幂等（存在即激活），use 支持按 id 或名激活，损坏文件回落备份，
  activeId 指向缺失时优雅降级为 null（走回退链）。
- **审计链接线（DI 单一缝）**：provider 链改为 `directory.ResolveActiveName()
  ?? settings.Traffic.OperatorName ?? Environment.UserName`——无档案时行为与旧版逐字节一致；
  采用档案后新审计条目自动盖新身份，工作台 Audit 表 / CLI `packet audit` / Agent
  `packet_audit` / 签名导出负载经既有路径自动继承，无需各自改动。
- **CLI**：`identity list | adopt <name> | use <name-or-id>`；list 显示 resolved 结果与
  回退说明，未切换/未知身份返回失败。管理面 CLI-only（同 signing-keys 的治理哲学）。
- 边界：评估域审批/复核的显式 operator 参数保持不变（那是归属契约而非"当前身份"）。
- 测试（+9，`OperatorIdentityGovernanceTests`）：空目录回退 null→adopt 激活、按名幂等 +
  跨重启持久、use 按 id/名切换与未知拒绝、名称规则四种拒绝、超长拒绝、真实审计链
  先盖 settings 回退后盖活动档案（时间序断言）、CLI 生命周期。

### 0.-5 2026-08-22 第八阶段：Agent 工具运用能力强化 II（✅ 完成，+5 测试，388/388 回归通过）

调研结论：主循环已支持一轮多个并行 tool calls；真正的缺口是"怎么用好工具"的引导——
系统提示只有安全/评估工作流指引，没有通用工具运用协议；模型重复发起完全相同的只读调用
时没有任何反馈信号；新增的分页参数（offset/limit）在 schema 里没有语义描述。

- **系统提示 Tool use protocol**（`AgentContextCompactor.BuildSystemMessage`）：四条可执行约定 ——
  ① 先用只读工具构建证据，Mutating/Dangerous 每次都要操作者确认，因此仔细备参并把相关修改
  合并为更少的调用；② 大数据一律分页（packet_query / packet_archive_export 的 offset/limit、
  归档信封 total 遍历到底、packet_body_chunk 有界区间、packet_audit limit）；③ 失败时按错误
  信息里的指引改参重试，禁止原样重复调用期待不同结果；④ 精确引用先前结果中的 packet/evidence id，
  工具输出是唯一证据来源。
- **调度器重复只读调用检测**（`AiToolDispatcher`）：复用会话授权键的规范化参数指纹，按
  （session, tool, pageId, args）跟踪最近只读调用（上限 256 条，满则整体清空）；同一调用再次
  出现时在结果尾部追加"[提示]…调整参数或改用其他工具"。**在截断之后追加**——超长结果先截断再
  标注，提示永不被切掉。Mutating/Dangerous 不标注（重复执行可能合法）。无 SessionId 的调用不跟踪。
- **schema 参数描述**：packet_archive_export 的 filter/offset/limit、packet_query 的
  offset/limit、packet_body_chunk 的 offset/count、packet_audit 的 limit 均补充一句话语义。
- 测试（+5，`AgentToolUseProtocolTests`）：系统提示含协议关键句；相同只读调用第二次获提示而
  首次没有；改参/Mutating 重复不标注；hint 在截断标记之后存活；分页 schema 描述存在。

### 0.-4 2026-08-22 第七阶段：P2-2 审计密钥治理（✅ 完成，+8 测试，383/383 回归通过）

调研结论：签名导出（流量审计 + 评估报告共用一把 ECDSA P-256）此前只有"内嵌公钥自校验 +
调用方指纹钉扎"，无轮换流程、无撤销语义、无可信指纹分发载体。

- **Automation 层**：`IPacketAuditTrustPolicy`（可选注入 `PacketAuditExportService`）。附加策略后，
  Verify 在密码学校验之外还要求文档 keyId 在本地允许列表中，否则返回既有错误码 `untrusted_key`；
  不注入策略时行为与旧版完全一致（第三方离线钉扎路径不受影响）。
- **Assessment 层**：镜像接口 `IAssessmentReportTrustPolicy` + 实例 Verify 覆盖层；
  静态 `VerifyDocument` 保持纯离线语义（ToolHost `--verify-report` 不变）。
- **App 层**：
  - `AuditKeyTrustFile`（版本化 JSON，临时文件原子写 + .bak 备份）：记录每代密钥的
    keyId/SPKI/状态（trusted/retired/revoked）/时间戳/备注。**文件不存在 = legacy 模式放行一切**
    （升级零惊扰）；一旦创建即 allowlist 模式。
  - `adopt` 显式登记当前代 → 进入治理模式；`rotate` 单次原子写入完成"旧代退役 + 新代 trusted"
    （已撤销的旧代保持 revoked），随后才覆盖 DPAPI 私钥密文（旧私钥销毁，历史文档凭信任文件里
    保留的公钥继续可验）；密钥写入失败自动回滚信任文件快照（Restore 补偿）。
  - `revoke <keyId>` 支持撤销未知 keyId（登记为 revocation-only 条目，用于分发黑名单）。
  - `PacketAuditSigningKey.Rotate()`：KeyId/PublicKey 变为锁内可变，签名/读取全程持锁。
  - 双域策略适配器 `PacketAuditTrustPolicy : IPacketAuditTrustPolicy, IAssessmentReportTrustPolicy`
    （沿用"Assessment 内重声明接口 + App 单例双实现"的分层先例）。
- **CLI**：新命令组 `signing-keys list | adopt [note] | rotate [note] | revoke <keyId> [note]`。
  **刻意不暴露给 Agent 工具面**——密钥采用/轮换/撤销是操作者决策，与"Agent 不获得任意 Shell"
  同一治理哲学；GAP-MATRIX 将标注此不对称。
- 测试（+8，`AuditSigningKeyGovernanceTests`，真实审计链 + 真实评估控制面 + CLI 注册表）：
  legacy 放行、adopt 后拒绝未知 key 且重启（重读文件）仍生效、轮换换签发密钥且退役文档仍可验、
  撤销拒历史文档但新文档正常、外来密钥签名文档被策略拒绝而纯密码学验证通过、未 adopt 时轮换
  拒绝、评估报告策略覆盖层（静态路径不受影响）、CLI 全生命周期（list→adopt→rotate→revoke→bogus）。

### 0.-3 2026-08-22 第六阶段：P2-3 新增只读受控 Adapter（✅ 完成，+2 测试，375/375 回归通过）

调研结论：`AuthorizedToolCatalog` 是唯一缝——工作台 Adapter 下拉从 `Describe()` 自动生成，
计划校验/ToolHost/证据链全部按 `AdapterId` 走通用路径，新增 Adapter 无需改任何其他层。
既有 6 个 recon Adapter + simulation.echo；本轮按"只读、系统自带二进制、零新增依赖"补两个：

- **`recon.http.get`**：curl `-sS -D - -o -` 单次 GET，返回状态行+响应头+有界正文
  （与 `recon.http.headers` 的 HEAD 头探测、httpx 单行输出互补，适合取证类正文快照）。
  可选 `path` 参数：必须以 `/` 开头、无空白与控制字符、≤256 字符（`NormalizeRequestPath`），
  缺省 `/`；scheme/port 沿用共享 `ReadWebEndpoint` 校验。
- **`recon.dns.resolve`**：nslookup 解析授权范围内精确主机名（仅 target 一个参数）。
  路径解析 `HACKERMES_NSLOOKUP_PATH` → PATH → System32；非 Windows 上自然标记不可用。
- 两者的 NormalizeStep 输出规范化 JSON（GET 含 path），精确目标范围校验双路
  （BuildInvocation 与 NormalizeJson）都生效；固定 argv、超时/输出上限钳制沿用现有逻辑。
- 测试（+2，扩展 `AuthorizedToolHostTests`）：argv 形状/缺省 path/超时输出钳制/
  规范化 JSON 形状（含大小写归一）/坏 path 三种拒绝/scope 越界拒绝/Describe 可用性；
  外加真实 ToolHost loopback GET 探测用例（404 状态行 + 响应头出现在输出中，
  与既有 live 用例同款守卫）。ARCHITECTURE.md ToolHost Adapter 清单已刷新。

### 0.-2 2026-08-22 第五阶段：P2-4 Agent 大归档分批交换（✅ 完成，+9 测试，373/373 回归通过）

调研结论：`PacketArchiveContent` 已有 500 条 / 2 MiB 有界交换，但导出**超限即整体失败**
（"narrow the filter"），Agent 没有任何分批手段拿到更大的过滤结果集；ROADMAP P2-4 的
"无上限预过滤"实为"有上限但无分页"。本轮补齐分批交换：

- **Automation 层**：`IPacketArchiveService` 新增 `ExportArchivePageAsync(PacketArchiveExchangeQuery, ct)`
  （`Filter/Offset/Limit` → `PacketArchivePage(Entries, Total)`）；分页切片为纯函数
  `PacketArchiveContent.Page`（offset 越界返回空页且 total 不变、limit 1–500 校验），
  服务实现只做条目解析。既有 `ExportArchiveAsync`（CLI/文件路径）不动。
- **Agent 工具**：`packet_archive_export` schema 增加 `offset`（≥0）/`limit`（1–500，
  缺省 500），响应信封 `{format, count, total, offset, content}`——模型按 total 规划
  后续批次直到取完；描述同步更新。风险维持 Dangerous（内容仍可能含 body secrets，
  需显式确认）。`packet_archive_import` 维持既有有界语义。
- **失败自纠错**：单批序列化超 2 MiB 时报错改为"retry with a smaller limit so each
  batch fits"，延续 Agent 失败消息带行动指引的约定。
- **CLI 不受影响**：文件导出走 `PacketArchiveCodec.Serialize` 全量写盘，本就无 2 MiB 限制。
- 测试（+9，`PacketArchivePagingTests`）：中段/末尾短页/越界空页切片、offset/limit 越界
  拒绝、Agent 分页信封（total/count/offset + content 反序列化核对条目）、缺省首批、
  坏参数安全失败、超大单批指引信息。

### 0.-1 2026-08-22 第四阶段：P1 收尾三件套 + 0.9.0 升版（✅ 完成，+11 测试，364/364 回归通过）

**P1-3b Comparer 对比快照接入 SHA-256 缓存**

- 层级调整：`BodySha256` 从 `Hackermes.Automation/Packet` 迁至 `Hackermes.Base.Cryptography` ——
  Traffic 不引用 Automation（反向），Comparer 想复用缓存必须把工具下沉到两层共享的 Base。
  调用点（App `TrafficIntegrationService` 两处）与测试仅改 using；Automation 内无其他引用。
- `TrafficComparisonService.Summarize` 改走 `BodySha256.Of(body)`：对比快照 body 与 store
  共享同一不可变数组实例，Create/UpdateSources/Recalculate 反复比较同一包时不再重算哈希；
  移除该文件已无用的 `System.Security.Cryptography` using。

**P1-2b 工作区切换自动刷新历史统计**

- 缺口：策略文件按工作区热替换后，工作台 HistoryStatus 仍显示旧值，需手点 Refresh 才能看到
  新 `policy <source>`；且面板初始值是"History statistics not loaded."占位。
- 事件链：`ITrafficWorkbenchService` 新增 `HistoryPolicyChanged`（接口注明 UI 线程契约 ——
  工作区事件全部由 UI 线程发布：文件夹选择器、`StartupPerformance.RunAfterDelay` 均 dispatch 回 UI 线程）→
  App `TrafficIntegrationService.NotifyHistoryPolicyChanged()` 显式桥接 →
  `TrafficIntegrationModule.RegisterWorkspacePolicyIsolation` 在 SwitchStorage + Cleanup **之后**
  触发（观察者读到的一定是新策略与裁剪后的最终状态，不依赖 EventBus 订阅顺序）→
  VM 订阅即刷。VM 构造时同步预载一次统计，消除初始占位文案。
- 测试支撑重构：原 `InspectorImportExportViewModelTests` 私有嵌套 fake 抽取为共享
  `WorkbenchServiceFake`（internal，独立文件），增加 `RaiseHistoryPolicyChanged()`、
  `HistoryOverviewRequests` 计数与可注入 `NextHistoryOverview`。
- 测试（+3，`TrafficWorkbenchHistoryPolicyRefreshTests`）：构造即预载一次、事件触发重读并
  更新状态行与表单字段、Dispose 后退订不再查询。

**P1-2c 最小窗口第 4 个内容标签完整可见**

- 根因回顾：880×560 最小窗口下左右面板（240+300+8 分隔线）吃掉约一半宽度，内容列仅 ~330px，
  四个内容标签 ≈572px 放不下，"授权评估"标签溢出裁剪只能靠滚动到达（门禁脚本靠左缘点击兜底绕过）。
- 新增 `RegionLayout`（App 层 internal 纯静态策略）：`ClampSidePanelWidth(regionWidth, desired)` =
  min(期望值, 窗口宽 × 30%, (窗宽 − 8 分隔线 − 600 中央保底) / 2)，全部数字集中可测。
  880 下两侧各压到 136（中央恰得 600）；1250/1492 下默认配置完全不受影响
  （240/300 均小于预算），只有用户拖得比预算更宽时才被夹取。
- `MainContentView` 接线：`SetColumn` 可见分支改走共享预算；新增 `_regionGrid.SizeChanged`
  → `ReclampVisibleSideColumns()`——只读当前列宽做夹取、**不写记忆值**（记忆值仍保存用户
  拖拽期望，恢复大窗口时原样回来），折叠中的列跳过，变化 <0.5px 不动以避免无效布局抖动。
- 已知边界：拖宽超过当前预算的分隔条在下次窗口缩放时会被压回（预期语义）；窄于
  "分隔线 + 中央保底"之和时两侧面板收敛为 0 宽（可再手动展开或调大窗口）。
- 测试（+8，`RegionLayoutPolicyTests`）：wide/medium 保持配置值、880 双侧压缩到 136 且
  内容列 ≥600 ≥ 四标签宽、超配额按 content/ratio 预算封顶、小期望值永不放大、
  零/负宽度归零、低于保底的极窄窗口两侧归零。

**版本与脚本升版（P0 前置）**

- `Directory.Build.props`：Version/AssemblyVersion/FileVersion 三处 `0.8.0 → 0.9.0`。
- `package-release.ps1` / `invoke-release-acceptance.ps1` 默认 `-Version` 参数同步 0.9.0。
- README 当前版本行改为"源码 0.9.0（最新已发布仍为 v0.8.0）"，开发快照行刷新为
  364/364 + 门禁口径说明；能力清单补 multipart 参数编辑。
- GAP-MATRIX"大 body 元数据"行的 SHA-256 缓存备注改为对比快照已接入。

**回归验证（2026-08-22）**：Release 全量构建 0 错误（24 个警告均为既有 fake CS0067 /
xUnit 分析器风格项，非本轮引入）；`dotnet test -c Release` **364/364 通过**
（本轮新测试 +11：8 个布局策略 + 3 个历史刷新；与上轮门禁 350 的差额 3 个为
门禁之后、本轮之前已存在于工作树的用例）。整套发布门禁（视觉矩阵/打包）对
0.9.0 源码尚未重跑，属 P0 余项。

### 0.0 2026-08-22 第三阶段：P1-2 复杂规则完整表单（✅ 完成，+14 测试，350/350 回归通过）

调研结论：`TrafficRule` 模型早已支持复杂编辑（`TrafficRequestEdit`/`TrafficResponseEdit`），
但唯一入口是 JSON 导入；工作台表单、CLI `rule add`、Agent `traffic_rule_change` 都只透出
pause/drop。约束：`TrafficRuleSet.Replace` 禁止单条规则同时携带请求编辑与响应编辑（单选）；
CDP `continueRequest` 的 headers 为 set/override 语义、`fulfillRequest` 的 responseHeaders
为完整集合——因此表单不做"删除头"语义，一律 `Name: value` 行。

**Draft 模型与服务层**

- `TrafficRuleDraft` 增加可选结构化负载：`RequestUrl/RequestMethod/RequestHeaders/RequestBody`
  与 `ResponseStatus/ResponseStatusText/ResponseHeaders/ResponseBody`
  （header 为 `TrafficRuleHeaderEdit(Name, Value)` 列表）；接口新增
  `UpdateRuleAsync(draft)` 与 `GetRuleAsync(id)`。
- 新增 App 层 `TrafficRuleDraftMapper`（internal，App 对测试工程开放 internals）：
  `BuildRule` 校验并映射四种行为（pause 显式 `Pause:true` 保持既有语义；edit 至少要求
  一处改动；fulfill 状态缺省 200、限 100–999；body ≤256KiB 与参数编辑上限对齐；
  stage 支持 request/response/any）；`ToDraft` 反向重建表单草稿，二进制 body 用严格
  UTF-8 解码探测、不可解码则不回显（规则内字节不动，仍走 JSON 导入/body-edit 编辑）。
- 头行解析/格式化在 ViewModel 静态方法（`ParseHeaderLines`/`FormatHeaderLines`，
  internal 可测）：逐行 `Name: value`，空行跳过，坏行抛带行号的 FormatException。

**工作台 UI（`TrafficRulesView.axaml` + VM 命令）**

- 新增"Advanced edits"折叠区（默认收起）：左右两栏分别为 request edit 与 response fulfill
  的完整字段；行为占位符更新为 pause/drop/edit/fulfill。
- 底部命令行新增 **Load selected**（选中行 → 全字段回填，记录 `_loadedRuleId`）与
  **Save changes**（仅加载后可用，按加载的规则 id 调 `UpdateRuleAsync`，保持列表位置；
  未加载时给出明确提示而非异常）。Add 保持纯新建语义（重复 id 由管理器拒绝并在状态栏报错）。
- 分层不变：Inspector 只依赖自身 Draft 记录，复杂规则到持久化模型的映射全部在 App 层。

**测试（+14，全量 336 → 350）**：`TrafficRuleComplexFormTests` —— 映射器 edit/fulfill/
pause/drop 形状与校验（含超大 body 拒绝）、draft⇄rule 双向 round-trip、二进制 body 不回显
且不被破坏、经真实 `TrafficRuleManager` 持久化重载后仍还原、VM 表单头行解析进 draft、
坏行安全失败不打服务、load→save 更新回环、未加载不可保存。
另同步更新 `InspectorImportExportViewModelTests` 的 fake 以实现新接口成员；
STAGE6-GAP-MATRIX"持久拦截规则"行的缺口已改写为剩余边界（CLI/Agent 复杂创建、二进制 body）。

**发布门禁重跑（✅ 2026-08-22 5/5 全量通过）**

对含第二阶段四项 + P1-2 改动的源码执行 `scripts/invoke-release-acceptance.ps1
-HackermesBuildRoot G:/HackermesBuild/release-acceptance-20260822 -RunResponsiveVisualMatrix`：

| 步骤 | 结果 |
| --- | --- |
| [1/5] Release 全量构建 | ✅ 14 项目 0 错误（Browser 1 个既有 Avalonia AVLN3001 警告，非本次引入） |
| [2/5] 完整测试集 + TRX | ✅ **350/350** |
| [3/5] 真实桌面 WebView2/CDP loopback | ✅ 5/5（capture/replay/intercept/request-edit/response-fulfill；DPAPI 密钥指纹跨进程一致） |
| [4/5] 视觉验收 | ✅ Assessment/Traffic 浅深主题 + wide/medium/minimum 响应式矩阵全过（minimum 880×560 连续第二次 PASS，确认脚本修复稳定） |
| [5/5] Windows 打包校验 | ✅ `Hackermes-0.8.0-windows-x64.zip`（167MB）+ SHA256SUMS |

证据：`G:\HackermesBuild\release-acceptance-20260822\release-evidence\release-acceptance.json`
（acceptedAtUtc 2026-08-22T08:55:57Z）。注意 manifest 记录的 buildRoot 为运行时原始路径。

### 0.1 2026-08-21 第二阶段：后续选项开发（✅ 四项全部完成，2026-08-22 实测 336/336 回归通过）

调研结论（探索代理全库梳理）：SHA-256 全部调用点每次全量重算，唯一缝在
`TrafficIntegrationService`（`ToEditVersion` 为最热路径）；multipart 全仓库无解析器；
签名导出有 `PacketAuditExports`（ECDSA P-256 + SPKI 内嵌 + 离线验签）可克隆先例；
工作区（`IWorkspaceService`）目前只有标题消费，无任何数据按工作区隔离。

**P1-4 超大 body SHA-256 缓存（✅ 完成）**

- `Hackermes.Automation/Packet/BodySha256.cs`：`ConditionalWeakTable<byte[], string>`
  按数组引用 memoize。正确性依据：Traffic body 为不可变 `byte[]`（编辑即新实例），
  引用相等 ⇒ 哈希必然新鲜；条目随数组被 GC 回收，无需容量管理、不可能返回陈旧值。
- 接线两处（`TrafficIntegrationService`）：`ToEditVersion`（最热路径）与 `DescribeBodyAsync`
  （`packet_body_info` + 工作台选中刷新）。已知未接：`TrafficComparisonService.Summarize`
  （对比快照路径，频度低，留待后续）。
- 本轮修复：上轮实现缺 `using System;` 从未编译通过，本轮构建时发现并修复。
- 测试：`BodySha256Tests`(2)（同一数组引用命中缓存、新数组重算且空/大 body 与直接哈希一致）。

**P1-3 multipart 参数编辑（✅ 完成，+11 测试）**

- `BoundedMultipartBody`：byte[] 切片级解析与按字节拼接写回（未编辑的 part——含二进制——
  逐字节保留）；边界：part ≤64、name ≤256、value ≤256KiB、boundary ≤128 且禁 CR/LF；
  文本 part 显示 UTF-8 值，二进制 part 显示 `<binary N bytes>` 占位（`IsBinary` 显式标记，
  替换原先 `<...>` 字符串启发式）。
- **本轮修复解析器缺陷**：处理完一个 part 后 cursor 跳到"下一个分隔符之后"，导致第 2、4、6…个
  part 全部被跳过；改为 `cursor = next`（分隔符即下一个 part 的开界）。
- 异常对齐：`ExtractBoundary` 的 `InvalidOperationException` → `InvalidDataException`，
  使 CLI（`PacketCommandRegistrar` catch 链）与 Agent（工具 catch 过滤器）都能转安全失败。
- 接线三端：`HttpParameterLocation` 增 `Multipart` 枚举 + `Read`/`Set` 分支（raw 字符串路径，
  UTF-8 往返）；CLI `packet param-set <id> <side> multipart <name> <occurrence> <value>`；
  Agent `packet_parameter_set` schema 枚举增 `multipart`；工作台参数面板自动透出
  （`Location` 列显示 `multipart[occurrence]`，提示文案已更新）。
- 已知限制（已写入 STAGE6-GAP-MATRIX）：raw 包路径 body 为 UTF-8 字符串，经该路径的二进制
  part 上游已失真，仅显示占位；二进制 part 的字节级编辑仍走 `body-edit`。
- 测试：`BoundedMultipartBodyTests`(5)（二进制字节保真/occurrence/越界拒绝/boundary 校验）、
  `HttpPacketParametersTests` +2（包级读/写、坏 boundary 静默跳过）、
  `MultipartParameterSurfaceTests`(3)（包级参数面读出 multipart part、写回保真兄弟 part、非 multipart 拒绝）、
  `PacketParameterEntryPointTests` +1（CLI/Agent 共用 multipart 契约 + 未命中安全失败）。

**P2-1 签名报告导出（✅ 完成，+5 测试）**

- 新增 `Hackermes.Assessment/AssessmentReportExports.cs`（克隆 `PacketAuditExports` 模式）：
  规范化负载 = Web camelCase 紧凑 JSON（`AssessmentReport` 整卷，经 `ReadCase` 组装），
  ECDSA P-256/SHA-256 P1363 签名，内嵌 SPKI 公钥 + keyId=SHA256(SPKI)；Verify 为静态
  `VerifyDocument`，无需控制面与私钥即可第三方离线验签；错误码体系与审计导出一致
  （empty_content/content_too_large/unsupported_version/unsupported_algorithm/invalid_document/
  too_many_entries/key_id_mismatch/untrusted_key/invalid_public_key/invalid_signature），
  验签侧新增负载校验（jobId 形状、三列表 ≤500）。上限：文档 ≤2MiB。
- 同一把密钥：App 层 `PacketAuditSigningKey` 同时实现 `IAssessmentReportSigningKey`
  （Assessment 不引用 Automation，接口在 Assessment 内重声明、App 单例双实现），
  DI 在 `AssessmentIntegrationModule` 注册。
- 三入口：CLI `assessment report-export <path> <job>` / `report-verify <path> [keyId]`；
  Agent `assessment_report_export`（Dangerous，schema 不含 path，返回文档原文）/
  `assessment_report_verify`（ReadOnly）；ToolHost 新增 `--verify-report <path>`
  第三方离线验签模式（argv 解析 + 文件预检 + 退出码 0/1，信封模式向后兼容）。
  已冒烟验证：missing→file_not_found/exit1、坏 JSON→unsupported_version/exit1。
- 文档同步：ARCHITECTURE.md Agent 工具清单、README 当前限制、STAGE6-GAP-MATRIX P2 项。
- 测试：`AssessmentReportExportTests`(3)（roundtrip+信任钉扎/篡改+越界+坏文档/静态验签）、
  `AssessmentReportExportEntryPointTests`(2)（CLI 文件导出+验签+钉扎失败、Agent 风险等级+
  无 path+回环+篡改拒绝）；克隆报告导出时同步加固审计侧：`PacketAuditExportTests` +2
  （负载篡改与 untrusted_key、空/超大内容不解析即拒）。

**P1-5 配额按工作区隔离（✅ 完成，+3 测试）**

- `TrafficHistoryPolicyStore`：新增 `PolicySource` 与 `SwitchStorage(path, source)`——
  同一把 `_gate` 锁内换路径并从新文件重载（缺失/损坏回落默认），`Update` 写入目标随路径走，
  无"换路径瞬间写旧文件"竞态；数据面（历史条目单文件）不改造。
- App 层桥接（`TrafficIntegrationModule.RegisterWorkspacePolicyIsolation`）：
  `ProjectOpenedEvent` → `<workspace>/.hackermes/traffic-history-policy.json`（"workspace"）；
  `ProjectClosedEvent` → 回退全局文件（"global"）；每次切换后立即 `history.Cleanup()`
  （force 裁剪 + flush + publish），不依赖 Put 触发、不受 1 分钟节流影响。
  分层约束：Traffic 不引用 Platform，桥接必须放 App 层（先例：TrafficRuleAuditBridge）。
- 来源标注三端透出：`TrafficHistoryStatistics.PolicySource`（可选参数，向后兼容）→
  CLI/Agent `traffic-history stats` 行尾 `policySource=workspace|global`（Agent 与 CLI 同源）→
  工作台 HistoryStatus 追加 `policy <source>`。
- 已知边界：启动后 300ms 工作区恢复前运行在全局文件（预期窗口期）；切换到更严策略会立即
  删数据（force 语义，预期）；`.hackermes/` 子目录为新约定（VersionedJsonFile 自动建目录）。
- 测试：`TrafficHistoryWorkspaceIsolationTests`(3)（工作区策略加载+更新路由+回切、
  缺失文件回落默认、stats 来源标注+切换后 cleanup 立即按新配额裁剪）。

### 0.2 2026-08-21：Agent 能力强化（本阶段重点，已验证）

调研结论与实施范围基于对 `Hackermes.AiPanel` Agent 子系统的全面梳理
（主循环在 `AiChatViewModel.RunToolLoopAsync`；策略/调度在 `Tools/ToolPolicy.cs`；
上下文在 `Agent/AgentContextCompactor.cs`；客户端为 `OpenAI/OpenAiCompatibleClient.cs`）。7 项落地：

1. **工具结果统一截断**（`AiToolDispatcher.Limit`）：出口处按 `maxToolResultCharacters`
   （默认 12k，设置窗口可调 1k–100k）截断，附截断标记与"改用分页/chunk"指引，
   防止单个工具挤爆 24k 上下文预算。
2. **工具调用超时**（Dispatcher `CancelAfter`）：`toolCallTimeoutSeconds` 默认 120s
   （5–3600 可调），超时返回含"拆小步骤"建议的失败信息；操作者取消仍正常传播。
3. **压缩保留工具证据**（`AgentContextCompactor.CompactCompletedTurns`）：不再整体丢弃
   tool 消息——摘要保留 `assistant: 调用工具 X` 与 `工具 X 结果: <240 字符有界摘要>`，
   按时间序穿插；超过 48 条时最旧的折叠为省略说明。多轮调查所得 packet id 等关键证据不再丢失。
4. **流式请求退避重试**（`OpenAiCompatibleClient`）：429/5xx/连接错误在**未消费任何内容前**
   重试（≤2 次，尊重 Retry-After，上限 30s）；非重试错误立即抛出带响应体详情的异常。
5. **usage token 解析与会话累计**：解析 SSE `usage` 块（此前请求了 include_usage 却不读），
   `AiChatViewModel.SessionPromptTokens/SessionCompletionTokens` 会话累计并在聊天面板显示 `↑N ↓N tokens`。
6. **弱类型命令投影泄漏修复**（`CommandToolAdapter.Excluded`）：`annotation`、`traffic-history`、
   `compare-session` 不再投影为单字符串参数的 `page_*` 工具（与专用强类型工具重复且易错参）。
7. **失败消息带行动指引**：未知工具/策略拒绝/未批准/参数准备失败/执行异常均返回
   含下一步建议的结构化中文消息（如"降低风险后重试""不要绕过策略"），提升模型自纠错能力。

新增设置项 `ai.maxToolResultCharacters`、`ai.toolCallTimeoutSeconds` 已接入
`SettingsService` 归一化钳制、`AiSettingsWindow` UI 与 `AiPanelModule` DI 装配。

新增测试 16 个（299 → **315**）：`AiToolDispatcherHardeningTests`(8)、
`AgentContextCompactorEvidenceTests`(2)、`OpenAiCompatibleClientResilienceTests`(5)、
`AiChatTokenUsageTests`(1)。

### 0.3 2026-08-21：上轮遗留验证收口

- 审计操作者身份改动（下节 0 的 P1-1）：Release 构建 0 错误，全量测试通过。
- 上轮新增测试 `Load_AcceptsLegacyEntriesWithoutOperatorField` 自身缺陷修复：
  直接 `File.WriteAllText` 前未创建临时目录（目录原本由 `PacketAuditTrail.Record` 内部创建），
  补 `Directory.CreateDirectory` 后通过。

### 0. 2026-08-18 增量（已于 08-21 验证）

**P1：审计链操作者身份（源码完成，待构建验证）**

- `PacketAuditEntry` 追加可选 `Operator` 字段（旧 JSON 无此字段可正常加载；旧构建读新文件会忽略该字段）。
- `PacketAuditTrail.Record` 作为单一缝统一盖章：条目未显式携带时由 provider 提供；
  净化为 Trim + 最长 64 字符，空白归 null；`ValidateEntry` 拒绝超长与含控制字符的操作者。
- `TrafficSettings.OperatorName`（`settings.json` 的 `traffic.operatorName`）为身份来源，
  空则回退 `Environment.UserName`；装配点在 `TrafficIntegrationModule`（`ISettingsService` 惰性读取）。
- 三端展示：工作台 Audit 表新增 Operator 列（`TrafficAuditItem` + `GetAudit` 映射 + axaml）；
  CLI `packet audit` 行尾追加 `operator=` 列；Agent `packet_audit` 与 CLI 共用
  `PacketCommandRegistrar.FormatOutcome` 自动继承，签名导出（`packet_audit_export`）的
  ECDSA 规范化负载自动包含 Operator。工作台/CLI/Agent/规则桥（`TrafficRuleAuditBridge`）均无需各自传身份。
- 新增测试：provider 盖章与显式值优先、净化（64 截断/空白归 null）、旧 JSON 兼容、
  控制字符与超长拒绝（`PacketAuditTrailTests`）。

**P0：Assessment 最小窗口激活缺陷 — 根因已定位，验收脚本已修复（待验证）**

- 根因（几何计算 + UIA 证据，非事件丢失）：主内容区 Grid 列为 `240,Auto,*,Auto,300`，
  最小窗口 880 逻辑宽时内容列仅约 332 逻辑宽（≈415 物理px）；自动打开页面时内容标签条有 4 个标签，
  UIA 实际范围 357→902 物理px，第 4 个"授权评估"标签（759→902）大部分在标签条 ScrollViewer
  裁剪区之外。UIA 上报**未裁剪** bounds，验收脚本按元素中心 (830,163) PostMessage 点击，
  实际落在右侧 Dock 面板区域——内容区选中项从未改变。wide/medium 内容列足够宽、
  无自动打开时只有 3 个标签（授权评估是第 3 个，未越界），因此三组对照全部通过。该失败自
  Stage 9 起就存在（当时整套门禁未跑完），不是近期回归。
- 修复（`scripts/capture-assessment-visual.ps1`）：点击前尝试沿 UIA 树向上找
  `ScrollItemPattern` 并 `ScrollIntoView`（Avalonia peer 不支持时跳过）；
  兜底把点击点从"文本中心"改为"**标签 ListItem 左缘 +5px**"——溢出裁剪时只有标签项
  左侧窄条可见，文本元素起点在裁剪线之后，按 ListItem 左缘点击才能命中真实交互区。
- 产品侧跟进项（非门禁阻断，留待后续决策）：最小窗口下第 4 个内容标签只能靠滚动条到达，
  可考虑小窗口下压缩左/右面板固定宽度或标签头 MaxWidth + 省略号。

**环境**：本轮会话的 Bash 工具失效——仓库改名后会话进程的工作目录仍指向 `G:\Hookmes`，
任何子进程 spawn 即 ENOENT。恢复方式：`cmd /c mklink /J G:\Hookmes G:\Hackmes` 或在
`G:\Hackmes` 重新打开会话。Read/Edit/Write 不受影响，本轮改动全部经由文件工具完成。

### 1. 项目现状盘点

- 阶段 0–8 已落地，v0.8.0 已发布（2026-08-14 提交 `4d88b35`）。
- Stage 9 增量已完成（layout-ready 启动、`page_security_snapshot`、原子 `ReadCases`、Traffic 最小窗口压缩），
  但整套发布门禁在本次之前未对 Stage 9 源码重跑过——这正是本次 P0 要补的缺口。

### 2. P0 发布门禁（部分执行，2026-08-17）

命令：`scripts/invoke-release-acceptance.ps1 -HackermesBuildRoot G:\HackermesBuild\release-acceptance -RunResponsiveVisualMatrix`

| 步骤 | 结果 | 说明 |
| --- | --- | --- |
| [1/5] Release 全量构建 | ✅ 通过 | 14 个项目零警告零错误 |
| [2/5] 完整测试集 + TRX | ✅ 通过 | **295/295**（比 Stage 9 快照 294 多 1 个，为 `page_assessment` 旁路关闭修复新增的用例） |
| [3/5] 真实桌面 WebView2/CDP loopback | ✅ 通过 | 捕获、重放、拦截继续、请求二进制改写、响应 Fulfill **5/5**；DPAPI 密钥指纹跨进程一致 |
| [4/5] 视觉验收 | ⚠️ 部分通过 | 主窗口 Assessment/Traffic 浅/深主题 ✅；响应式矩阵 wide×2、medium×2 ✅；**Assessment minimum（880×560）失败**（见下方缺陷） |
| [5/5] Windows 打包校验 | ⏸ 未执行 | 被第 4 步阻断；按本次决定暂不执行 |

证据目录（均在 G 盘）：

- 门禁运行：`G:\HackermesBuild\release-acceptance\release-evidence\`
  - 测试 TRX：`test-results\full-tests.trx`（295/295）
  - 桌面 loopback：`desktop-loopback.log`
  - 视觉：`visual\`、`traffic-visual\`、`responsive-visual-matrix\`（wide/medium 完整、minimum 缺 Assessment）
- 缺陷复现与诊断：`G:\HackermesBuild\evidence\probe\run\`（UIA 元素转储、前后截图）

**注意**：门禁整体未通过（第 4 步失败），以上通过项是"对当前源码的真实验收证据"，
但不能表述为"Stage 9 发布门禁通过"。

### 3. 缺陷诊断：Assessment 工作区在最小窗口下无法激活

**现象**：880×560（窗口声明的逻辑最小尺寸）下，UIA 点击"授权评估"标签后，
"创建授权范围"等工作区元素始终不出现，官方脚本判定 `The assessment workspace did not become active`。

**已确认的事实**（复现 3/3，含独立重跑）：

- 仅在 **最小窗口 + 自动打开浏览器页面**（`HACKERMES_AUTOOPEN_URL`，即标签条上有 4 个标签：
  安全工具 / Welcome / 浏览器页 / 授权评估）时发生。
- 最小窗口 **不自动打开页面** 时，工作区可正常激活，"创建授权范围"等元素都在 UIA 树中
  （仅位于视口下方 y≈1154，属正常滚动区内容）。
- wide（1492×997）、medium（1250×820）两档 **带浏览器标签也能正常切换**。
- 点击后连续采样 3s/5s/10s 共 18 秒，工作区从未激活、浏览器标签始终为激活态——
  **排除了加载慢或"切回竞态"**，是标签点击本身未生效。

**根因已定位（2026-08-18，见上方"2026-08-18 增量"）**：标签条 ScrollViewer 溢出裁剪 +
UIA 上报未裁剪 bounds，点击落在右侧 Dock 面板；验收脚本已修复（ScrollIntoView + ListItem
左缘点击兜底），待 Shell 恢复后重跑 minimum 档验证。

### 4. 环境核对

.NET SDK 10.0.302、WebView2 Runtime 151.0.4129.86、G 盘剩余约 102GB，均满足门禁要求。

## 二、未完成方案（按优先级）

### P0 余项（0.9.0 出包前必做）

1. **对当前源码重跑发布门禁**：P1 收尾三件套（SHA 缓存接 Comparer / 历史统计自动刷新 /
   最小窗口标签可见性）与 0.9.0 升版尚未整套验收。
   `scripts/invoke-release-acceptance.ps1 -RunResponsiveVisualMatrix`，要求 5/5 + 全量测试通过。
   注意：本轮改动了主窗口布局（最小窗口下侧面板压缩），minimum 档视觉矩阵必须重跑确认
   Assessment/Traffic 在新布局下仍可激活与截图。
2. **出包**：Windows x64 zip + Linux x64 tar.gz + SHA256SUMS（脚本默认参数已是 0.9.0），
   归档证据目录；发布 GitHub Release 并更新 README 下载链接（当前仍指向 v0.8.0 附件）。

### P1：Traffic 缺口（全部完成）

1. ~~审计链补操作者身份~~ → ✅ 已实现并验证（2026-08-18 实现 / 08-21 构建测试通过）。
2. ~~复杂 request/response edit 规则的完整表单 UI~~ → ✅ 完成（2026-08-22，见"一、已完成 0.0"；
    剩余边界：CLI/Agent 复杂规则创建仍走 JSON 导入，二进制 body 不在表单回显）。
3. ~~结构化参数编辑补 multipart 支持~~ → ✅ 完成（2026-08-21，见"一、已完成 0.1"）。
4. ~~超大 body 增量 SHA-256 缓存~~ → ✅ 完成（2026-08-21；对比快照路径 08-22 补齐，
    见"一、已完成 0.-1"，`BodySha256` 已下沉 Base 层共享）。
5. ~~历史容量配额按工作区隔离~~ → ✅ 完成（策略文件热替换 + 立即应用 + 来源标注，2026-08-21；
    切换后工作台统计自动刷新 08-22 补齐）。

### P2：Assessment 增强（README 登记的后续项）

1. ~~外部签名式评估报告导出~~ → ✅ 完成（ECDSA 签名 + 三端入口 + ToolHost 离线验签，2026-08-21）。
2. 多用户身份提供方（当前审计只有单一本地操作者概念，P1-1 操作者字段已提供承载点）。
3. 新增受控工具 Adapter（现仅 4 个固定参数 recon Adapter；继续保持"不接破坏/利用工具"约束）。

### P3：Linux 转正

真实 Linux GUI（WebKitGTK）全链路验收 + 安全工具运行环境逐项适配。投入大，建议 P0/P1 收尾后启动。

## 三、口径说明

- **Stage 9 发布门禁最近一次于 2026-08-22 全量通过**（对含第二阶段四项 + P1-2 复杂规则表单的源码，
  5/5 步、350/350 测试），证据见 `G:\HackermesBuild\release-acceptance-20260822\release-evidence\`。
  此前 2026-08-21 的通过（315/315，Agent 强化后源码）保留在
  `G:\HackermesBuild\release-acceptance-20260821\` 作历史对照。
- **0.9.0 源码（P1 收尾三件套 + 升版）尚未重跑门禁**；对外表述不应声称 0.9.0 已发布验收，
  最新已发布版本仍为 v0.8.0。
- 2026-08-17 的部分通过证据保留在 `G:\HackermesBuild\release-acceptance\`（历史对照用）。
- Linux 包为交叉发布产物，未经真实 Linux GUI 验收，对外表述不应声称"Linux 已验收"。
