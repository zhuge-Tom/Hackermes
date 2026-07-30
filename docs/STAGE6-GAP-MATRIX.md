# Stage 6 人工 / CLI / Agent 能力矩阵

> 静态审查基线：2026-07-30。矩阵描述源码中已经存在的入口，不代表本轮重新执行过构建或验收。

目标是让人工操作者、CLI 自动化和内部 Agent 使用同一套 Traffic 服务完成数据包分析与更改。符号：✅ 已有直接入口；△ 部分可用或需要绕行；— 尚无入口。

| 能力 | 人工工作台 | CLI | Agent | 当前证据 | 下一阶段缺口 |
|---|---:|---:|---:|---|---|
| 捕获、筛选、分页和查看原始请求/响应 | ✅ | ✅ | ✅ | Traffic Workbench；`packet ls/show`；`packet_list/show` | Agent `list` 结果不支持与 UI 相同的结构化复合筛选/分页 |
| 协议异常、敏感字段分析 | ✅ | ✅ | ✅ | Analyze；`packet analyze`；`packet_analyze` | 规则集固定，缺少可扩展分析器与发现项定位到编辑器 |
| 语义 Diff | ✅ | ✅ | ✅ | Comparer；`packet diff` / `compare`；`packet_diff` / `packet_compare_structured` | UI 需手输 packet id；持久 Comparison Session 尚无 UI/CLI/Agent CRUD |
| 请求拦截、继续、丢弃 | ✅ | ✅ | ✅ | Request Intercept；`packet intercept/continue/drop`；对应 Agent 工具 | 已具备基本对等性 |
| 响应拦截与 Fulfill | ✅ | △ | △ | UI 有独立 Response Intercept 与 Fulfill；CLI/Agent 只有通用 `edit response` | CLI/Agent 无独立响应拦截开关，无法清晰表达 request/response/both 模式 |
| 原始 HTTP 文本编辑并应用 | ✅ | ✅ | ✅ | Request/Response editor；`packet edit`；`packet_edit` | 需补编辑前后 Content-Length/Transfer-Encoding 一致性提示 |
| 结构化参数分析与修改 | ✅ | ✅ | ✅ | Workbench `Parameters` 页读取/修改 query、form 和顶层 JSON；`packet param-list/param-set`；`packet_parameters/packet_parameter_set`（只读结果遮蔽敏感值，修改为 Dangerous） | UI 修改先应用到文本编辑器、CLI/Agent `param-set` 直接提交 held packet，交互语义不同；尚不支持嵌套 JSON、multipart、Cookie 和 Header 参数 |
| 大 body 元数据与范围读取 | ✅ | ✅ | ✅ | Binary editor；`body-info/body-read`；`packet_body_info/chunk` | UI 缺少长度/SHA-256 固定摘要、分块导航与 offset 进度视图 |
| Hex/Base64 Replace/Insert/Delete | ✅ | ✅ | ✅ | Binary editor；`packet body-edit`；`packet_body_edit`；`TrafficIntegrationService` 以 `_binaryEdited` 跟踪暂存修改，Continue 对请求提交 `TrafficRequestEdit`、对响应提交 `Fulfill`，成功后才清标记 | 三端已有实际提交路径；仍缺显式的暂存状态/放弃修改 UI，以及真实 echo 对修改后字节的回归断言 |
| 请求重放 | ✅ | ✅ | ✅ | Replay；`packet replay`；`packet_replay` | 已具备基本对等性；后续应支持显式超时和取消结果 |
| Repeater 草稿与多轮历史 | ✅ | ✅ | △ | Repeater Workbench；`repeater ls/create/send/rename/delete/clear`；Agent list/create/send/delete | Agent 缺 rename、clear-history；UI 只显示最新响应，不能逐轮选择和比较 |
| 持久拦截规则 | ✅ | ✅ | ✅ | Rules Workbench；`rule ...`；`traffic_rule_list/change` | UI 缺规则 JSON import/export；复杂 request/response edit 规则没有完整表单 |
| 分析标注与复核状态 | ✅ | ✅ | ✅ | Workbench `Annotation` 页维护 starred、tags、note、review status；`annotation list/show/set/delete/prune`；`packet_annotation_get/set/delete`；`TrafficAnnotationService` 版本化 JSON 原子持久化 | Agent 缺 list/query/prune，UI 缺删除、按标签/状态筛选和批量标注；标注引用的包被清理后需明确自动 prune 策略 |
| HAR / Hookmes JSON 导入导出 | — | ✅ | — | `packet export/import` | 人工没有文件入口；Agent 没有受限路径/附件式归档工具 |
| 历史、Repeater、Comparer 跨重启 | ✅ | ✅ | ✅ | 共用 Traffic Store 和版本化持久化服务 | 缺保留期、容量、清理和存储占用管理入口 |
| 操作安全与审计 | △ | △ | ✅ | Agent 风险分级；CLI 根命令标记 mutating；持久化原子替换/备份 | 人工/CLI 缺统一的操作审计记录、撤销和修改前后 hash |

## 下一阶段优先级

### P0：固化“更改包”的一致提交语义

当前源码已经避免“只改 Store、Continue 发送旧 body”：二进制修改由 `_binaryEdited` 标记，Request Continue 携带更新后的 body/headers，Response Continue 转为 Fulfill；标记仅在 CDP 操作成功后清除。UI 的文本 Continue/Replay/Fulfill 路径也会优先合并暂存的二进制 body。

下一步不是重新实现提交，而是把已存在的语义固化为公共契约：

1. 将 `_binaryEdited` 的隐式标记升级为可查询的编辑草稿状态，允许三端显示 pending/committed/failed。
2. 补 `discard-draft`；将现有 Continue/Fulfill 明确命名或返回为 `apply-and-continue` / `apply-and-fulfill` 结果。
3. 操作返回旧/新长度、SHA-256、Content-Length 变化和最终 TrafficState；失败时保留暂存标记以便重试。

验收：同一个暂停请求分别从 UI、CLI、Agent 修改二进制 body 后继续，本地 echo 服务收到完全相同的新字节；响应 body 修改走 Fulfill 并返回新字节；模拟 CDP 失败后暂存标记仍存在且可重试。

### P1：补齐三端功能对等

- CLI/Agent：独立选择 request、response 或 both 拦截模式。
- UI：HAR/Hookmes JSON 导入导出、规则导入导出。
- Agent：Repeater rename/clear-history。
- 三端：持久 Comparison Session 的 list/create/rename/recalculate/delete，并能从 Traffic/Repeater 当前选择直接填充左右来源。

验收：每项公共服务能力至少有一个人工入口、一个 CLI 命令和一个具备正确风险等级的 Agent 工具；契约测试验证三者调用相同服务结果，而不是复制逻辑。

### P2：可审计性与大数据体验

- 为修改、丢弃、Fulfill、规则命中和重放记录操作者、入口、时间、前后 hash 与结果。
- 提供历史容量/保留期/清理策略和磁盘占用视图。
- UI 增加 body 分块导航、十六进制 offset、当前范围和摘要；分析发现可跳转到对应 Header/body 位置。

## 完成门槛

Stage 6 只有在以下证据同时存在时才可标记完成：

1. 单元/契约测试覆盖文本与二进制、请求与响应、成功与回滚、跨重启恢复。
2. loopback HTTP 验收覆盖真实 App 的捕获、请求拦截修改、响应拦截 Fulfill 和重放，并断言服务端实际收到的字节。
3. 人工、CLI、Agent 对等矩阵不再存在 P0/P1 的 `△` 或 `—`。
4. README 命令、Agent 工具名、UI 标签与源码注册保持一致。
