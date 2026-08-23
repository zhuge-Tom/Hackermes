<!-- markdownlint-disable MD013 -->

# Hackermes 路线图（完成项与后续计划）

> 更新：2026-08-22。本文是长期路线的总览；会话级实施细节见 [PROGRESS.md](PROGRESS.md)，
> 三端能力对等矩阵见 [docs/STAGE6-GAP-MATRIX.md](docs/STAGE6-GAP-MATRIX.md)，
> 架构分层见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 一、当前状态速览

| 维度 | 状态 |
| --- | --- |
| 里程碑 | 阶段 0–9 全部落地；**源码已升版 `0.9.0`**，最新发布版本仍为 v0.8.0 |
| 测试基线 | **397/397 通过**（Release 构建 0 错误） |
| 最新门禁 | Stage 9 门禁 5/5 于 2026-08-22 对"P1-2 复杂规则表单后"源码通过（350/350） |
| 门禁缺口 | 其后叠加的 P1 收尾三件套与 0.9.0 升版**尚未重跑门禁**（见 P0-1） |
| 平台 | Windows 全链路验收 ✅；Linux 为交叉发布产物，GUI 未验收 |

常用验证命令：

```powershell
dotnet build -c Release          # 全量构建
dotnet test -c Release           # 全量测试
scripts/invoke-release-acceptance.ps1 -HackermesBuildRoot <目录> -RunResponsiveVisualMatrix   # 发布门禁 5 步
```

## 二、已完成

### 平台主体（Stage 0–8，构成 v0.8.0）

- 安全浏览器工作台（WebView2/CDP）：捕获、拦截改写、Fulfill、重放、DOM 预览。
- Traffic 数据包工作台：筛选分页、原始编辑、语义 Diff、Repeater 多轮草稿、持久规则、
  分析标注、HAR/JSON 归档导入导出、历史容量治理（全局 + site 配额）。
- 授权评估子系统：范围/计划/审批/作业生命周期、有界 ToolHost 隔离执行、证据 SHA-256、
  HMAC 审计链、Finding 复核、JSON/MD/HTML 报告。
- 内部 Agent（OpenAI 兼容）：强类型工具面（packet/assessment/repeater/rules/history 等）、
  风险分级确认、上下文压缩、策略门禁。
- 三端同源契约：人工工作台、CLI、Agent 调用同一服务层；关键契约有入口点测试锁定。

### Stage 9 增量

- layout-ready 启动、`page_security_snapshot`、原子 `ReadCases`、Traffic 最小窗口压缩。

### 2026-08-18/21：质量与安全收口

- 审计链操作者身份（`Operator` 字段贯穿工作台/CLI/Agent/签名导出）。
- Assessment 最小窗口激活缺陷根因定位 + 验收脚本修复（UIA 裁剪 bounds 兜底点击）。
- Agent 能力强化 7 项：工具结果统一截断、调用超时、压缩保留工具证据、流式退避重试、
  usage token 会话累计、弱类型投影去重、失败消息带行动指引。
- **Stage 9 发布门禁 5/5 全量通过**（含 minimum 档视觉矩阵、Windows 打包校验）+
  Linux x64 交叉发布。证据：`G:\HackermesBuild\release-acceptance-20260821\release-evidence\`。

### 2026-08-21 第二阶段（四项功能，315 → 331 测试）

1. **超大 body SHA-256 缓存**（P1-4）：`ConditionalWeakTable` 按不可变数组引用 memoize，
   接线最热路径（审计链/草稿/提交结果）与 `packet_body_info`。
2. **multipart 参数编辑**（P1-3）：有界解析器修复跳 part 缺陷后接线三端
   （枚举/CLI `param-set multipart`/Agent schema/工作台），未编辑 part 字节级保真；
   raw 路径二进制 part 显示占位（字节级修改走 `body-edit`）。
3. **签名评估报告导出**（P2-1）：ECDSA P-256 + SPKI 内嵌 + keyId=SHA256(SPKI)，
   与审计同一把密钥；CLI `assessment report-export/report-verify`、
   Agent `assessment_report_export/verify`、ToolHost `--verify-report` 第三方离线验签。
4. **配额按工作区隔离**（P1-5）：策略文件解析到 `<workspace>/.hackermes/traffic-history-policy.json`
   （无工作区回退全局），切换热替换并立即 force 裁剪，三端 stats 标注
   `policySource=workspace|global`。

### 2026-08-22：P1 收尾 + P2 全部完成 + Agent 能力 II（331 → 397 测试）

1. **复杂 request/response edit 规则完整表单**（原 P1-2，+14）：工作台 Advanced edits 区
   加载回填/保存更新，App 层 Draft⇄Rule 映射器，二进制 body 不回显不破坏；
   门禁随即 5/5 重跑通过（350/350）。
2. **Comparer 对比快照接入 SHA-256 缓存**：`BodySha256` 下沉到 Base 层共享，
   `TrafficComparisonService.Summarize` 不再重复全量哈希。
3. **工作区切换自动刷新历史统计**：`HistoryPolicyChanged` 事件链（模块切换后触发，
   观察者读到最终状态），工作台构造即预载统计，免手点 Refresh。
4. **最小窗口第 4 个内容标签完整可见**：`RegionLayout` 纯策略 —— 侧面板宽度 =
   min(期望, 窗宽×30%, (窗宽−8−600)/2)，880 最小窗口两侧各压到 136、中央保底 600，
   四标签全部可见且可直接点击；窗口缩放时重夹取但不覆盖用户记忆值。
5. **版本号 0.8.0 → 0.9.0**：props 三处 + 打包/门禁脚本默认参数 + README 口径。
6. **Agent 大归档分批交换**（原 P2-4，+9）：`packet_archive_export` 增加 offset/limit 分页，
   信封携带 `total`，Agent 可遍历任意大的过滤结果而不再"超限即失败"；单批超 2 MiB 时
   报错附带"调小 limit 重试"指引；纯分页器 `PacketArchiveContent.Page` 与 Agent 入口
   契约均有测试锁定。导入侧维持既有 500 条 / 2 MiB 有界交换。
7. **新增只读受控 Adapter**（原 P2-3，+2）：`recon.http.get`（系统 curl 单次 GET，
   状态+响应头+有界正文，路径限绝对/无空白/≤256 字符）与 `recon.dns.resolve`
   （系统 nslookup 解析授权内精确主机名）——零新增第三方依赖，沿用精确目标范围、
   有界超时/输出与固定 argv 约束；目录即唯一缝，工作台下拉自动透出。
8. **审计密钥治理**（原 P2-2，+8）：本地信任文件（adopt → allowlist 验证模式）+
   `signing-keys adopt/rotate/revoke` CLI 治理流 + ECDSA 轮换（单次原子写入退役旧代/
   登记新代，旧私钥销毁而历史文档仍可离线验签，失败自动回滚）；撤销支持未知 keyId 黑名单
   条目。验证策略双域注入（流量审计 + 评估报告），静态离线验签路径不受影响；
   密钥治理刻意不进 Agent 工具面。
9. **Agent 工具运用能力强化 II**（+5）：系统提示注入 Tool use protocol（先读后改与确认成本、
   offset-limit-total 分页协议、失败自纠禁止原样重试、id 精确引用）；调度器对重复相同参数的
   只读调用在截断后追加纠偏提示；分页类工具 schema 参数语义描述。
10. **多用户身份提供方**（原 P2-1，+9）：`OperatorIdentityDirectory` 本地多档案 +
    `identity list/adopt/use` CLI；审计链单一缝解析活动档案，空目录回退
    `traffic.operatorName → Environment.UserName`（升级零惊扰）；名称录入即按审计身份规则
    校验。**P2 至此全部完成。**

## 三、下一步开发（按优先级）

### P0：发布卫生（下一次对外发布前必做）

1. **对当前源码重跑发布门禁**（P1/P2 增量与升版后尚未验收）：
   `scripts/invoke-release-acceptance.ps1 -RunResponsiveVisualMatrix`，要求 5/5 + 全量测试通过。
2. **出包**：Windows x64 zip + Linux x64 tar.gz + SHA256SUMS（脚本默认参数已是 0.9.0），
   归档证据目录，发布 GitHub Release 并更新 README 下载链接。

> P1/P2 功能项已全部完成；此后新功能建议在 0.9.0 出包后再排队。

### P3：Linux 转正（投入大，最后启动）

1. 真实 Linux GUI（WebKitGTK）全链路验收：捕获/拦截/Fulfill/视觉矩阵在 Linux 桌面复跑。
2. 安全工具运行环境逐项适配（Python 隔离、路径差异、DPAPI 替代实现验证）。

## 四、已知限制与技术债（非阻断，随做随清）

- multipart raw 包路径 body 为 UTF-8 字符串，二进制 part 上游已失真，仅显示占位；
  字节级编辑须走 `body-edit`（已写入 GAP-MATRIX）。
- CLI `param-set` 的 value 经空格 join 分词，值内连续空格会塌缩（Agent 路径无此问题，
  差异有测试锁定）。
- 启动后约 300ms 工作区恢复完成前，流量策略运行在全局文件（预期窗口期，来源标注可见）。
- 切换到更严格的配额策略会立即裁剪历史数据（force 语义，属预期，stats 可追溯）。
- 分析规则集为内置静态集合，尚无插件式发现机制；Finding 尚无自动跳转目标编辑页。
- 标注无批量操作；标注引用的包被清理后的自动 prune 策略未定。
