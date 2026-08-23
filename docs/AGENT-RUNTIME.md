# Agent 运行时与工作流

Hackermes 的内置 Agent 采用“模型只做计划、工具统一受策略门控制”的工作方式。权限模式、Skill、会话上下文和 CLI 共享同一套本地状态；模型不能通过提示词绕过策略。

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
