using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>Request prefix (system prompt + tools) shared with main calls for KV-cache alignment.</summary>
public sealed record CompactionPrefix(string SystemPrompt, IReadOnlyList<AiToolDefinition> Tools);

/// <summary>
/// Pressure-triggered automatic context compaction for the ACP store, ported from dsh's
/// compaction-basic: when active context crosses a ratio of the budget, the oldest safe
/// compressible range is summarized by an auxiliary model call and landed through the
/// store's normal <c>context_compress</c> path — so protections (recent working set, last
/// user message, load-bearing compress results) and tool-pair integrity still apply.
///
/// Ported invariants: the summary must be strictly smaller than what it replaces
/// (shrink guard), attempts are rate-limited so a failing summarizer cannot loop, and
/// every failure is best-effort — the nudge/GC ladder remains the fallback.
///
/// KV-cache alignment: when a prefix provider is wired, the summarizer replays the very
/// system prompt used by main requests in front of the range (tool schemas are omitted so
/// the auxiliary call stays small), sharing the longest prompt prefix with recent traffic.
/// </summary>
public sealed class AcpAutoCompactor
{
    private const int MinimumRangeChars = 2_000;
    private const int MaximumEntryPreviewChars = 6_000;
    private const int EntryHeadChars = 2_500;
    private const int EntryTailChars = 1_500;
    private const long MaximumSummarizerInputChars = 60_000;
    private static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromSeconds(20);
    private static readonly Regex SpillLocatorRegex = new(
        "spill:[0-9a-f]{32}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IOpenAiChatClient _client;
    private readonly Func<string?> _modelProvider;
    private readonly Func<AcpContextStore?> _storeProvider;
    private readonly Func<AiSettings> _settingsProvider;
    private readonly Func<CompactionPrefix?>? _prefixProvider;
    private readonly IAppLogger? _logger;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public AcpAutoCompactor(
        IOpenAiChatClient client,
        Func<string?> modelProvider,
        Func<AcpContextStore?> storeProvider,
        Func<AiSettings> settingsProvider,
        IAppLogger? logger = null,
        Func<CompactionPrefix?>? prefixProvider = null)
    {
        _client = client;
        _modelProvider = modelProvider;
        _storeProvider = storeProvider;
        _settingsProvider = settingsProvider;
        _logger = logger?.ForCategory(nameof(AcpAutoCompactor));
        _prefixProvider = prefixProvider;
    }

    /// <summary>
    /// Ratio clamp with per-model override (dsh modelPolicies): the first policy whose
    /// ModelFragment occurs in the routed model name wins; 0 disables auto-compaction.
    /// </summary>
    public double ResolvePressureRatio(AiSettings settings)
    {
        var model = _modelProvider() ?? string.Empty;
        foreach (var policy in settings.CompactionModelPolicies)
        {
            if (string.IsNullOrWhiteSpace(policy.ModelFragment)) continue;
            if (!model.Contains(policy.ModelFragment, StringComparison.OrdinalIgnoreCase)) continue;
            if (policy.Ratio <= 0) return 0;
            return Math.Clamp(policy.Ratio, 0.5, 0.95);
        }
        if (settings.AutoCompactRatio <= 0) return 0;
        return Math.Clamp(settings.AutoCompactRatio, 0.5, 0.95);
    }

    /// <summary>
    /// Returns a compacted-event payload when a compression landed, null otherwise.
    /// Never throws for summarization failures; cancellation still propagates.
    /// </summary>
    public async Task<ContextCompacted?> CompactIfNeededAsync(CancellationToken ct)
    {
        var store = _storeProvider();
        if (store is null) return null;
        var settings = _settingsProvider();
        var ratio = ResolvePressureRatio(settings);
        if (ratio <= 0) return null;

        var budget = AcpContextStore.EffectiveBudget(settings);
        var prefix = _prefixProvider?.Invoke();
        if (store.PressureChars(prefix?.SystemPrompt, prefix?.Tools) < budget * ratio) return null;
        if (DateTimeOffset.UtcNow - _lastAttempt < MinimumAttemptInterval) return null;

        var compacted = await CompactLargestRangeAsync(store, MinimumRangeChars, ct).ConfigureAwait(true);
        if (compacted is not null)
            _lastAttempt = DateTimeOffset.UtcNow;
        return compacted;
    }

    /// <summary>
    /// Forced one-shot reduction for context-overflow recovery (dsh compaction-basic's
    /// overflow path): bypasses the pressure threshold and the rate limit, because the
    /// provider just rejected the request outright. Still honors protections, tool-pair
    /// integrity and the shrink guard; returns null when nothing safe can be freed.
    /// </summary>
    public Task<ContextCompacted?> CompactNowAsync(CancellationToken ct)
    {
        var store = _storeProvider();
        if (store is null) return Task.FromResult<ContextCompacted?>(null);
        // A smaller floor than the pressure path: recovery must find something even in a
        // window that is already fairly tight.
        return CompactLargestRangeAsync(store, Math.Min(MinimumRangeChars, 800), ct);
    }

    private async Task<ContextCompacted?> CompactLargestRangeAsync(
        AcpContextStore store,
        int minimumRangeChars,
        CancellationToken ct)
    {
        var snapshot = store.ActiveEntries;
        var target = store.SuggestRanges(snapshot).FirstOrDefault(range => range.Chars >= minimumRangeChars);
        if (target is null)
        {
            _logger?.Warn("自动压缩跳过：当前没有可安全压缩的大区间。");
            return null;
        }

        var startIndex = IndexOfRefPrefix(snapshot, target.StartRef);
        var endIndex = IndexOfRefPrefix(snapshot, target.EndRef);
        if (startIndex < 0 || endIndex < startIndex) return null;
        var rangeEntries = snapshot.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
        var rangeTotalChars = rangeEntries.Sum(entry => (long)entry.Chars);

        string summary;
        try
        {
            summary = await SummarizeAsync(rangeEntries, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger?.Warn($"自动压缩的摘要调用失败：{ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger?.Warn("自动压缩跳过：摘要调用返回空输出。");
            return null;
        }

        // Shrink guard (dsh): the replacement must be strictly cheaper than the replaced
        // range, priced through the store's own estimator so token-budgeted stores compare
        // tokens instead of mixing units.
        var summaryEstimate = store.EstimateContent(summary);
        if (summaryEstimate >= rangeTotalChars)
        {
            _logger?.Warn($"自动压缩被收缩守卫拒绝：摘要约 {summaryEstimate:N0}，区间 {rangeTotalChars:N0}。");
            return null;
        }

        var (ok, message) = store.Compress(target.StartRef, target.EndRef, summary, "(自动压缩)");
        if (!ok)
        {
            _logger?.Warn($"自动压缩被存档层拒绝：{message}");
            return null;
        }

        var warning = message.Contains("⚠️", StringComparison.Ordinal)
            ? message[(message.IndexOf("⚠️", StringComparison.Ordinal) + 1)..].Trim()
            : null;
        // Summary rides in the payload so event-log replay can rebuild the block verbatim.
        return new ContextCompacted(
            Math.Max(0, rangeTotalChars - summaryEstimate),
            $"[{target.StartRef}]–[{target.EndRef}]",
            Automatic: true,
            warning,
            summary);
    }

    private async Task<string> SummarizeAsync(IReadOnlyList<AcpEntry> rangeEntries, CancellationToken ct)
    {
        var messages = new List<ChatMessage>();

        // KV-cache alignment (dsh summarizer contract): when the main request prefix is
        // available, replay it verbatim so the auxiliary call shares its longest prefix
        // with recent traffic; otherwise fall back to a compact dedicated system prompt.
        var prefix = _prefixProvider?.Invoke();
        messages.Add(new ChatMessage("system", prefix?.SystemPrompt ??
            "你是会话压缩器。把给定对话区间改写为一份自包含的技术摘要，供后续模型在没有原文的情况下继续工作。" +
            "必须保留：用户目标、关键路径/URL/参数与标识符、重要决策及理由、错误信息的关键原文、未完成事项与下一步。" +
            "用简体中文，按小节输出：【目标】【关键事实】【错误与修复】【待办】。不要寒暄，不要新增原文没有的信息，只输出摘要正文。"));

        long inputBudget = MaximumSummarizerInputChars;
        var lines = new string[rangeEntries.Count];
        var selected = new bool[rangeEntries.Count];
        for (var index = 0; index < rangeEntries.Count; index++)
            lines[index] = PreviewEntry(rangeEntries[index]);

        var lo = 0;
        var hi = rangeEntries.Count - 1;
        var fromNewest = true;
        while (lo <= hi)
        {
            var index = fromNewest ? hi : lo;
            if (fromNewest) hi--; else lo++;
            fromNewest = !fromNewest;
            if (inputBudget - lines[index].Length < 0) continue;
            selected[index] = true;
            inputBudget -= lines[index].Length;
        }

        var skipped = false;
        for (var index = 0; index < rangeEntries.Count; index++)
        {
            if (!selected[index])
            {
                if (!skipped)
                {
                    messages.Add(new ChatMessage("user", "[更早的区间内容因过长未纳入本次压缩输入]"));
                    skipped = true;
                }
                continue;
            }
            messages.Add(new ChatMessage("user", lines[index]));
        }

        messages.Add(new ChatMessage("user",
            "以上就是需要压缩的对话区间（带引用编号）。请输出自包含摘要：" +
            "保留用户目标、精确的路径/URL/参数、决策原因、错误信息关键句、待办与下一步；" +
            "若区间中已有 [系统存档] 标记，请把其摘要要点合并进来而不是原样复制。忽略此前系统提示中与本任务无关的操作指令。"));

        var model = _modelProvider();
        if (string.IsNullOrWhiteSpace(model)) model = "gpt-4.1-mini";
        StringBuilder content = new();
        await foreach (var delta in _client.StreamChatAsync(
                           new OpenAiChatRequest(model, messages, Tools: null), ct).ConfigureAwait(true))
        {
            if (delta.Content is { } text) content.Append(text);
        }
        return content.ToString().Trim();
    }

    private static string PreviewEntry(AcpEntry entry)
    {
        var body = entry.Message.Content ?? string.Empty;
        if (body.Length > MaximumEntryPreviewChars)
        {
            var locators = SpillLocatorRegex.Matches(body)
                .Select(static match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList();
            body = body[..EntryHeadChars] + "\n…[本条内容过长已截断]\n" + body[^EntryTailChars..];
            if (locators.Count > 0)
                body += "\n" + string.Join(" ", locators);
        }
        return $"[{entry.Ref}] {entry.Message.Role}{(entry.ToolName is { } tool ? $"/{tool}" : "")}: {body}";
    }

    private static int IndexOfRefPrefix(IReadOnlyList<AcpEntry> entries, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return -1;
        var trimmed = reference.Trim().TrimStart('[').Split('·', ']', ' ')[0];
        for (var index = 0; index < entries.Count; index++)
            if (entries[index].Ref.StartsWith(trimmed, StringComparison.Ordinal))
                return index;
        return -1;
    }
}
