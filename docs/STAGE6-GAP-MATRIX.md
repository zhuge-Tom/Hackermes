# Stage 6 人工 / CLI / Agent 能力矩阵

> 静态审查基线：2026-08-01。矩阵描述源码中已经存在的入口，不代表本轮重新执行过构建或验收。

目标是让人工操作者、CLI 自动化和内部 Agent 使用同一套 Traffic 服务完成数据包分析与更改。符号：✅ 已有直接入口；△ 部分可用或需要绕行；— 尚无入口。

| 能力 | 人工工作台 | CLI | Agent | 当前证据 | 下一阶段缺口 |
|---|---:|---:|---:|---|---|
| 捕获、筛选、分页和查看原始请求/响应 | ✅ | ✅ | ✅ | Traffic Workbench；`packet ls/show`；`packet_list/show` | Agent `list` 结果不支持与 UI 相同的结构化复合筛选/分页 |
| 协议异常、敏感字段分析 | ✅ | ✅ | ✅ | 三端复用 `HttpPacketAnalyzer` 的结构化 Finding；包含 side、稳定 code、Header 重复项和 UTF-8 body offset；UI 可精确选中 Header/StartLine 或定位 Binary editor | 规则集仍为内置静态集合，尚无插件发现机制 |
| 语义 Diff | ✅ | ✅ | ✅ | Comparer 支持 Traffic/Repeater 来源直填与持久 Session CRUD；`packet diff` / `compare` / `compare-session`；`packet_diff` / `packet_compare_structured` / `comparison_session_*` | Repeater 来源仍需显式刷新后选择；后续可增加从其他工作台一键发送到左右槽位 |
| 请求拦截、继续、丢弃 | ✅ | ✅ | ✅ | Request Intercept；`packet intercept/continue/drop`；对应 Agent 工具 | 已具备基本对等性 |
| 响应拦截与 Fulfill | ✅ | ✅ | ✅ | UI 有独立 Response Intercept；CLI `packet intercept-mode request|response|both|off`；Agent `packet_intercept_mode`；均复用同一捕获服务 | `packet intercept on|off` 保留为仅控制请求拦截的兼容入口 |
| 原始 HTTP 文本编辑并应用 | ✅ | ✅ | ✅ | Request/Response editor；`packet edit`；`packet_edit` | 需补编辑前后 Content-Length/Transfer-Encoding 一致性提示 |
| 结构化参数分析与修改 | ✅ | ✅ | ✅ | Workbench `Parameters` 页读取/修改 query、form 和顶层 JSON；`packet param-list/param-set`；`packet_parameters/packet_parameter_set`（只读结果遮蔽敏感值，修改为 Dangerous） | UI 修改先应用到文本编辑器、CLI/Agent `param-set` 直接提交 held packet，交互语义不同；尚不支持嵌套 JSON、multipart、Cookie 和 Header 参数 |
| 大 body 元数据与范围读取 | ✅ | ✅ | ✅ | Binary editor 固定显示长度/SHA-256/类型/字符集，支持 64 KiB 前后/跳转、实际范围和进度；`body-info/body-read`；`packet_body_info/chunk` | 超大 body 的完整 SHA-256 仍为 O(n)，尚无增量缓存 |
| Hex/Base64 Replace/Insert/Delete | ✅ | ✅ | ✅ | `IPacketEditDraftService` 保存首次快照、前后长度/SHA-256/Content-Length和最近失败；Binary editor 可 Refresh/Discard；CLI `draft-list/show/discard`；Agent `packet_edit_drafts/draft/discard`；成功与失败提交进入元数据审计 | 仍需真实 echo 对修改后字节、响应 Fulfill 与 CDP 失败重试执行验收 |
| 请求重放 | ✅ | ✅ | ✅ | Replay；`packet replay`；`packet_replay` | 已具备基本对等性；后续应支持显式超时和取消结果 |
| Repeater 草稿与多轮历史 | ✅ | ✅ | ✅ | Repeater Workbench 可选择任意持久 send-result，以稳定 `DraftId + ResultId` 比较并保存持久 Comparison Session；`repeater ls/create/send/rename/delete/clear`；Agent `repeater_list/create/send/rename/delete/clear_history` | 可继续增加跨工作台拖放来源 |
| 持久拦截规则 | ✅ | ✅ | ✅ | Rules Workbench 支持路径型 JSON export 与 replace/merge import；`rule ...`；`traffic_rule_list/change`；UI 适配层直接调用 `ITrafficRuleManager.ExportJson/ImportJson` | UI 仍缺系统文件选择器；复杂 request/response edit 规则没有完整表单 |
| 分析标注与复核状态 | ✅ | ✅ | ✅ | Workbench `Annotation` 页维护 starred、tags、note、review status；`annotation list/show/set/delete/prune`；`packet_annotation_get/list/set/delete/prune`；`TrafficAnnotationService` 版本化 JSON 原子持久化 | UI 缺删除、按标签/状态筛选和批量标注；标注引用的包被清理后需明确自动 prune 策略 |
| HAR / Hookmes JSON 导入导出 | ✅ | ✅ | ✅ | Traffic Workbench `Archive`；`packet export/import`；Agent `packet_archive_export/import` 只收发内容、不接受路径，复用 `PacketArchiveCodec/IPacketArchiveService`，限制 500 entries / 2 MiB；批量导出为 Dangerous、导入为 Mutating | UI 仍缺系统文件选择器和覆盖确认；Agent 大归档需先过滤、分批交换，且导出内容可能包含 body secrets |
| 历史、Repeater、Comparer 跨重启 | ✅ | ✅ | ✅ | 共用版本化持久化服务；历史支持全局及精确主机/`*.domain` 条数容量配额、保留期、自动清理、统计与显式清空，三端共享策略 | 尚无按工作区隔离的配额配置 |
| 操作安全与审计 | ✅ | ✅ | ✅ | 修改、Discard、继续、丢弃、Fulfill、重放和规则 Matched/Succeeded/Failed/Skipped 均进入元数据审计；规则路径只存 SHA-256，不保存 query/header value/body | 尚无撤销操作和审计导出签名 |

## 下一阶段优先级

### P0：固化“更改包”的一致提交语义

编辑草稿已升级为 `IPacketEditDraftService` 公共契约：首次编辑保存原 body/header 快照，三端可查看 pending 的前后长度、SHA-256、Content-Length和最近提交失败，也可 Discard 精确恢复原状态。Request Continue 携带草稿 body/headers，Response Continue 转为 Fulfill；只有 CDP 成功后才清草稿，失败保留并累计 attempts。

剩余 P0 工作以执行证据和审计为主：

1. 为 apply-and-continue / apply-and-fulfill 返回最终 TrafficState 和可持久追踪的操作结果。
2. 在 loopback 与真实 CDP 验收中断言服务端/浏览器实际收到的字节。
3. 为审计提供签名导出，并让提交结果直接携带可追踪 audit id。

验收：同一个暂停请求分别从 UI、CLI、Agent 修改二进制 body 后继续，本地 echo 服务收到完全相同的新字节；响应 body 修改走 Fulfill 并返回新字节；模拟 CDP 失败后暂存标记仍存在且可重试。

### P1：补齐三端功能对等

- CLI/Agent 独立 request/response/both/off 拦截模式已有源码入口，等待统一构建与真实 CDP 验收。
- UI：为现有 HAR/Hookmes JSON 与规则导入导出路径栏补系统文件选择器、覆盖确认和最近路径。
- UI：为归档与规则路径栏补系统文件选择器、覆盖确认和最近路径。

验收：每项公共服务能力至少有一个人工入口、一个 CLI 命令和一个具备正确风险等级的 Agent 工具；契约测试验证三者调用相同服务结果，而不是复制逻辑。

### P2：可审计性与大数据体验

- 为审计提供签名导出及操作者身份扩展。
- 缓存超大 body 摘要，并增加按工作区隔离的历史配额。
- 为 Finding 增加自动切换目标编辑页和插件式分析器发现。

## 完成门槛

Stage 6 只有在以下证据同时存在时才可标记完成：

1. 单元/契约测试覆盖文本与二进制、请求与响应、成功与回滚、跨重启恢复。
2. loopback HTTP 验收覆盖真实 App 的捕获、请求拦截修改、响应拦截 Fulfill 和重放，并断言服务端实际收到的字节。
3. 人工、CLI、Agent 对等矩阵不再存在 P0/P1 的 `△` 或 `—`。
4. README 命令、Agent 工具名、UI 标签与源码注册保持一致。
