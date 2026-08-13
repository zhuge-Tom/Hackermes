using Hackermes.AiPanel.OpenAI;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hackermes.AiPanel.Agent;

/// <summary>
/// Keeps the prompt bounded without discarding the thread's working state. Older turns are
/// reduced to a deterministic, persisted digest; only complete recent turns remain verbatim.
/// </summary>
public sealed class AgentContextCompactor
{
    public IReadOnlyList<ChatMessage> BuildRequest(
        IReadOnlyList<ChatMessage> history,
        AgentMemoryDocument memory,
        IReadOnlyList<AgentSkill> skills,
        AiSettings settings)
    {
        var system = BuildSystemMessage(memory, skills, settings);
        var available = Math.Max(1_000, settings.MaxContextCharacters - system.Length);
        var starts = history.Select((message, index) => (message, index)).Where(pair => pair.message.Role == "user").Select(pair => pair.index).ToArray();
        if (starts.Length == 0) return [new ChatMessage("system", system)];

        // Keep whole interaction turns. Cutting in front of a tool result produces an invalid
        // OpenAI tool transcript, so the newest turn wins over an overly aggressive truncation.
        var keepFrom = starts[^1];
        var used = history.Skip(keepFrom).Sum(Estimate);
        for (var index = starts.Length - 2; index >= 0; index--)
        {
            var candidateCost = history.Skip(starts[index]).Take(keepFrom - starts[index]).Sum(Estimate);
            if (used + candidateCost > available) break;
            used += candidateCost;
            keepFrom = starts[index];
        }

        var retained = history.Skip(keepFrom).ToList();
        retained.Insert(0, new ChatMessage("system", system));
        return retained;
    }

    public string CompactCompletedTurns(List<ChatMessage> history, string existingSummary, AiSettings settings)
    {
        var keepFrom = FindKeepFrom(history, settings.MaxRecentMessages);
        if (keepFrom == 0) return Limit(existingSummary, 20_000);

        var compacted = history.Take(keepFrom)
            .Where(message => message.Role is "user" or "assistant")
            .Where(message => message.ToolCalls is null || message.ToolCalls.Count == 0)
            .Select(message => $"{message.Role}: {Shorten(message.Content, 900)}")
            .Where(line => line.Length > 0)
            .ToArray();

        history.RemoveRange(0, keepFrom);
        if (compacted.Length == 0) return Limit(existingSummary, 20_000);
        var joined = string.IsNullOrWhiteSpace(existingSummary)
            ? string.Join('\n', compacted)
            : existingSummary + '\n' + string.Join('\n', compacted);
        return Limit(joined, 20_000);
    }

    private static int FindKeepFrom(IReadOnlyList<ChatMessage> history, int maxRecentMessages)
    {
        if (history.Count <= maxRecentMessages) return 0;

        // A user message marks the beginning of a completed interaction turn. Keeping the
        // last two turns avoids emitting orphaned tool results after a compaction boundary.
        var userStarts = new List<int>();
        for (var index = 0; index < history.Count; index++)
            if (history[index].Role == "user") userStarts.Add(index);
        if (userStarts.Count <= 2) return 0;
        return userStarts[^2];
    }

    private static string BuildSystemMessage(AgentMemoryDocument memory, IReadOnlyList<AgentSkill> skills, AiSettings settings)
    {
        var builder = new StringBuilder(
            "You are the Hackermes desktop assistant. Follow enabled workflows, keep actions bounded, " +
            "and never claim a tool ran unless its result confirms it. Tool policy and user approvals are authoritative.");
        builder.Append(" Context is compacted and persistent memory is redacted; do not request or store credentials in memory.");
        builder.Append(
            " When helping with an authorized website assessment in the embedded browser, first use page_context, then " +
            "page_security_snapshot for that exact page. Use its bounded findings to choose only the necessary read-only " +
            "console/network/DOM or packet/page tools; do not repeatedly collect the same snapshot or request, echo, or infer " +
            "cookie, token, form-field, storage, request-body, or script-source values. Never invent or substitute a target. " +
            "Create browser-bound " +
            "scope only with assessment_create_scope_from_page, then use the shared scope -> fixed plan -> one-time approval -> " +
            "bounded ToolHost run -> evidence/findings/report workflow. Do not bypass confirmation, scope, plan hashing, expiry, " +
            "or human review, and do not claim a vulnerability without tool evidence. Never request arbitrary shell access or " +
            "execute uncatalogued commands. If no page is attached or authorization details are missing, explain what the operator " +
            "must provide instead of selecting another page or target.");

        if (!string.IsNullOrWhiteSpace(memory.Notes))
            builder.Append("\nOperator memory:\n").Append(Limit(memory.Notes, 8_000));
        if (!string.IsNullOrWhiteSpace(memory.Summary))
            builder.Append("\nCompressed conversation context:\n").Append(Limit(memory.Summary, 12_000));

        foreach (var skill in skills.Where(skill => skill.Enabled))
        {
            builder.Append("\nWorkflow [").Append(skill.Name).Append("]:\n")
                .Append(Limit(skill.Instructions, 4_000));
            if (skill.ToolNames.Count > 0)
                builder.Append("\nWorkflow tools: ").Append(string.Join(", ", skill.ToolNames));
        }

        return Limit(builder.ToString(), Math.Min(20_000, settings.MaxContextCharacters / 2));
    }

    private static int Estimate(ChatMessage message) => (message.Content?.Length ?? 0) + 800;
    private static string Shorten(string? content, int max) => Limit(content, max).Replace('\n', ' ');
    private static string Limit(string? value, int max) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, max)];
}
