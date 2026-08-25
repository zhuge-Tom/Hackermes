using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Builds a human-readable Markdown transcript from the projected event stream —
/// what the operator saw, plus tool protocol and compaction markers. Pure function so
/// export formats are testable without UI or IO.
/// </summary>
public static class AgentTranscriptExporter
{
    public static string BuildMarkdown(
        string sessionName,
        DateTimeOffset exportedAt,
        IEnumerable<AgentSessionEvent> events)
    {
        var builder = new StringBuilder(4_096);
        builder.AppendLine($"# Hackermes 会话导出 — {sessionName}");
        builder.AppendLine();
        builder.AppendLine($"- 导出时间：{exportedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 事件数：{events.Count()}");
        builder.AppendLine();

        foreach (var @event in events)
        {
            switch (@event.Data)
            {
                case UserMessageReceived user:
                    builder.AppendLine(user.Injected
                        ? $"### ⤵ 上下文注入 · 第 {@event.Turn}/{@event.Step} 步"
                        : user.Steered
                            ? $"### ➕ 追加指示{(user.Priority ? "（优先）" : string.Empty)}"
                            : "### 🧑 操作者");
                    builder.AppendLine();
                    builder.AppendLine(user.Text);
                    builder.AppendLine();
                    break;

                case AssistantReply reply when !reply.HasToolCalls && reply.Content.Length > 0:
                    builder.AppendLine(reply.IsFinalReport ? "### ✅ 执行报告" : $"### 🤖 助手 · 第 {@event.Step} 阶段");
                    builder.AppendLine();
                    builder.AppendLine(reply.Content);
                    builder.AppendLine();
                    break;

                case ToolCallCompleted done:
                    builder.AppendLine($"#### 🔧 {done.Name} — {(done.Success ? "成功" : "失败")}");
                    builder.AppendLine("```text");
                    var content = done.Content ?? string.Empty;
                    if (content.Length > 2_000) content = content[..2_000] + "\n…[截断]";
                    builder.AppendLine(content.Replace("```", "```\\"));
                    builder.AppendLine("```");
                    builder.AppendLine();
                    break;

                case ContextCompacted compacted:
                    builder.AppendLine($"- 🗜 已压缩 {compacted.Range}，回收约 {compacted.ReclaimedChars:N0}" +
                                       $"{(compacted.Automatic ? "（自动）" : string.Empty)}");
                    break;

                case ApprovalAudited audit:
                    builder.AppendLine($"- 🛡 审批 [{audit.Record.Decision}] {audit.Record.Tool}" +
                                       $"（页面：{audit.Record.PageId ?? "-"}）");
                    break;

                case TurnEnded ended:
                    builder.AppendLine("---");
                    builder.AppendLine($"*回合结束：{ended.Reason}*");
                    builder.AppendLine();
                    break;
            }
        }
        return builder.ToString();
    }
}
