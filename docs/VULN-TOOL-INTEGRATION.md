# 漏洞工具融合集成 — 总体计划与阶段状态

> 本文档是"让 Hackermes Agent 具备自主寻找与验证漏洞能力"工具融合工作的**总控文档**：
> 记录总体任务阶段划分、每个阶段的范围/状态，以及后续入口。运行时行为细节见
> `docs/AGENT-RUNTIME.md` 的"第一梯队漏洞工具适配器"与"OA POC 探测"章节。

## 总体阶段（总任务）

| 阶段 | 范围 | 状态 |
|---|---|---|
| 阶段 0 · 工具普查 | 摸底 `G:\securitytools` 全部工具形态（CLI/GUI/脚本），确定适配候选与延后项 | ✅ 完成 |
| 阶段 1 · 第一梯队 | 侦察/泄露枚举 + 中间件·反序列化检测 + heapdump 分析 + vCenter 验证，共 8 个 CLI 适配器接入 ToolHost 授权管线 | ✅ 完成 |
| 阶段 2 · OA POC 框架 | OA-EXPTOOL 97 条国内 OA POC 的单发（single-shot）runner + `detect.oa_poc.list` / `detect.oa_poc.probe` 适配器 | ✅ 完成（含复查修复） |
| 阶段 3 · 靶场联调与实测 | 解析器实测定标（批次 A）、联网情报（批次 B）、门控+Skill（批次 C）、Shiro/Struts2/Nacos/FastjsonExploit/JNDI 监听（批次 D） | ✅ 完成（命中路径待真靶，见批次 A 矩阵） |
| 阶段 4 · 补齐靶面 | cf 密钥旁路 ✅（DPAPI 暂存 + 环境变量注入 + 只读验证）；VulHub 矩阵与本地靶场扩展 **取消**（判定见批次记录）；其余靶面按需 | ✅ 完成 |
| 阶段 5 · 编排与收束 | 利用前强制门控 ✅、Skill 链 ✅、联网情报 ✅、密钥旁路 ✅；剩余：实战首跑调优与技能迭代 | 🔶 实战调优中 |

**当前阶段：阶段 5 实战调优（2026-08-29）。** 计划内的开发工作全部落地（16 个授权
适配器 + 联网情报三件套 + JNDI 检测闭环 + 密钥旁路 + 强制门控 + 3 条技能链，
658 用例回归基线）；下一步是在真实授权评估中使用 agent，把实战暴露的问题回修到
解析器/技能/提示词。VulHub 真靶矩阵如未来需要可按批次 A 矩阵随时重启。

## 阶段 2 复查修复记录（2026-08-29）

复查发现并修复 `oa_poc_runner.py` 的 3 个缺陷（均有 mock 靶场回归覆盖）：

1. **提取器替换对后续请求不生效**：`enumerate(paths)` 绑定旧列表且 header 按整体
   请求序号取值——致远 session 串接等"提取 → 下一个请求"类 POC 全部失效。改为
   paths/bodies/Rheader/Gheader 四个列表原地替换。
2. **header 索引语义错误**：上游按 GET 序号 / POST 序号各自计数（遇 "None" 重置），
   原实现按整体请求序号取值，POST+GET 组合 POC（如致远）第二个请求带不上 Cookie。
   修正为双游标。
3. **header 提取序列化**：py3 `str(response.headers)` 是 dict 格式，贪婪 `(.*)`
   会把相邻键值噪声一起捕获；改为原始 `K: V` 行格式（贴近 py2 上游行为）。

另：runner 目标校验放行 IPv6（`http://[::1]:port`）。修复后致远提取器链与通达
单发 POC 均在 mock 靶场命中。

## 阶段 3 第一增量：本地靶场自动化回归（2026-08-29）

- **`third_party/tools/_testrange/testrange_server.py`**：确定性 mock 靶标——
  通达 session 泄露（紧凑 JSON）、致远提取器链（严格校验第二个请求确实回放了
  提取出的 JSESSIONID 值）、swagger api-docs（含 parameters 触发解析）、
  dumb-HTTP git 仓库（由测试用真实 git CLI 生成）。服务器自选临时端口并在
  `RANGE_READY port=` 行回显，消除并行测试的端口竞态。
- **`VulnTargetRangeTests`（3 个）**：全部走 `AuthorizedToolCatalog.BuildInvocation`
  的生产参数面实跑，再经 `ReconObservationParser` 断言 finding 级别——
  通达 HIT→Medium、致远 HIT→High（证明提取器替换真实发生）、GitHack 真实
  `git clone`（哑协议）恢复标记文件→Medium、swagger-hack 枚举端点→Medium。
- **附带修复**：
  - GitHack 兼容补丁：恢复输出改到当前工作目录（ToolHost scratch）、bundled
    data 仍从脚本根加载——工具目录在只读安装下保持不可变。
  - swagger-hack2.0 兼容补丁：CSV 结果逐行镜像到 stdout（`[SWAGGER] METHOD url | status`），
    证据（仅 stdout）因此携带枚举结果。
  - `recon.swagger_api.enum` 新增可选 `path` 输入（指向 api-docs 地址；
    上游 `check()` 只探测传入 URL 本身），与 dirsearch/未授权扫描的发现流程衔接。
- **靶面覆盖矩阵（剩余待真靶）**：weblogic（T3 协议）、fastjson（JNDI/DNSLog 回调）、
  vcenter（HTTPS 靶标）、heapdump（真实转储文件）、svn（wc.db 结构）、ds_store
  （真实 .DS_Store 二进制）→ 接入 VulHub（需 Docker）或回调设施后补齐。

## 阶段 3 批次 A：二进制工具实测定标（2026-08-29）

对三个无法用 mock 完全模拟输出的二进制工具做了实测定标，并按真实行为修正解析器——

- **JsonExp（detect.fastjson_jndi.scan）**：
  - 实测确认工具**必须**指定回调（-l/-r/--dnslog 三选一），否则拒绝运行 →
    适配器改为 ldap 或 rmi **必填**（操作者提供监听）。
  - 正常输出只有 `[+] 序号：N` + payload 正文，**没有命中判定**——判定来自监听端
    （LDAP/RMI listener 或 DNSLog 后台）。原解析器把 `[+]` 计数当候选会**每次运行
    误报 Medium**，已移除；仅当输出出现显式判定词（存在漏洞/vulnerable）才报 High。
- **JDumpSpider（exploit.heapdump.analyze）**：用 jcmd 对持有真实
  HikariDataSource 凭据的 Java 进程转储 heapdump 实测。真实节结构为
  `=== 横幅 → 类别名 → ----- → 值 | "not found!"`，且绝大多数类别是 not found。
  原按横幅计数会把全 not-found 的运行误报 High；已重写为按节内容判定
  （排除 "not found!"），消息中列出真实提取到的类别名。
- **VcenterKiller（exploit.vcenter.verify）**：对返回 2xx 的**任意**端点（普通 JSON
  mock）都会打出 `[+] Upload success, try command execute.`——工具输出是乐观型，
  单凭它不能确认 vCenter 沦陷。解析器从 High 降级为 **Medium 候选**
  （vcenter-verification-candidate），明确要求人工在靶标上复现后再立案；与批次 C
  的"利用前强制复核"门控衔接。
- 靶面覆盖矩阵更新：jsonexp / jdump / vcenter 三项 → **miss 路径 + 语义已实测**；
  命中路径仍需真靶或真实监听（接入 VulHub 后补齐）。

## 阶段 3 批次 B：联网情报（web_search / CVE 查询 / 工件阅读）（2026-08-29）

新增三个数据只读的 agent 工具（均不执行任何网络内容）：

- **`web_search`**：有界搜索结果（title/url/snippet，1-10 条）。
  - **API 模式**：Brave Search API 或 Serper（Google）API，Key 存于 DPAPI secret
    store（`ai.webSearchApiKey`，设置窗口录入，不落 settings.json）。
  - **降级方案（默认）**：无 Key 时驱动内置浏览器打开 Bing，经 CDP
    `Runtime.evaluate` 提取 `li.b_algo` 结果块后关闭页签；被拦截/DOM 变更时报错并
    引导配置 API。
  - 提供商四选一（AI 设置 → 联网情报）：auto（有 Key 走 Brave，否则浏览器）/
    browser / brave / serper。
- **`vuln_cve_lookup`**：单个 CVE 的有界摘要（描述、CVSS 分数与向量、references ≤8），
  NVD API 2.0 优先、OSV 兜底、两者皆失返回说明；可选 NVD API Key 提升限额
  （DPAPI：`ai.nvdApiKey`）。
- **`agent_artifact_list` / `agent_artifact_read`**：补完"资料下载为数据"闭环——
  列出工件库内容并按 offset/limit 分页读回文本；二进制工件（exe/jar/zip 等，按扩展名
  与 NUL 嗅探双重判定）一律拒绝进入模型上下文，仍只能走 ToolHost 适配器。
- 测试：WebIntelTests 7 个（API 解析、NVD/OSV 兜底、Bing 提取表达式、工件分页与
  二进制拒绝）；全量 641 用例，除既有时序抖动 1 个外全绿。
- 待真机验证：浏览器降级路径需在桌面应用里实测（WebView2 + CDP 在单测中无法驱动）。

### 运维记录：构建输出目录迁移

排查本机测试偶发挂起时发现 `G:` 是一块 PHILIPS U 盘（exfat），此前的构建输出
`G:\HackmesBuild\workspace\bin` 已从盘上消失，testhost 对仍在映射中的 DLL 分页时
无限等待。已按 `Directory.Build.props` 的既有约定设置用户级环境变量
`HackermesBuildRoot=C:\HackermesBuild`，构建输出迁移到本地 SSD；如在其他机器构建，
出现同类挂起时同样设置该变量即可。

## 阶段 3 批次 C：编排与收束——利用前强制门控 + Skill 链固化（2026-08-29）

- **利用前强制复核门控（控制平面）**：`AssessmentControlPlane.CreatePlan` 现在拒绝
  没有同目标检测证据的利用型适配器（当前为 `exploit.vcenter.verify`）。解锁条件
  二选一：① 同一计划中、利用步骤**之前**存在针对同一目标的检测步骤；② 控制平面
  已有"来自活动（未吊销、未过期）scope、job 已完成、且 scope 目标覆盖该目标"的
  检测证据（`AuthorizedToolCatalog.IsDetectionStage` 定义检测型集合）。跳阶段直接
  利用会在建计划时报错，错误信息引导先跑检测阶段；系统提示词同步告知 agent 该
  门控行为。分类方法：`IsExploitationStage` / `IsDetectionStage`（新利用型适配器
  落地时在此登记）。
- **Skill 链固化（3 条内置工作流）**：`BuiltInSkillCatalog` 首次启动播种（幂等、
  不覆盖用户编辑），默认**禁用**，操作者在 Skills 页启用后生效：
  1. `builtin.leak-recon-chain` — 信息泄露侦察链（dirsearch → 四类泄露枚举 → finding）；
  2. `builtin.oa-poc-chain` — 国内 OA 探测链（指纹 → list → probe → finding）；
  3. `builtin.springboot-heapdump-chain` — actuator → heapdump 下载 → 凭据提取链。
- 测试：`OrchestrationGateTests` 6 个（无证据拒绝、计划内先检测后利用放行、
  活跃 scope 证据放行、异目标证据拒绝、证据 scope 已吊销拒绝、Skill 播种幂等且
  用户编辑优先）。全量 647 用例，除既有时序抖动 1 个外全绿。

## 阶段 3 批次 D：Shiro/Struts2/Nacos 接入 + JNDI 监听 + FastjsonExploit（2026-08-29）

- **`detect.shiro.scan`（shiro_tool.jar，vendor 自本地库）**：Shiro rememberMe key
  爆破；stdin EOF 驱动交互菜单（未命中自动退出）。解析器按实测/源码标记分级：
  `is use shiro` + 未出现 `get shiro key fail` → High（key 已破）；仅确认框架 →
  Medium；`target may not use shiro` → 无 finding。需 Java 8+（HACKERMES_JAVA_PATH）。
- **`detect.struts2.scan`（Struts2-Scan，GPL-3.0，外部下载 + 代码审查通过）**：
  S2-001~S2-057 系列 OGNL 漏洞检测（`-u` 全量 + `-q` 只留命中）。审查结论：网络
  外联仅目标 URL（192.168.100.8 仅为帮助文本示例），exec 内容为发往目标的 OGNL
  payload（工具本职）。vendored 依赖：click（BSD-3-Clause）。命中格式
  `[*] {url} 存在漏洞: S2-0xx` → High。
- **`detect.nacos.scan`（Hackermes 自研只读探测）**：未授权用户列表/配置读取/
  集群信息/控制台暴露/metrics 6 项只读检查（无任何写操作），输出
  `[NACOS-HIT] id | severity | url`，命中按项映射 finding 严重级。
- **`jndi_listener_start` / `jndi_listener_hits` / `jndi_listener_stop`**：本地
  JNDI 回连监听（仅绑 127.0.0.1，自动端口，15 分钟自动过期，最多 4 个并发）。
  任何入站连接即证明目标执行了注入的回调地址——fastjson 检测闭环：
  `jndi_listener_start` → `detect.fastjson_jndi.scan(ldap=127.0.0.1:<port>)` →
  `jndi_listener_hits` 有记录即确认命中。监听只记录不回送对象，检测 only。
- **`exploit.fastjson_payload.generate`（FastjsonExploit 0.1-beta2，c0ny1 原作 +
  Hackermes 构建）**：本地 mvn 构建（补丁：pom source/target 1.8、org.javassist
  3.29.2-GA、ClassClassPath 修补）；payload 生成走 JVM `--add-opens` + gadget
  白名单（TemplatesImpl1/2、BasicDataSource1/2）；**利用型，已纳入利用前强制
  门控**（IsExploitationStage）。
- 测试：BatchDTests 5 个（JNDI 监听真实 socket 回连记录/停止、4 适配器参数面与
  范围/白名单校验、门控分类、三解析器行为）；全量 652 用例全绿。
- Struts2-Scan 仓库说明：`HatBoy/Struts2Scan` 已 404，现名 `HatBoy/Struts2-Scan`。

## 阶段 4：cf 密钥旁路（2026-08-29，VulHub 矩阵取消）

**必要性结论**：cf 旁路必要（发现链产出的云 AK/SK 需要在审计环境内闭环验证，且
必须解决密钥落盘）；VulHub 矩阵与本地靶场扩展**取消**——新工具命中标记已从源码
定标、fastjson 确认已由 JNDI 监听权威判据闭环、解析器宁漏勿误，剩余命中路径差异
由实战首跑暴露后回修；既有 mock 回归测试（VulnTargetRangeTests）继续作为回归基线
保留，不再扩建。

**密钥旁路全链路（密钥零落盘）**：

1. `cloud_credential_stage`（Dangerous，逐次确认）：agent 把评估中发现（heapdump/
   git 泄露）的云 AK/SK 暂存进 **DPAPI secret store**（≤60 分钟自动过期），只返回
   不透明 token（`cc-<16hex>`）；AK 格式/SK 格式/provider 白名单校验，密钥永不回显、
   不进计划/票据/证据/日志。
2. `probe.cloud_aksk.verify`（检测型）：输入只含 token（计划持久化的 JSON 零密钥），
   `BuildInvocation` 产出带 `SecretReference` 的调用；**ToolHost 子进程**用自身 DPAPI
   解析引用 → `CloudCredentialEnvironment` 映射为各云 SDK 标准环境变量
   （alibaba/aws/tencent/huawei 四家）→ 注入 vendored cf.exe 进程环境。解析失败/
   过期以明确错误拒绝执行。
3. 只读验证仅 `ls` / `perm`（列资源/列权限）；**接管控制台、云函数执行等利用操作
   明确不做**，保持操作者手工。解析器保守：cf 正向输出 → Medium 候选（密钥有效），
   证据全文保留；输出定标待真实云账号。
4. `cloud_credential_clear` 随时可清（过期自动清）。
- 测试：CloudCredentialTests 6 个（暂存往返、格式拒绝、清除、环境变量映射、
  BuildInvocation 零密钥断言、解析器行为）；全量 658 用例，除既有时序抖动外全绿。

## 左侧工具栏 GUI 工具集成（2026-08-29，实战配置）

应用户要求，将 G:\securitytools 中适合人工使用的 GUI 综合利用工具**直接内置**进
左侧"安全工具"面板（`gui.*` 目录 + BundledTools 声明，不依赖 E:\tool 等外部路径）。
JavaFX 前置已解决：内置 OpenJFX 21.0.5 平台模块（`_runtime/javafx/lib`，Maven
Central 拼装），`ToolLaunchService` 对 RequiresJavaFx 工具自动以
`java --module-path ... --add-modules ...` 启动，Swing 工具直接 `java -jar`。
仅面板可用（人工操作），**不进 agent 工具列表、不经 ToolHost**。

| 类别 | 工具 |
|---|---|
| 漏洞利用 | ShiroExploit（key 爆破+ysoserial）、Struts2 全版本检测（Swing）、ThinkPHP 综合、TomcatPass、NacosExploitGUI、XXL-JOB ExploitGUI、JenkinsExploit-GUI、通达 OA 综合、帆软 FrChannelPlus、海康综合、大华综合、MYExploit 综合、MDAT 数据库综合利用 |
| 加解密 | DecryptTools 综合加解密 |
| Web 与流量 | API-T00L |

manifest 以 `uiKind` 标注，与 agent 适配器清单明确区分。JavaFX 模块与 Java 21 运行
时缺失时条目自动显示不可用原因。

## 明确延后项（含原因）

- **cf（云 AK/SK 利用）**：凭证会明文持久化进 plan 存储（`assessments.json`），
  需要 ToolHost 票证密钥旁路设计后再接入（阶段 4）。
- **VcenterKit 附带脚本**：signxml/lxml/bitstring 等依赖不在内置 Python 运行时内。
- **GUI 型综合工具**（Struts2/Tomcat/Nacos/Jenkins 等 30+ 个）：无头不可驱动，
  待引入 CLI 等价物（阶段 4）。

## 已落地能力速览

### 阶段 1 适配器（8 个）

| 阶段 | AdapterId | 输入要点 |
|---|---|---|
| 信息收集 | `recon.git_leak.scan` / `recon.svn_leak.scan` / `recon.ds_store.scan` / `recon.swagger_api.enum` | 共享 web 端点 JSON（target/scheme/port） |
| 漏洞验证 | `detect.weblogic_t3.scan` | target + port（默认 7001） |
| 漏洞验证 | `detect.fastjson_jndi.scan` | web 端点 + 可选 ldap/rmi 回连（有界 host:port）+ method |
| 漏洞验证 | `exploit.heapdump.analyze` | `file`（仅工件库 `agent-tools/` 内文件名，需 Java） |
| 漏洞利用 | `exploit.vcenter.verify` | mode 白名单 + action（scan/upload/getcookie）+ command ≤128 |

### 阶段 2 适配器（2 个）

| AdapterId | 输入要点 |
|---|---|
| `detect.oa_poc.list` | 无参数（枚举 97 条 POC：模块/名称/严重级） |
| `detect.oa_poc.probe` | web 端点 + `module`（如 tongda/weaver/seeyou/yonyou）+ 可选 `poc` 单条文件名 |

- `oa_poc_runner.py`（Hackermes 自写，AGPL-3.0 POC 库来自 OA-EXPTOOL 0.83）：
  复刻 nuclei 风格 word/status 匹配器与请求序号提取器（如致远 session 串接），
  只输出 `[HIT]/[MISS]/[ERROR]/[SUMMARY]` 行，不落盘、不执行上游利用载荷。
- 解析器把 `[HIT]` 映射为 finding（严重级取自 POC 定义），`detect.oa_poc.list`
  走通用解析。

## 复查修复记录（阶段 1 收尾复查，2026-08-29）

- `detect.fastjson_jndi.scan` 的 `method` 非法值由静默忽略改为显式拒绝。
- 6 个会产生写盘副作用的适配器（GitHack/SvnExploit/ds_store/swagger/WeblogicScan/
  JsonExp）工作目录改为一次性临时目录（`%TEMP%\hackermes-toolhost\<guid>`），内置
  工具目录保持不可变，兼容只读安装；JsonExp 模板改为随 exe 目录绝对路径传递。
- `ParseHeapdump` 分节横幅正则收紧（`=====` 行不再与值行混计）。
- `manifest.json` 校验通过（18 个工具条目），目录无测试残留。

## 维护约定

- 新增适配器必须同时落地：`AuthorizedToolCatalog`（常量/描述/校验/参数构造）、
  `ReconObservationParser`（输出→finding）、`DesktopToolCatalog`（面板）、
  `third_party/tools/manifest.json`（来源/许可证）、测试与提示词阶段指引（如适用）。
  利用型适配器还须登记进 `IsExploitationStage`（自动纳入利用前门控）。
- 回归基线：`dotnet test tests/Hackermes.PacketTraffic.Tests` 全绿（658 用例，11 秒）。
  历史上的两个偶发项已于最终检查中**根治**：
  1. 全量套件偶发卡死/`Page_navigate` 挂起 —— 根因是无 Avalonia 应用的进程里
     `UiThreadBridge.Post` 把工作投进永不被泵的队列；已加无头守护
     （`Application.Current is null` 时同步执行，生产语义不变）。
  2. `Network_store_derives_security...` 抖动 —— 根因是 `NetworkStore` 的查询
     （ReadSecurityMetadata）依赖 UI 线程泵时机；已把查询数据源切到同步的
     `_byRequestId`（可观察集合仍走 UI 线程镜像），agent 查询变确定性。
