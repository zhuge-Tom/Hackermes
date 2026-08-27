using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Agent;

/// <summary>One message (or compressed-block marker) tracked with an ACP reference id.</summary>
public sealed class AcpEntry
{
    public string Ref { get; init; } = string.Empty;
    public ChatMessage Message { get; set; } = new("user", string.Empty);
    public int Chars { get; set; }
    /// <summary>Tool name for role=="tool" entries; enables load-bearing detection.</summary>
    public string? ToolName { get; set; }
    /// <summary>Set when this entry is the visible marker of a compression block.</summary>
    public string? BlockId { get; set; }
}

/// <summary>A compressed range: original messages are parked here, a marker stays in context.</summary>
public sealed class AcpBlock
{
    public string Id { get; init; } = string.Empty;
    public int Tier { get; set; } = 1;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public long OriginalChars { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Active { get; set; } = true;
    public bool IsTombstone { get; set; }
    public List<AcpEntry> OriginalEntries { get; set; } = [];
}

/// <summary>
/// Active Context Pruning store (opencode-acp / pai-acp lineage): the model decides
/// when and what to compress. Messages carry stable refs ([m00012]) used as compression
/// boundaries; compressed ranges become labeled blocks that can be decompressed or searched.
/// Protections: recent working set, last user message, and context_compress tool results
/// (their summaries are the sole record of compressed conversation). Ranges never split an
/// assistant tool_call from its results. A GC fallback truncates the oldest content when the
/// budget is still exceeded, parking victims into a searchable tombstone block.
/// </summary>
public sealed class AcpContextStore
{
    public const double MinNudgeRatio = 0.45;
    public const double MaxNudgeRatio = 0.55;
    public const string LoadBearingTool = "context_compress";
    private const int ProtectedRecentEntries = 4;
    private const int NudgeEveryBuilds = 3;
    private const int MaxSuggestions = 5;
    private const int MinCandidateChars = 400;
    private const int TombstoneDigestCap = 2_000;

    private readonly Func<string> _systemMessageFactory;
    private readonly List<AcpEntry> _active = [];
    private readonly List<AcpBlock> _blocks = [];
    private readonly object _gate = new();
    /// <summary>Unit provider: raw chars (+overhead) by default, estimated tokens when token budgeting is on.</summary>
    private readonly Func<string, int> _estimate;
    private int _nextRef;
    private int _buildCount;
    private bool _gcRan;
    private int _budgetChars;

    /// <param name="initialBudget">Budget used for protection zones before the first BuildRequest, in the active unit.</param>
    /// <param name="estimate">Optional unit override; when provided (token metering) entry sizes and budgets are tokens.</param>
    public AcpContextStore(Func<string> systemMessageFactory, int initialBudget = 120_000, Func<string, int>? estimate = null)
    {
        _systemMessageFactory = systemMessageFactory;
        _estimate = estimate ?? (content => (content?.Length ?? 0) + LegacyEntryOverheadChars);
        _budgetChars = Math.Max(1_000, initialBudget);
    }

    /// <summary>Budget for this request in the store's active unit (tokens override characters).</summary>
    public static int EffectiveBudget(AiSettings settings) =>
        settings.MaxContextTokens > 0
            ? Math.Max(1_000, settings.MaxContextTokens)
            : Math.Max(1_000, settings.MaxContextCharacters);

    /// <summary>Default per-entry overhead in the legacy character unit (kept for tests/diagnostics).</summary>
    public const int LegacyEntryOverheadChars = 200;

    /// <summary>
    /// Prices content in this store's active unit (tokens or characters). The shrink guard
    /// and the auto-compactor route every size comparison through here so mixed-unit
    /// summaries can never slip past (or falsely fail) the guard.
    /// </summary>
    public int EstimateContent(string content) => _estimate(content ?? string.Empty);

    public int BlockCount { get { lock (_gate) return _blocks.Count; } }
    public long ActiveChars { get { lock (_gate) return _active.Sum(entry => (long)entry.Chars); } }

    /// <summary>Extra request cost (tool schemas) reserved against the budget.</summary>
    public int RequestOverhead { get; set; }

    public int EstimateOverhead(string? system, IReadOnlyList<AiToolDefinition>? tools)
    {
        var total = string.IsNullOrEmpty(system) ? 0 : _estimate(system);
        if (tools is null) return total;
        foreach (var tool in tools)
            total += _estimate(tool.Name + "\n" + tool.Description + "\n" + tool.InputSchema.GetRawText());
        return total;
    }

    public long PressureChars(string? system, IReadOnlyList<AiToolDefinition>? tools) =>
        ActiveChars + EstimateOverhead(system, tools);

    /// <summary>Compact usage line for the chat status bar, e.g. "上下文 38%（45.6K / 120K 字符 · 块 2）".</summary>
    /// <summary>Unit label for the status bar (tokens when token budgeting is active).</summary>
    public string UsageLine(int budget)
    {
        lock (_gate)
        {
            var chars = _active.Sum(entry => (long)entry.Chars);
            var safeBudget = Math.Max(1_000, budget);
            return $"上下文 {chars / (double)safeBudget:P0}（{FormatSize(chars)} / {FormatSize(safeBudget)} 字符 · 块 {_blocks.Count(static block => block.Active)}）";
        }
    }

    #region Append

    public void AppendUser(string content) => Append(new ChatMessage("user", content));
    public void AppendAssistant(string? content) => Append(new ChatMessage("assistant", content));

    public void AppendToolResult(string toolCallId, string content, string? toolName = null,
        IReadOnlyList<ChatImage>? images = null)
    {
        // 工具输出是最值得压缩的内容,但刚产生的输出属于当前工作集 —— 由保护窗口兜底。
        var entry = Append(new ChatMessage("tool", content, ToolCallId: toolCallId, Images: images));
        entry.Chars = _estimate(content) + (images is { Count: > 0 } ? 2_000 : 0);
        entry.ToolName = toolName;
    }

    public void AppendAssistantToolCalls(string? content, IReadOnlyList<AssistantToolCall> toolCalls) =>
        Append(new ChatMessage("assistant", content, ToolCalls: toolCalls));

    private AcpEntry Append(ChatMessage message)
    {
        lock (_gate)
        {
            var entry = new AcpEntry
            {
                Ref = $"m{++_nextRef:D5}",
                Message = message,
                Chars = _estimate(message.Content ?? string.Empty) + (message.ToolCalls is { Count: > 0 } ? 400 : 0)
            };
            _active.Add(entry);
            return entry;
        }
    }

    /// <summary>Snapshot of active entries for diagnostics and tests.</summary>
    public IReadOnlyList<AcpEntry> ActiveEntries { get { lock (_gate) return _active.ToArray(); } }
    public IReadOnlyList<AcpBlock> Blocks { get { lock (_gate) return _blocks.ToArray(); } }

    #endregion

    #region Build request

    /// <summary>System prompt + ref-annotated active messages + ACP guidance.</summary>
    public IReadOnlyList<ChatMessage> BuildRequest(
        AgentMemoryDocument memory, IReadOnlyList<AgentSkill> skills, AiSettings settings)
    {
        GcIfNeeded(settings, RequestOverhead);
        List<AcpEntry> snapshot;
        long chars;
        lock (_gate)
        {
            snapshot = [.. _active];
            chars = _active.Sum(entry => (long)entry.Chars);
            _buildCount++;
        }

        var budget = EffectiveBudget(settings);
        _budgetChars = budget;
        var system = _systemMessageFactory() + "\n" + PhilosophyLine;
        var nudge = BuildNudge(chars, budget, snapshot);
        if (nudge.Length > 0) system += "\n" + nudge;
        if (_gcRan) system += "\n[ACP] 更早的上下文已超出预算被自动截断；可用 context_search 检索已归档内容。";

        var reserved = RequestOverhead + _estimate(system);
        if (chars + reserved > budget)
        {
            GcIfNeeded(settings, reserved);
            lock (_gate)
            {
                snapshot = [.. _active];
                chars = _active.Sum(entry => (long)entry.Chars);
            }
        }

        var messages = new List<ChatMessage>(snapshot.Count + 1) { new("system", system) };
        foreach (var entry in snapshot)
            messages.Add(Annotate(entry));
        KeepNewestScreenshot(messages);
        return messages;
    }

    /// <summary>Stable one-line guidance injected on every request so compression stays in attention.</summary>
    public const string PhilosophyLine =
        "[ACP 上下文管理] 你负责管理本会话的上下文：当较早的内容不再被当前步骤需要时，调用 context_compress 把该区间替换为你写的自包含摘要" +
        "（保留文件路径、决策、错误信息与用户目标）。压缩前可用 context_status 查看用量与可压缩区间，context_search 可检索已归档块。";

    private static void KeepNewestScreenshot(List<ChatMessage> messages)
    {
        var newest = -1;
        for (var index = messages.Count - 1; index >= 1; index--)
        {
            if (messages[index].Images is not { Count: > 0 }) continue;
            newest = index;
            break;
        }
        if (newest < 0) return;
        for (var index = 1; index < messages.Count; index++)
        {
            if (index == newest || messages[index].Images is not { Count: > 0 }) continue;
            messages[index] = messages[index] with
            {
                Images = null,
                Content = (messages[index].Content ?? string.Empty) + "（较旧截图已省略）"
            };
        }
    }

    private static ChatMessage Annotate(AcpEntry entry)
    {
        var message = entry.Message;
        var tag = message.Role == "tool" ? $"[{entry.Ref}·tool] " : $"[{entry.Ref}] ";
        if (string.IsNullOrEmpty(message.Content))
        {
            // Pure tool_calls turns carry no anchor text; synthesize one so every
            // entry exposes its ref as a possible compression boundary.
            var names = message.ToolCalls is { Count: > 0 } calls
                ? string.Join(", ", calls.Select(call => call.Name).Distinct(StringComparer.Ordinal))
                : "assistant";
            return message with { Content = $"{tag}(调用工具 {names})" };
        }
        return message with { Content = tag + message.Content };
    }

    internal static string FormatSize(long chars) =>
        chars >= 1000 ? $"{chars / 1000d:0.#}K" : chars.ToString("0");

    private string BuildNudge(long chars, long budget, IReadOnlyList<AcpEntry> snapshot)
    {
        if (snapshot.Count == 0 || chars < budget * MinNudgeRatio) return string.Empty;
        var strong = chars >= budget * MaxNudgeRatio;
        // 软阈值下降低提示频率,硬阈值下每轮都提醒。
        if (!strong && _buildCount % NudgeEveryBuilds != 0) return string.Empty;

        var suggestions = SuggestRanges(snapshot);
        if (suggestions.Count == 0 && !strong) return string.Empty;

        var builder = new StringBuilder("[ACP 上下文预算] ");
        builder.Append(strong
            ? $"活动上下文已达预算的 {chars / (double)budget:P0}（{chars:N0}/{budget:N0} 字符），必须立即压缩："
            : $"活动上下文约占预算 {chars / (double)budget:P0}（{chars:N0}/{budget:N0} 字符），建议适时压缩：");
        AppendBreakdown(builder, snapshot);
        if (suggestions.Count > 0)
        {
            builder.Append("可压缩区间（从大到小）:\n");
            foreach (var suggestion in suggestions)
                builder.Append($"  [{suggestion.StartRef}]–[{suggestion.EndRef}] 约 {FormatSize(suggestion.Chars)} 字符 ({suggestion.Label})\n");
        }
        else
        {
            builder.Append("当前没有可安全压缩的大区间；避免再产生大结果输出。");
        }
        builder.Append("调用 context_compress，ranges 传入 {{start,end,summary,title}}。优先级: 冗长工具输出(构建/扫描/目录列表) → " +
                       "无结果的探索 → 重复读取 → 已完成任务的中间步骤。必须保留文件路径、决策、错误信息与用户目标。" +
                       $"最近 {ProtectedRecentEntries} 条消息、最后一条用户消息与 context_compress 结果受保护；" +
                       "区间不能把一次工具调用和它的结果分开。摘要要自包含——解压前的检索只能看到它。");
        if (strong)
            builder.Append(" 压缩完成前不要再发起新的大结果工具调用；若无可压缩区间，超出预算的部分会被自动截断。");
        return builder.ToString();
    }

    private static void AppendBreakdown(StringBuilder builder, IReadOnlyList<AcpEntry> snapshot)
    {
        long tool = 0, user = 0, assistant = 0;
        foreach (var entry in snapshot)
        {
            switch (entry.Message.Role)
            {
                case "tool": tool += entry.Chars; break;
                case "user": user += entry.Chars; break;
                default: assistant += entry.Chars; break;
            }
        }
        builder.Append($" 构成: 工具输出 {FormatSize(tool)} / 用户 {FormatSize(user)} / 助手 {FormatSize(assistant)} 字符。 ");
    }

    #endregion

    #region Suggestions

    /// <summary>Largest contiguous runs of compressible (unprotected) entries, best first.</summary>
    public List<AcpRangeSuggestion> SuggestRanges(IReadOnlyList<AcpEntry> snapshot)
    {
        var flags = ComputeProtections(snapshot);
        var runs = new List<AcpRangeSuggestion>();
        var index = 0;
        while (index < snapshot.Count)
        {
            if (flags[index]) { index++; continue; }
            var start = index;
            long runChars = 0;
            while (index < snapshot.Count && !flags[index])
            {
                runChars += snapshot[index].Chars;
                index++;
            }
            if (runChars >= MinCandidateChars)
            {
                var slice = snapshot.Skip(start).Take(index - start).ToArray();
                var roles = slice
                    .GroupBy(entry => entry.Message.Role).OrderByDescending(group => group.Sum(entry => (long)entry.Chars))
                    .Select(group => group.Key == "tool" ? "工具输出" : group.Key == "user" ? "用户" : "助手")
                    .ToList();
                runs.Add(new AcpRangeSuggestion(snapshot[start].Ref, snapshot[index - 1].Ref, runChars,
                    roles.Count == 0 ? "混合" : string.Join("+", roles), ScoreRange(slice, runChars)));
            }
        }
        return runs
            .OrderByDescending(run => run.Score)
            .ThenByDescending(run => run.Chars)
            .Take(MaxSuggestions)
            .ToList();
    }

    private static long ScoreRange(IReadOnlyList<AcpEntry> entries, long runChars)
    {
        long score = runChars;
        foreach (var entry in entries)
        {
            var name = entry.ToolName ?? string.Empty;
            var text = entry.Message.Content ?? string.Empty;
            if (IsEvidenceTool(name) || text.Contains("spill:", StringComparison.Ordinal) ||
                text.Contains("\"code\":", StringComparison.Ordinal))
                score -= entry.Chars / 2;
            else if (IsLowValueTool(name))
                score += entry.Chars;
        }
        return score;
    }

    private static bool IsEvidenceTool(string name) =>
        name is "packet_analyze" or "packet_show" or "page_security_snapshot"
            or "assessment_evidence" or "assessment_findings";

    private static bool IsLowValueTool(string name) =>
        name is "console_read" or "network_list" or "page_query" or "packet_list" or "page_screenshot";

    #endregion

    #region Protection

    /// <summary>True when the entry must survive compression ranges and GC as long as possible.</summary>
    private static bool IsLoadBearing(AcpEntry entry) =>
        entry.Message.Role == "tool" && string.Equals(entry.ToolName, LoadBearingTool, StringComparison.Ordinal);

    private bool[] ComputeProtections(IReadOnlyList<AcpEntry> snapshot)
    {
        var flags = new bool[snapshot.Count];
        for (var i = Math.Max(0, snapshot.Count - ProtectedRecentEntries); i < snapshot.Count; i++)
            flags[i] = true;

        // Soft char zone over non-tool entries only: verbose tool output is exactly what
        // should stay compressible once consumed, so it does not extend the zone.
        long accumulated = 0;
        var softBudget = (long)(_budgetChars * 0.15);
        for (var i = snapshot.Count - 1; i >= 0 && accumulated < softBudget; i--)
        {
            if (snapshot[i].Message.Role == "tool") continue;
            flags[i] = true;
            accumulated += snapshot[i].Chars;
        }

        for (var i = snapshot.Count - 1; i >= 0; i--)
            if (snapshot[i].Message.Role == "user") { flags[i] = true; break; }

        for (var i = 0; i < snapshot.Count; i++)
            if (IsLoadBearing(snapshot[i])) flags[i] = true;

        return flags;
    }

    #endregion

    #region Segments (tool-pair integrity)

    /// <summary>
    /// Atomic segments: an assistant tool_call turn plus its consecutive results moves as one
    /// unit, so a compression range can never produce an orphaned tool result transcript.
    /// </summary>
    private static List<(int Start, int End)> PartitionSegments(IReadOnlyList<AcpEntry> snapshot)
    {
        var segments = new List<(int, int)>();
        var index = 0;
        while (index < snapshot.Count)
        {
            var start = index;
            var entry = snapshot[index];
            if (entry.Message.ToolCalls is { Count: > 0 } calls)
            {
                var pending = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
                index++;
                while (index < snapshot.Count && pending.Count > 0)
                {
                    var toolCallId = snapshot[index].Message.ToolCallId;
                    if (toolCallId is not null && pending.Remove(toolCallId)) { index++; continue; }
                    break;
                }
            }
            else index++;
            segments.Add((start, index - 1));
        }
        return segments;
    }

    #endregion

    #region Compress / decompress

    /// <summary>Compress one inclusive ref range into a block marker. Returns (ok, message).</summary>
    public (bool Ok, string Message) Compress(string startRef, string endRef, string? summary, string? title)
    {
        lock (_gate)
        {
            var start = IndexOfRef(startRef);
            var end = IndexOfRef(endRef);
            if (start < 0 || end < 0)
                return (false, $"引用不存在或已归档: {startRef} / {endRef} 中至少一个无效。可用 context_status 查看当前引用，不要按记忆推算引用。");
            if (start > end) (start, end) = (end, start);

            var snapshot = _active.ToArray();
            var segments = PartitionSegments(snapshot);
            start = segments.First(segment => segment.Start <= start && start <= segment.End).Start;
            end = segments.First(segment => segment.Start <= end && end <= segment.End).End;

            var flags = ComputeProtections(snapshot);
            for (var i = start; i <= end; i++)
                if (flags[i])
                    return (false, $"区间包含受保护的条目 [{snapshot[i].Ref}]（最近工作集、最后一条用户消息或 context_compress 结果）。" +
                                   "请把范围缩小到更早的内容，或拆成多个避开保护区的区间。");

            var consumed = snapshot[start..(end + 1)].ToList();
            if (consumed.All(entry => entry.BlockId is { } id && FindBlock(id) is { Tier: 3 } ))
                return (false, "该区间只包含 T3 终端块——T3 已是最高层，重写它不会回收任何空间且会无限重复。" +
                               "如需细节请用 context_decompress 或 context_search；要压缩请选择包含未压缩消息的区间（context_status 会列出）。");

            if ((summary ?? string.Empty).Trim().Length == 0)
                return (false, "summary 不能为空——它是解压前唯一的检索来源。");

            // Hard shrink guard (dsh invariant): a replacement must be strictly cheaper than
            // the range it shadows, otherwise compression reclaims nothing and can loop forever.
            // Priced through the store's own estimator so token-budgeted stores compare tokens.
            if (EstimateContent((summary ?? string.Empty).Trim()) >= consumed.Sum(entry => (long)entry.Chars))
                return (false, "摘要不小于原区间（收缩守卫）：压缩无法回收空间。请提供更精炼的自包含摘要，" +
                               "或改用 context_decompress / context_search 查看已有内容。");

            string warning = QualityGateWarning(consumed, summary);

            if (consumed.All(entry => entry.BlockId is null))
            {
                CreateBlock(consumed, summary, title, tier: 1);
            }
            else
            {
                // T2/T3: distill existing block markers; tier climbs, capped at T3
                var nextTier = Math.Min(3, consumed.Where(entry => entry.BlockId is not null)
                    .Select(entry => FindBlock(entry.BlockId!)?.Tier ?? 1).Min() + 1);
                foreach (var entry in consumed.Where(entry => entry.BlockId is not null))
                    if (FindBlock(entry.BlockId!) is { } inner) inner.Active = false;
                var mergedOriginals = consumed.SelectMany(entry =>
                        entry.BlockId is { } id ? FindBlock(id)?.OriginalEntries ?? [entry] : [entry])
                    .ToList();
                var block = CreateBlock(consumed, summary, title, nextTier);
                block.OriginalEntries = mergedOriginals;
                block.OriginalChars = mergedOriginals.Sum(entry => (long)entry.Chars);
            }

            var markerChars = _active.FirstOrDefault(entry => entry.BlockId == LastBlockId())?.Chars ?? 0;
            var reclaimed = Math.Max(0, consumed.Sum(entry => (long)entry.Chars) - markerChars);
            var message = $"已压缩 [{startRef}]–[{endRef}] 为块 {LastBlockId()}（T{_blocks[^1].Tier}），" +
                          $"活动上下文约减少 {reclaimed:N0} 字符。" +
                          "原始内容可用 context_decompress 恢复，context_search 可检索。";
            if (warning.Length > 0) message += "\n⚠️ " + warning;
            return (true, message);
        }
    }

    /// <summary>
    /// Non-blocking quality gate (opencode-acp rouge-recall-v1, layer 1 only): flag summaries
    /// so short relative to their range that catastrophic content loss is likely.
    /// </summary>
    private static string QualityGateWarning(List<AcpEntry> consumed, string? summary)
    {
        var clean = (summary ?? string.Empty).Trim();
        var rangeChars = consumed.Sum(entry => (long)entry.Chars);
        var floor = Math.Max(200, rangeChars / 100);
        return clean.Length < floor
            ? $"质量门提醒: 区间约 {rangeChars:N0} 字符而摘要仅 {clean.Length} 字符（下限 {floor:N0}）。" +
              "过短的摘要可能丢失关键细节；确认已保留路径/决策/错误信息，否则可 decompress 后重新压缩。"
            : string.Empty;
    }

    private string LastBlockId() => _blocks.Count == 0 ? "?" : $"b{_blocks.Count}";

    private AcpBlock CreateBlock(List<AcpEntry> consumed, string? summary, string? title, int tier)
    {
        var block = new AcpBlock
        {
            Id = $"b{_blocks.Count + 1}",
            Tier = tier,
            Title = (title ?? string.Empty).Trim(),
            Summary = summary ?? string.Empty,
            OriginalChars = consumed.Sum(entry => (long)entry.Chars),
            OriginalEntries = consumed
        };
        _blocks.Add(block);

        var markerText =
            $"[系统存档 {block.Id}·T{tier}] 已压缩 {consumed.First().Ref}–{consumed[^1].Ref}" +
            $"（原约 {block.OriginalChars:N0} 字符）。标题: {(block.Title.Length == 0 ? "(未命名)" : block.Title)}\n" +
            $"摘要: {block.Summary}\n" +
            $"（本条为系统生成的存档标记，不是用户输入；需要原文时调用 context_decompress {{\"block\":\"{block.Id}\"}}）";
        var spills = CollectSpillLocators(consumed);
        if (spills.Count > 0)
            markerText += "\n外存: " + string.Join(" ", spills);
        var marker = new AcpEntry
        {
            Ref = NextMarkerRef(),
            Message = new ChatMessage("user", markerText),
            Chars = _estimate(markerText),
            BlockId = block.Id
        };
        var insertAt = Math.Max(_active.IndexOf(consumed[0]), 0);
        _active.Insert(insertAt, marker);
        foreach (var entry in consumed) _active.Remove(entry);
        return block;
    }

    private int _markerSeq;
    private string NextMarkerRef() => $"m{++_nextRef:D5}.{++_markerSeq}";

    public (bool Ok, string Message) Decompress(string blockId)
    {
        lock (_gate)
        {
            var block = _blocks.FirstOrDefault(value => value.Id == blockId);
            if (block is null) return (false, $"找不到压缩块 {blockId}。可用 context_status 查看全部块。");
            if (block.IsTombstone) return (false, $"压缩块 {blockId} 是自动截断的存档，原文已不在会话中；只能通过 context_search 查看其线索。");
            if (!block.Active) return (false, $"压缩块 {blockId} 已被更高层压缩合并，无法单独恢复；请恢复合并它的那个更高层块。");

            var markerIndex = _active.FindIndex(entry => string.Equals(entry.BlockId, blockId, StringComparison.Ordinal));
            if (markerIndex < 0) return (false, $"压缩块 {blockId} 的标记不在当前上下文中。");

            _active.RemoveAt(markerIndex);
            for (var index = block.OriginalEntries.Count - 1; index >= 0; index--)
                _active.Insert(markerIndex, CloneOriginal(block.OriginalEntries[index]));
            block.Active = false;
            return (true, $"已恢复 {blockId} 的原文（约 {block.OriginalChars:N0} 字符）。该块不再占用存档位；可重新压缩。");
        }
    }

    private static AcpEntry CloneOriginal(AcpEntry original) => new()
    {
        Ref = original.Ref,
        Message = original.Message,
        Chars = original.Chars,
        ToolName = original.ToolName
    };

    #endregion

    #region Search / status

    public string Search(string query, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 8 : limit, 1, 32);
        var keyword = query.Trim();
        if (keyword.Length == 0) return "查询为空。";
        var results = new List<string>();
        lock (_gate)
        {
            foreach (var block in _blocks.AsEnumerable().Reverse())
            {
                var haystack = $"{block.Title} {block.Summary}";
                var label = block.IsTombstone ? ",自动截断" : block.Active ? "" : ",已合并";
                var title = block.Title.Length == 0 ? "未命名" : block.Title;
                if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"{block.Id}(T{block.Tier}{label}) \"{title}\": {Shorten(block.Summary, 220)}");
                    if (results.Count >= limit) return JsonSerializer.Serialize(results);
                    continue;
                }
                foreach (var original in block.OriginalEntries)
                {
                    var text = original.Message.Content;
                    if (string.IsNullOrEmpty(text) || !text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        continue;
                    results.Add($"{block.Id}(T{block.Tier}{label}) \"{title}\": {Shorten(text, 180)}");
                    if (results.Count >= limit) return JsonSerializer.Serialize(results);
                    break;
                }
            }
            foreach (var entry in _active.AsEnumerable().Reverse())
            {
                var text = entry.Message.Content;
                if (string.IsNullOrEmpty(text) || !text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add($"[{entry.Ref}] {entry.Message.Role}: {Shorten(text, 200)}");
                if (results.Count >= limit) break;
            }
        }
        return results.Count == 0
            ? $"没有匹配 \"{keyword}\" 的内容（含已压缩块）。"
            : JsonSerializer.Serialize(results);
    }

    public string Status()
    {
        lock (_gate)
        {
            var chars = _active.Sum(entry => (long)entry.Chars);
            var payload = new AcpStatusPayload
            {
                ActiveEntries = _active.Count,
                ActiveChars = chars,
                OldestRef = _active.Count == 0 ? null : _active[0].Ref,
                NewestRef = _active.Count == 0 ? null : _active[^1].Ref,
                GcRan = _gcRan,
                Breakdown = new AcpStatusBreakdown
                {
                    ToolChars = _active.Where(entry => entry.Message.Role == "tool").Sum(entry => (long)entry.Chars),
                    UserChars = _active.Where(entry => entry.Message.Role == "user").Sum(entry => (long)entry.Chars),
                    AssistantChars = _active.Where(entry => entry.Message.Role == "assistant").Sum(entry => (long)entry.Chars)
                },
                SuggestedRanges = SuggestRanges(_active),
                Blocks = _blocks.Select(value => new AcpStatusBlock
                {
                    Id = value.Id,
                    Tier = value.Tier,
                    Title = value.Title,
                    Active = value.Active,
                    Tombstone = value.IsTombstone,
                    SummaryChars = value.Summary.Length,
                    OriginalChars = value.OriginalChars,
                    AgeMinutes = (long)(DateTimeOffset.UtcNow - value.CreatedAt).TotalMinutes
                }).ToList()
            };
            return JsonSerializer.Serialize(payload, AcpJsonContext.Default.AcpStatusPayload);
        }
    }

    #endregion

    #region GC fallback

    /// <summary>
    /// Last-resort truncation: drop oldest droppable entries until within budget. Victims are
    /// parked into a searchable tombstone block instead of vanishing silently. Load-bearing
    /// context_compress results are dropped only after everything else is gone.
    /// </summary>
    private void GcIfNeeded(AiSettings settings, int reserve = 0)
    {
        var budget = EffectiveBudget(settings);
        _budgetChars = budget;
        var limit = Math.Max(1_000, budget - Math.Max(0, reserve));
        lock (_gate)
        {
            var dropped = new List<AcpEntry>();
            while (_active.Sum(entry => (long)entry.Chars) > limit &&
                   _active.Count > ProtectedRecentEntries + 1)
            {
                var snapshot = _active.ToArray();
                var removableEnd = snapshot.Length - ProtectedRecentEntries - 2;
                var segments = PartitionSegments(snapshot)
                    .Where(segment => segment.End <= removableEnd)
                    .ToList();
                if (segments.Count == 0) break;

                // A tool-call assistant message and all of its results form one protocol unit.
                // Removing entries one at a time can leave a role=tool message without its
                // preceding tool_calls message, which strict OpenAI-compatible APIs reject.
                var victimSegmentIndex = segments.FindIndex(segment =>
                    snapshot.Skip(segment.Start).Take(segment.End - segment.Start + 1)
                        .All(entry => !IsLoadBearing(entry)));
                var victimSegment = segments[victimSegmentIndex >= 0 ? victimSegmentIndex : 0];

                var victims = snapshot[victimSegment.Start..(victimSegment.End + 1)];
                foreach (var victim in victims)
                {
                    if (victim.BlockId is { } id && FindBlock(id) is { } owner) owner.Active = false;
                    _active.Remove(victim);
                    dropped.Add(victim);
                }
                _gcRan = true;
            }
            if (dropped.Count > 0) CreateTombstone(dropped);
        }
    }

    private void CreateTombstone(List<AcpEntry> victims)
    {
        var digest = new StringBuilder();
        foreach (var victim in victims)
        {
            var head = Shorten(victim.Message.Content ?? string.Empty, 60);
            digest.Append($"[{victim.Ref}] {victim.Message.Role}{(victim.ToolName is { } name ? $"/{name}" : "")} " +
                          $"约 {victim.Chars} 字符: {head}\n");
            if (digest.Length > TombstoneDigestCap) break;
        }
        var block = new AcpBlock
        {
            Id = $"b{_blocks.Count + 1}",
            Tier = 1,
            Title = "(自动截断)",
            Summary = $"超出预算被自动截断的 {victims.Count} 条内容:\n{digest}",
            OriginalChars = victims.Sum(entry => (long)entry.Chars),
            Active = false,
            IsTombstone = true,
            OriginalEntries = victims
        };
        _blocks.Add(block);
    }

    #endregion

    private AcpBlock? FindBlock(string id) => _blocks.FirstOrDefault(value => value.Id == id);

    private int IndexOfRef(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return -1;
        var trimmed = reference.Trim().TrimStart('[').Split('·', ']', ' ')[0];
        if (trimmed.Length < 2 || trimmed[0] != 'm') return -1;
        var digits = new string(trimmed.Skip(1).TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var number)) return -1;
        var prefix = $"m{number:D5}";
        return _active.FindIndex(entry => entry.Ref.StartsWith(prefix, StringComparison.Ordinal));
    }

    private int Estimate(string? content) => _estimate(content ?? string.Empty);

    private static string Shorten(string value, int max)
    {
        var flat = value.Replace("\r", string.Empty).Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static List<string> CollectSpillLocators(IEnumerable<AcpEntry> entries)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var text = entry.Message.Content;
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match match in Regex.Matches(text, "spill:[0-9a-f]{32}"))
            {
                if (!seen.Add(match.Value)) continue;
                found.Add(match.Value);
                if (found.Count >= 8) return found;
            }
        }
        return found;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AcpStatusPayload))]
internal partial class AcpJsonContext : JsonSerializerContext
{
}

public sealed class AcpStatusPayload
{
    public int ActiveEntries { get; set; }
    public long ActiveChars { get; set; }
    public string? OldestRef { get; set; }
    public string? NewestRef { get; set; }
    public bool GcRan { get; set; }
    public AcpStatusBreakdown? Breakdown { get; set; }
    public List<AcpRangeSuggestion> SuggestedRanges { get; set; } = [];
    public List<AcpStatusBlock> Blocks { get; set; } = [];
}

public sealed class AcpStatusBreakdown
{
    public long ToolChars { get; set; }
    public long UserChars { get; set; }
    public long AssistantChars { get; set; }
}

public sealed record AcpRangeSuggestion(string StartRef, string EndRef, long Chars, string Label, long Score = 0);

public sealed class AcpStatusBlock
{
    public string Id { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool Active { get; set; }
    public bool Tombstone { get; set; }
    public int SummaryChars { get; set; }
    public long OriginalChars { get; set; }
    public long AgeMinutes { get; set; }
}

/// <summary>Bridges per-session stores to the globally registered Agent tools.</summary>
public sealed class AcpContextRegistry
{
    public AcpContextStore? Current { get; set; }
}

/// <summary>
/// ACP 的模型侧工具面:compress / decompress / search / status。
/// 全部只操作本地会话记忆,不触碰外部状态,因此按只读风险放行,无需逐步确认 —— 与
/// opencode-acp 的 "allow" 权限一致。压缩本身是唯一改变上下文的动作,而它正是被授权的目的。
/// </summary>
public sealed class AcpToolAdapter(AcpContextRegistry registry)
{
    public void RegisterAll(IAiToolRegistry toolRegistry)
    {
        toolRegistry.Register(new AiToolDefinition(
            "context_compress",
            "Compress no-longer-needed message ranges into self-contained summary blocks to reclaim context. " +
            "Ranges are given with [start]/[end] message refs. Batch multiple unrelated ranges in one call. " +
            "Recent messages, the last user message, prior context_compress results are protected; a range may " +
            "never split an assistant tool call from its results.",
            Schema(new
            {
                ranges = new
                {
                    type = "array",
                    description = "one or more ranges, applied in order",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            start = new { type = "string", description = "first ref, e.g. m00003" },
                            end = new { type = "string", description = "last ref, e.g. m00018" },
                            title = new { type = "string", description = "short block name" },
                            summary = new { type = "string", description = "self-contained summary: keep file paths, decisions, errors, user goals" }
                        },
                        required = new[] { "start", "end", "summary" },
                        additionalProperties = false
                    }
                }
            }), AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(Compress(call.Arguments))));

        toolRegistry.Register(new AiToolDefinition(
            "context_decompress",
            "Restore the original messages of one compressed block back into context.",
            Schema(new { @block = new { type = "string", description = "block id, e.g. b3" } }),
            AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(Decompress(call.Arguments))));

        toolRegistry.Register(new AiToolDefinition(
            "context_search",
            "Search compressed block summaries and active messages by keyword without decompressing. " +
            "Use before decompressing to locate the right block.",
            Schema(new
            {
                query = new { type = "string" },
                limit = new { type = "integer", description = "optional, default 8" }
            }), AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(WithStore(store => store.Search(
                Text(call.Arguments, "query"),
                Number(call.Arguments, "limit", 8))))));

        toolRegistry.Register(new AiToolDefinition(
            "context_status",
            "Show active context usage, category breakdown, suggested compressible ranges and all compression blocks.",
            Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(WithStore(store => store.Status()))));
    }

    private ToolResult Compress(JsonElement arguments)
    {
        return WithStoreResult(store =>
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("ranges", out var ranges))
                return ToolResult.Fail("ranges 必须是非空数组，每项包含 start/end/summary。");

            // 非 strict 工具提供方有时把嵌套数组序列化成字符串 —— 接受 JSON 编码形式并自动解析。
            if (ranges.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(ranges.GetString() ?? string.Empty);
                    ranges = parsed.RootElement.Clone();
                }
                catch (JsonException exception)
                {
                    return ToolResult.Fail($"ranges 不是有效 JSON: {exception.Message}。请直接传数组。");
                }
            }

            if (ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() == 0)
                return ToolResult.Fail("ranges 必须是非空数组，每项包含 start/end/summary。");

            var applied = 0;
            var failures = new List<string>();
            foreach (var range in ranges.EnumerateArray())
            {
                var result = store.Compress(
                    Text(range, "start"), Text(range, "end"),
                    Text(range, "summary"), Text(range, "title"));
                if (result.Ok) applied++;
                else failures.Add(result.Message);
            }

            return failures.Count == 0
                ? ToolResult.Ok($"已应用 {applied} 个压缩区间。{Environment.NewLine}{store.Status()}")
                : applied > 0
                    ? ToolResult.Ok($"已应用 {applied} 个区间；{failures.Count} 个失败: {string.Join("；", failures)}")
                    : ToolResult.Fail(string.Join("；", failures));
        });
    }

    private ToolResult Decompress(JsonElement arguments) => WithStoreResult(store =>
    {
        var result = store.Decompress(Text(arguments, "block"));
        return result.Ok ? ToolResult.Ok(result.Message) : ToolResult.Fail(result.Message);
    });

    private ToolResult WithStore(Func<AcpContextStore, string> action)
    {
        var store = registry.Current;
        return store is null
            ? ToolResult.Fail("当前没有活动的 AI 会话上下文。")
            : ToolResult.Ok(action(store));
    }

    private ToolResult WithStoreResult(Func<AcpContextStore, ToolResult> action)
    {
        var store = registry.Current;
        return store is null
            ? ToolResult.Fail("当前没有活动的 AI 会话上下文。")
            : action(store);
    }

    private static JsonElement Schema(object properties) =>
        JsonSerializer.SerializeToElement(new { type = "object", properties, additionalProperties = false });

    private static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement arguments, string name, int fallback) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : fallback;
}
