# Hackermes 授权评估 Agent —— Skill 开发文档

> 记录把 `G:\skills\网安skill{先保存再下载}` 中的网络安全技能/工具融入本 Agent 的进展、甄别分析、已接入/未接入清单与下一步。只读分析 + 实现均已落地，本文为沉淀文档。
> 同步存放于：`docs/SKILL_DEVELOPMENT.md`（本文件为根目录镜像）。

---

## 1. 目标

让 Agent 在授权范围内的漏洞扫描中，尽可能利用现有 + 随附的网络安全工具与技能，形成一套**覆盖全面的自动化漏洞扫描能力**：
- 侦察（主机/端口/头/目录/WAF/存活）
- 指纹与技术识别
- 逐类验证（注入、未授权/BOLA、XSS/SSRF/IDOR/JWT、传输与配置）
- 自动确认漏洞并产出 PoC
- 报告 + 证据 + 流程归档到专门文件夹，人工可查

---

## 2. 核心架构（实现所依赖的接缝）

| 组件 | 作用 |
|---|---|
| `AuthorizedToolCatalog` | 把**结构化 JSON 输入**转成受限 `ProcessStartInfo.ArgumentList`，绝不接受任意命令行。每个 adapter 都过 `EnsureAuthorizedTarget`（范围/授权/审批门禁）。 |
| `ToolHost`（Hackermes.ToolHost） | 短命独立进程执行外部工具，有界输出。运行任何 catalog 注册的 adapter。 |
| `ReconObservationParser` | 把工具 stdout 解析成观察项（含 severity + PoC），`AddEvidence` 再落为 finding。 |
| `AssessmentControlPlane` | scope→plan→approval→job 生命周行 + 审计 + 证据 + finding。 |
| `IAssessmentReportArchive` | 每任务写一个文件夹（report.md/case.json/evidence/audit.json/signed-report.json）。 |

> 关键点：**"skill"（LLM 提示词包）≠ "工具"**。`AuthorizedToolCatalog` 只能包装**可运行、有界、可解析输出**的 CLI 工具；纯提示词类 skill 属于 agent 知识/指引，进不了 ToolHost。

---

## 3. 已完成实现

### 3.1 修复：recon 作业因严重程度非法整批失败
- `ReconObservationParser`：缺 HSTS/CSP/防frame 的观察项 severity 由 `"Warning"` 改为 `"Low"`（缺 XCTO 为 `Info`）。
- `NormalizeSeverity` 兜底接受 `"warning" => "Info"`。
- `AddEvidence` 单条观察非法时 try/catch **跳过而非炸掉整作业**。
- 效果：之前 41/160 个侦察作业 `job.failed: Severity must be...`，修复后不再发生。

### 3.2 自动确认 + PoC 全链路
- `AssessmentFinding` 增加 `PoC` 字段（有界 2000 字符，进审计：`poc=present/none`）。
- `assessment_create_finding` 工具新增 `poc` 参数（agent 可上传观察到的复现）。
- `ReconObservationParser` 自动确认规则：
  - **HTTPS→明文HTTP 降级**（http.headers/get）：自动 **Medium** + PoC（`curl -sSI` + Location）。
  - **`probe.sqlmap.inject`**（含 sqlmap 确认标记 `is vulnerable`）：自动 **High** + PoC；`not injectable` 等负面抑制。
  - **`probe.unauthorized.access`**（`: Vulnerable` 标记）：自动 **High** + PoC URL。
- 报告导出（json/markdown/html）自动渲染每个 finding 的 PoC。

### 3.3 可运行工具的受限适配器（unused→used）
- 新注册到 `AuthorizedToolCatalog`：
  - `probe.sqlmap.inject`：`sqlmap --batch --level 1 --risk 1 --technique=BE --threads 1`（限定参数、有界、单目标）。
  - `probe.unauthorized.access`：`Unauthorized-Vul.py -u <url> -t 1`（40+ 未授权端点检测）。
- 两者均走授权/批准/范围门禁；`assessment_tools` 自动暴露给 Agent，Agent 用 `assessment_authorize_and_run`/`assessment_create_plan` 调用。

### 3.4 报告归档到专门文件夹
- 新增 `IAssessmentReportArchive`，每任务写入 `%LocalAppData%\Hackermes\reports\<jobId>\`：
  - `report.md`（发现含 PoC + 证据概览 + 审计时间线）
  - `case.json`（结构化快照含 PoC）
  - `evidence/NN_<source>.txt` + `evidence/index.md`（原始脱敏证据）
  - `audit.json`（流程/审计链）
  - `signed-report.json`（有签名密钥时）
- 触发：`assessment_authorize_and_run` 完成自动归档（结果带 `ArchiveFolder`）；工具 `assessment_report_archive` / `assessment_report_open`；CLI `assessment report-archive <job>`；桌面"授权评估→归档并打开文件夹"按钮（归档 + explorer 打开）。

### 3.5 全面扫描方法论（技能知识注入）
- 重写 `DefaultAssessmentSkill`（授权评估）instructions 为系统性闭环：
  **侦察 → 指纹 → 逐类验证 → 立案 → 报告/归档**，覆盖 Web/API/JWT/BOLA/XSS/SSRF/SQLi/注入/传输与配置；并强调：只在授权目标、只用证据、PoC 不虚构、完成后归档。
- 该指引随系统提示注入 Agent（已通过 7000 上限 + 安全断言测试）。

### 3.6 语料资源优化 + 子域枚举 + 资源可见性
把外部 `G:\skills\网安skill{先保存再下载}` 中可用的**语料**做成**自包含资产**（不再依赖外部路径），并让 Agent 真正用起来：

- **自包含语料库**（拷贝并整理到仓库内 `third_party/resources/corpus/` 与 `third_party/tools/recon.subdomain.terminal/`）：
  - `subdomains.txt`（292 行子域字典，源自 Orizon recon-dominator）
  - SQLi payload：`sqli-auth-bypass.txt`(96) / `quick-sqli.txt`(77) / `generic-sqli.txt`(268) / `nosql.txt`(22)
  - `ldap-fuzzing.txt`(26)、`special-chars.txt`(32)、`command-injection-commix.txt`(8262, 926KB)
- **新增受限适配器 `recon.subdomain.enum`**：对授权根域用内置子域字典做 DNS 枚举，解析成功即产出 `Info` 观察（`subdomains-resolved` + PoC）。脚本为自研 `subdomain_enum.py`（stdlib，有界输出，`assessment_authorize_and_run`/`assessment_create_plan` 可调用）；已实测：`www.example.com -> 104.20.23.154`，与解析分支匹配。
- **新增 `assessment_resources` 工具**：列出可用语料（id/名称/路径），Agent 自主选择输入；`assessment_tools` 继续列出全部受限适配器。
- **新增受限探针 `probe.param.corpus`（§7-D 落地）**：自研 `param_corpus_probe.py`（stdlib；对每个 payload 发有界 GET，比对状态码/响应体长度/错误差异），把 `sqli-auth-bypass` / `quick-sqli` / `generic-sqli` / `nosql` / `ldap-fuzzing` / `command-injection` 语料喂给单个参数，**只产出 `CANDIDATE` 行 → parser 映射为 Medium“注入候选、需复核”观察（带 PoC，明确 NOT a confirmed vulnerability）**，不自动判 High；确认仍走 `probe.sqlmap.inject` 或人工复核。
- **方法论同步**：RECON 阶段加入 `recon.subdomain.enum`（对 `*.domain` 范围自动枚举子域），并提示先 `assessment_resources` 看语料再选输入；VERIFY 阶段对可疑参数先 `probe.param.corpus`（候选），再用 `probe.sqlmap.inject` 确认（High+PoC）。
- 授权门禁不变：`recon.subdomain.enum` 仅对授权根域（精确或通配 `*.example.com`）可运行；`probe.param.corpus` 仅对精确授权 Web 目标 + 且 `EnsureAuthorizedTarget` 通过。

---

## 4. `G:\skills\网安skill{先保存再下载}` 甄别分析

实测盘点（剔除 `ai破甲`）：

| 类别 | 数量 | 说明 |
|---|---|---|
| skill/提示词文档 (.md/.mdc/.txt) | 11,925 | LLM 智能体技能说明，**非可运行工具** |
| 脚本 (.py/.sh/.js/.go/.c…) | 4,382 | 各仓库实现/脚手架，多为 `scripts/process.py` 粘合脚本，**未经审计** |
| 配置/管道 (.yml/.json) | 488 | 配置 |
| 特征规则 (.yar/.ql CodeQL) | 30 | 检测规则，**无执行宿主** |
| 可执行文件 (exe/dll/apk/war) | 2 | 极少 |
| 可用语料 (wordlist/payload) | 少量 | `Eyadkelleh/awesome-skills-security` 的 SQLi/auth-bypass/命令注入/LDAP 语料、`Orizon` 的 subdomains 字典 |

**结论：这些仓库几乎不提供新的可运行 CLI 工具**。价值集中在：方法论/思路、以及部分 wordlist/payload 语料。

---

## 5. 已接入 vs 未接入清单

### 已接入（used）
- 真实工具集（harness 自带的可运行工具，均已成为 adapters）：`recon.dns.resolve / nmap.quick / nmap.service / http.headers / http.get / httpx.probe / dirsearch.quick` + `recon.subdomain.enum` + `probe.param.corpus`（候选）+ `probe.sqlmap.inject` + `probe.unauthorized.access`。
- 由档案驱动的自动确认：SQLi(High)、未授权/BOLA(High)、HTTPS→HTTP 降级与缺安全头(Medium)，全部带 PoC。
- 报告/证据/流程归档（`assessment_report_archive` / `assessment_report_open` / 桌面按钮 / CLI）。
- 全面扫描方法论注入（`DefaultAssessmentSkill`）。
- 语料资源（`third_party/resources/corpus/` 与 `recon.subdomain.terminal/`）+ `assessment_resources` 可见性，供 Agent 在扫描中选用。

### 未接入（not used）+ 原因
| 仓库/资产 | 为何未用 |
|---|---|
| `akashrpatil/awesome-offensive-security-skills` | 几乎全为提示词技能包 + 粘合脚本，无可运行 CLI 工具 |
| `Anthropic-Cybersecurity-Skills`（mukul975/Njones17/kangoulya）×3 | 754+ 个全为 LLM 提示词包，非工具；强聚合、注入面大 |
| `Arenbai/SecSkills`、`Orizon/claude-code-pentest`、`transilienceai/communitytools` | Agent 编排/prompt；脚本无稳定入口、未审计 |
| `xiaolai/Claude-BugHunter`、`galact/galact-Skills` | 核心是 prompt 模板/规则，非独立工具；无解析接口 |
| `Eyadkelleh/awesome-skills-security` | 有可用 payload/字典**语料**，但未做成"工具输入模糊器"，因为通用错误差异模糊器会大量误报 High，违背自动确认的严谨性。仅作方法论文档引用 |
| `26zl/cybersec-toolkit` | 工具+Skill 聚合，依赖多、未审计，无干净 CLI |
| `GoldenWing-360`、`briiirussell`、`drapala`、`HexRaysSA/ida-...`、`anthropics/claude-code-security-review` | 防御/蓝队/审计/IDA/CI 参考：YARA/CodeQL 无执行器；IDA 插件需 IDA 宿主；CI Action 非本机扫描 |
| `trailofbits/skills` | 精选 but 以 CodeQL/Semgrep 规则为主，无对应执行器 |
| `recon.layer`（Layer.exe） | 实测裸跑挂起，无有界 CLI，无法做受限适配器 |
| `ai破甲` | **越狱/破甲技能，明确不参与** |

---

## 6. 测试与验证状态

- 修复/新增均补了测试：`ReconObservationParserTests`（SQLi/未授权/降级/子域枚举/语料候选确认与抑制 + `CorpusResources` 存在性）、`AuthorizedToolHostTests`（catalog 归一化 + 越权拒绝 + 子域枚举归一化 + 语料探针归一化/未知语料拒绝）、`AssessmentStage7CTests`（缺头 recon 完成 + PoC 落报告）、`AssessmentReportExportTests`（归档写 report/PoC/evidence/audit）。
- 已跑通过的选集：prompt/catalog/assessment/report 相关 **全过**；此前全量 605~606 通过（唯一失败的 `PageSecuritySnapshotTests` 为既有 flaky CDP 时序测试，独立跑通过，与本改动无关）。
- 真实工具实跑验证：`Unauthorized-Vul.py` 本地 200 服务器 → `- rsync: Vulnerable` → parser 确认 High + PoC；`subdomain_enum.py` 对真实域名 → `www.example.com -> 104.20.23.154` → `subdomains-resolved` 观察；`param_corpus_probe.py` 对本地 SQLi 端点 → `CANDIDATE param=id payload=' or 1=1 status=500 vs baseline=200` → parser 产出 Medium“候选、需复核”观察。

---

## 7. 下一步

- **(A) 接入语料资源 —— 已完成**：语料已自包含化；新增 `recon.subdomain.enum`（子域枚举，用内置字典）与 `assessment_resources`（资源可见）供 Agent 在扫描时选用。**保持"不引入错误差异自动确认"**——语料用于扩大侦察/覆盖面，确认仍靠 sqlmap/unauthorized（带 PoC）或人工复核，避免假阳性。
- **(B) 维持现状**：以完整方法论驱动现有真实工具 + 子域枚举 + 资源可见性，即可完成全面覆盖闭环。
- **(C)（若确需）最小验证型探针**：仅针对**已确认线索**（如观测到的 `organizationCode`/`appSecurityKey`/降级中的鉴权 cookie）做**授权范围内**确认性检测，映射为 Medium/High + PoC。此路可把"自动确认 High"落地为真漏洞，但需逐一为验证类适配器补 parser 分支；且属**主动探测**，仅在已授权目标可用。
- **(D) 把 payload 语料接入受限验证 —— 已完成**：`probe.param.corpus` 用 `sqli-*`/`nosql`/`ldap-fuzzing`/`command-injection` 语料对单参数做有界请求，**仅产出 Medium"候选、需复核"观察**（带 PoC，明确非已确认漏洞），交由 `probe.sqlmap.inject`/`unauthorized` 或人工确认，**不自动确认为 High**。已实跑验证产生 `CANDIDATE ... status=500 vs baseline=200`。
- **安全护栏（贯穿始终）**：只对授权目标、只用观察到的证据、PoC 不虚构、有界输出与超时、绝不扫描范围外主机；`ai破甲` 类排除。

---

## 8. 结论

- 从 `G:\skills\网安skill{先保存再下载}` 主要获得的是**方法论/知识**与**部分语料**，而非新的可运行工具。
- "全面漏洞扫描能力"目前由**真实工具集 + 完整方法方法论编排 + 自动确认(PoC) + 报告归档**共同实现。
- 若要进一步把语料用起来，走 **(A)**；要真正把"已确认线索"判别为 High，走 **(C)** 且严格限定授权范围。
