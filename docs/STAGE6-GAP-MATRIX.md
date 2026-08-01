# Stage 6 人工 / CLI / Agent 能力矩阵

> 静态审查基线：2026-08-01。矩阵描述源码中已经存在的入口，不代表本轮重新执行过构建或验收。

目标是让人工操作者、CLI 自动化和内部 Agent 使用同一套 Traffic 服务完成数据包分析与更改。符号：✅ 已有直接入口；△ 部分可用或需要绕行；— 尚无入口。

| 能力 | 人工工作台 | CLI | Agent | 当前证据 | 下一阶段缺口 |
|---|---:|---:|---:|---|---|
| 捕获、筛选、分页和查看原始请求/响应 | ✅ | ✅ | ✅ | Traffic Workbench；CLI `packet query/show`；Agent `packet_query/show`；共享有界查询覆盖文本、方法、状态、资源类型、暂停状态及 offset/limit | 仍需真实大历史数据验证排序稳定性与翻页期间新增数据的游标语义 |
| 协议异常、敏感字段分析 | ✅ | ✅ | ✅ | 三端复用 `HttpPacketAnalyzer` 的结构化 Finding；包含 side、稳定 code、Header 重复项和 UTF-8 body offset；UI 可精确选中 Header/StartLine 或定位 Binary editor | 规则集仍为内置静态集合，尚无插件发现机制 |
| 语义 Diff | ✅ | ✅ | ✅ | Comparer 支持 Traffic/Repeater 来源直填与持久 Session CRUD；`packet diff` / `compare` / `compare-session`；`packet_diff` / `packet_compare_structured` / `comparison_session_*` | Repeater 来源仍需显式刷新后选择；后续可增加从其他工作台一键发送到左右槽位 |
| 请求拦截、继续、丢弃 | ✅ | ✅ | ✅ | `IPacketCommitService` 返回统一最终状态、前后摘要、audit id/error code；UI 摘要、CLI `key=value`、Agent JSON | 已具备源码对等性，等待真实 CDP 验收 |
| 响应拦截与 Fulfill | ✅ | ✅ | ✅ | UI 有独立 Response Intercept；CLI/Agent 四态拦截；响应 Edit 通过统一提交结果报告 Fulfilled 与 audit id | `packet intercept on|off` 保留为仅控制请求拦截的兼容入口 |
| 原始 HTTP 文本编辑并应用 | ✅ | ✅ | ✅ | Request/Response editor、`packet edit`、`packet_edit` 共用 `IPacketCommitService`；失败仍返回结构化结果 | 需补编辑前后 Content-Length/Transfer-Encoding 一致性提示 |
| 结构化参数分析与修改 | ✅ | ✅ | ✅ | Workbench `Parameters`、CLI `packet param-*`、Agent `packet_parameter*` 共享 query/form/顶层 JSON/重复 Header/Cookie occurrence 契约；修改有界并防 Header 注入，Agent 遮蔽 Cookie/认证值 | UI 修改先应用到文本编辑器、CLI/Agent 直接提交 held packet，交互语义不同；尚不支持嵌套 JSON 与 multipart |
| 大 body 元数据与范围读取 | ✅ | ✅ | ✅ | Binary editor 固定显示长度/SHA-256/类型/字符集，支持 64 KiB 前后/跳转、实际范围和进度；`body-info/body-read`；`packet_body_info/chunk` | 超大 body 的完整 SHA-256 仍为 O(n)，尚无增量缓存 |
| Hex/Base64 Replace/Insert/Delete | ✅ | ✅ | ✅ | `IPacketEditDraftService` 保存首次快照、前后长度/SHA-256/Content-Length和最近失败；Binary editor 可 Refresh/Discard；CLI `draft-list/show/discard`；Agent `packet_edit_drafts/draft/discard`；成功与失败提交进入元数据审计 | 仍需真实 echo 对修改后字节、响应 Fulfill 与 CDP 失败重试执行验收 |
| 请求重放 | ✅ | ✅ | ✅ | Replay；`packet replay`；`packet_replay` | 已具备基本对等性；后续应支持显式超时和取消结果 |
| Repeater 草稿与多轮历史 | ✅ | ✅ | ✅ | Repeater Workbench 可选择任意持久 send-result，以稳定 `DraftId + ResultId` 比较并保存持久 Comparison Session；`repeater ls/create/send/rename/delete/clear`；Agent `repeater_list/create/send/rename/delete/clear_history` | 可继续增加跨工作台拖放来源 |
| 持久拦截规则 | ✅ | ✅ | ✅ | Rules Workbench 使用系统 JSON picker、覆盖/replace 确认和最近路径；`rule ...`；`traffic_rule_list/change` | 复杂 request/response edit 规则没有完整表单 |
| 分析标注与复核状态 | ✅ | ✅ | ✅ | Workbench `Annotation` 页维护 starred、tags、note、review status；`annotation list/show/set/delete/prune`；`packet_annotation_get/list/set/delete/prune`；`TrafficAnnotationService` 版本化 JSON 原子持久化 | UI 缺删除、按标签/状态筛选和批量标注；标注引用的包被清理后需明确自动 prune 策略 |
| HAR / Hookmes JSON 导入导出 | ✅ | ✅ | ✅ | Traffic Workbench 使用系统 HAR/JSON picker、覆盖确认和最近路径；`packet export/import`；Agent 只交换有限内容、不接受路径 | Agent 大归档仍需先过滤、分批交换，且导出内容可能包含 body secrets |
| 历史、Repeater、Comparer 跨重启 | ✅ | ✅ | ✅ | 共用版本化持久化服务；历史支持全局及精确主机/`*.domain` 条数容量配额、保留期、自动清理、统计与显式清空，三端共享策略 | 尚无按工作区隔离的配额配置 |
| 操作安全与审计 | ✅ | ✅ | ✅ | 修改、Discard、继续、丢弃、Fulfill、重放和规则执行均进入元数据审计；三端共享 ECDSA P-256 签名导出/离线验签及可选指纹信任固定，不保存原始包 | 尚无撤销、操作者身份、密钥轮换及可信指纹分发机制 |

## 下一阶段优先级

### P0：固化“更改包”的一致提交语义

编辑草稿已升级为 `IPacketEditDraftService` 公共契约：首次编辑保存原 body/header 快照，三端可查看 pending 的前后长度、SHA-256、Content-Length和最近提交失败，也可 Discard 精确恢复原状态。Request Continue 携带草稿 body/headers，Response Continue 转为 Fulfill；只有 CDP 成功后才清草稿，失败保留并累计 attempts。

剩余 P0 工作以执行证据和审计为主：

1. 在 loopback 与真实 CDP 验收中断言服务端/浏览器实际收到的字节。
2. 在真实平台密钥存储上验收签名密钥跨重启复用、损坏恢复和可信指纹固定。

验收：同一个暂停请求分别从 UI、CLI、Agent 修改二进制 body 后继续，本地 echo 服务收到完全相同的新字节；响应 body 修改走 Fulfill 并返回新字节；模拟 CDP 失败后暂存标记仍存在且可重试。

### P1：补齐三端功能对等

- CLI/Agent 独立 request/response/both/off 拦截模式已有源码入口，等待统一构建与真实 CDP 验收。
- 人工、CLI、Agent 的 P1 公共能力已具备源码入口，后续以真实桌面/CDP 验收为主。

验收：每项公共服务能力至少有一个人工入口、一个 CLI 命令和一个具备正确风险等级的 Agent 工具；契约测试验证三者调用相同服务结果，而不是复制逻辑。

### P2：可审计性与大数据体验

- 为审计补充操作者身份、密钥轮换和可信指纹分发；签名导出源码入口已覆盖三端。
- 缓存超大 body 摘要，并增加按工作区隔离的历史配额。
- 为 Finding 增加自动切换目标编辑页和插件式分析器发现。

## 完成门槛

Stage 6 只有在以下证据同时存在时才可标记完成：

1. 单元/契约测试覆盖文本与二进制、请求与响应、成功与回滚、跨重启恢复。
2. loopback HTTP 验收覆盖真实 App 的捕获、请求拦截修改、响应拦截 Fulfill 和重放，并断言服务端实际收到的字节。
3. 人工、CLI、Agent 对等矩阵不再存在 P0/P1 的 `△` 或 `—`。
4. README 命令、Agent 工具名、UI 标签与源码注册保持一致。
