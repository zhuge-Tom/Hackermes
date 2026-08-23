using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Compaction must preserve attributable tool evidence (which tool produced which
/// bounded digest) instead of discarding every tool message wholesale.
/// </summary>
public sealed class AgentContextCompactorEvidenceTests
{
    [Fact]
    public void Compaction_keeps_named_tool_evidence_and_call_intent()
    {
        var settings = new AiSettings { MaxRecentMessages = 4 };
        var history = new List<ChatMessage>
        {
            new("user", "调查 packet-42 的来源。"),
            new("assistant", null, ToolCalls: [new AssistantToolCall("c1", "packet_query", "{}")]),
            new("tool", Evidence(400, "TAIL-ONLY-IN-ORIGINAL"), ToolCallId: "c1"),
            new("assistant", "初步结论：来自授权目标自身。"),
            new("user", "第二个问题"),
            new("assistant", "回答二"),
            new("user", "第三个问题"),
            new("assistant", "回答三"),
        };

        var summary = new AgentContextCompactor().CompactCompletedTurns(history, string.Empty, settings);

        Assert.Contains("user: 调查 packet-42 的来源。", summary, StringComparison.Ordinal);
        Assert.Contains("assistant: 调用工具 packet_query", summary, StringComparison.Ordinal);
        Assert.Contains("工具 packet_query 结果:", summary, StringComparison.Ordinal);
        // The digest is bounded; content past the digest budget must not leak into the summary.
        Assert.DoesNotContain("TAIL-ONLY-IN-ORIGINAL", summary, StringComparison.Ordinal);
        Assert.Equal("第二个问题", history[0].Content);
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public void Compaction_caps_tool_digest_lines_with_omission_note()
    {
        var settings = new AiSettings { MaxRecentMessages = 4 };
        var history = new List<ChatMessage>();
        for (var turn = 0; turn < 60; turn++)
        {
            history.Add(new ChatMessage("user", $"问题-{turn:D3}"));
            history.Add(new ChatMessage("assistant", null,
                ToolCalls: [new AssistantToolCall($"c{turn}", "packet_show", "{}")]));
            history.Add(new ChatMessage("tool", $"evidence-{turn:D3}", ToolCallId: $"c{turn}"));
        }
        history.Add(new ChatMessage("assistant", "回答"));
        history.Add(new ChatMessage("user", "收尾问题"));

        var summary = new AgentContextCompactor().CompactCompletedTurns(history, string.Empty, settings);

        // keepFrom keeps the last two user turns (问题-059 + 收尾), so 59 tool digests
        // were compacted and the oldest 11 collapse into one omission note.
        Assert.Contains("另有 11 条更早的工具结果已省略", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence-000", summary, StringComparison.Ordinal);
        Assert.Contains("evidence-057", summary, StringComparison.Ordinal);
    }

    private static string Evidence(int length, string tail)
    {
        var body = new string('e', Math.Max(0, length - tail.Length - 1));
        return body + " " + tail;
    }
}
