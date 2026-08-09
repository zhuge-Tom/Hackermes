# Hackermes 开发基线

## 当前范围

- 阶段 0–1：应用骨架、Dock、浏览器和 CDP 通道已落地。
- 阶段 2：Page Agent、Network、Console 已接通；DOM 已改为树形结构并补齐页面拾取器、页面/树双向悬停与点击定位、父级展开、树项滚动、计算样式/匹配规则编辑和导航后陈旧节点清理。页面资源不再占用左侧栏，相关 src/href 在 DOM 详情中查看。
- 阶段 3：动作描述、执行器、选择器、录制、保存、加载、回放、领域 REPL 和 PTY 已接通。此次复核确认模块已注册，不是空壳。
- 阶段 4–5：AI 工具策略、MCP 桥、数据包工作台、CLI 与 Agent 共用的数据包服务保留。
- 阶段 6：基础功能与运行验收均已完成。Traffic 捕获启动已串行化，避免同一页面重复注册 `Fetch.requestPaused` 后对同一 requestId 重复 Continue/GetBody；Continue/Fulfill 只发送实际设置的 CDP 可选字段。请求/响应二进制 body 编辑、草稿回滚、独立拦截模式、规则、审计、归档、历史、Comparer 和三端（工作台 / CLI / Agent）入口均已落地。Repeater 已支持 0.1–600 秒超时和取消并持久化结果；Annotation 已支持精确标签/复核状态筛选、清除筛选与删除；Windows 当前用户 DPAPI 密钥复用、损坏恢复、指纹固定及轮换拒绝均有定向验收用例。真实 WebView2/CDP loopback 已连续两次通过捕获、重放、暂停继续、请求二进制改写和响应 Fulfill 的 5 个闭环；默认 DPAPI 审计密钥指纹在两个独立桌面进程间保持一致。完整测试集 201/201 通过。

- 阶段 7：授权评估控制面基础已落地。AI 设置提供“请求批准（默认）/帮我批准/完全访问权限”三档统一策略；Skill 工作流、压缩上下文、持久记忆、受控 HTTPS 工具缓存与人工 CLI 管理入口已接入。`assessment` CLI 与 Agent 工具共享持久化的目标范围、计划、审批、任务取消、范围撤销、证据、Finding 与审计记录。嵌套 JSON Pointer（深度/条目上限）和最多 500 条的批量标注也已加入底层服务，其中批量标注可由 CLI/Agent 调用。完整方案见 [`STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md`](STAGE7-AUTHORIZED-ASSESSMENT-PLAN.md)，使用说明见 [`AGENT-RUNTIME.md`](AGENT-RUNTIME.md)。

- 阶段 7 ToolHost 基线：已新增独立 `Hackermes.ToolHost` 进程、DPAPI 保护的 HMAC 任务票据、短时效与一次性 nonce、防重放记录、精确目标范围、不可变计划哈希、一次性批准、批准/范围撤销、进程树取消、硬超时、输出上限、证据和审计。Agent 侧注册 `recon.nmap.quick`、`recon.nmap.service`、`recon.dirsearch.quick` 和 `recon.wafw00f.quick` 四个固定参数 Adapter；未接入口令爆破、漏洞利用、规避或破坏工具。工具与便携 Python 从应用相对 `tools` 目录解析，Nmap、Dirsearch、Wafw00f 均已在 `127.0.0.1` loopback 靶场完成真实 ToolHost 调用；完整测试集 220/220 通过。

## 明确延期

已实现有界嵌套 JSON Pointer 的读取与修改：对象/数组定位遵循 RFC 6901 转义规则，默认深度上限为 32、条目上限为 2000，调用方可在更严格的 1–64 层与 1–10000 条范围内收紧限制。现有顶层 JSON、query、form、header 和 cookie 能力保持不变。

## 命名

源码目录、项目文件、程序集、命名空间、配置键和文档品牌已统一迁移为 `Hackermes`。当前磁盘工作区根目录仍沿用会话创建时的旧路径，它不属于产品命名或仓库内容。

## 已执行的运行验收

已完成真实 CDP loopback 5 项闭环两次、默认密钥文件跨重启指纹验证，以及 Repeater 超时/取消、Annotation 筛选/删除的工作台命令定向测试（22/22）。复杂能力需单独排期。
