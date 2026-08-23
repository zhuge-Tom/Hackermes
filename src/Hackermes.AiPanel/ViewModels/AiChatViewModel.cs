using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.Agent;
using Hackermes.Base.Events;
using Hackermes.Base.Mvvm;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.ViewModels;

public partial class AiChatLine : ObservableObject
{
    public AiChatLine(string role, string content) { Role = role; Content = content; }
    public string Role { get; }
    [ObservableProperty] private string _content;

    /// <summary>Localized role label for display ("user"/"assistant" stay internal).</summary>
    public string RoleLabel => Role switch
    {
        "user" => "用户",
        "assistant" => "助手",
        _ => Role
    };
}

/// <summary>Status of one Agent tool invocation shown as a compact transcript row.</summary>
public enum AiToolCallStatus { Running, Success, Failed }

/// <summary>
/// OpenCode-style compact tool-call row: status glyph, tool name, short argument digest and
/// duration on a single dimmed line; full arguments plus result stay collapsed until expanded.
/// Failed calls expand automatically so problems surface without hunting. Derives from
/// <see cref="AiChatLine"/> so mixed transcript collections stay strongly typed.
/// </summary>
public partial class AiToolCallLine : AiChatLine
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public AiToolCallLine(string toolName, string summary) : base("tool", string.Empty)
    {
        ToolName = toolName;
        _summary = summary;
    }

    public string ToolName { get; }

    [ObservableProperty] private string _summary;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _detail = string.Empty;

    public AiToolCallStatus Status { get; private set; } = AiToolCallStatus.Running;

    public bool IsRunning => Status == AiToolCallStatus.Running;
    public bool IsSuccess => Status == AiToolCallStatus.Success;
    public bool IsFailed => Status == AiToolCallStatus.Failed;

    public string DurationLabel { get; private set; } = string.Empty;

    public void Complete(bool success, string detail)
    {
        Status = success ? AiToolCallStatus.Success : AiToolCallStatus.Failed;
        DurationLabel = $"{_clock.Elapsed.TotalSeconds:0.#}s";
        Detail = detail;
        IsExpanded = !success;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(DurationLabel));
    }
}

/// <summary>Selectable entry in the chat session picker.</summary>
public partial class AgentSessionOption : ObservableObject
{
    public AgentSessionOption(string id, string name, DateTimeOffset updatedAt)
    {
        Id = id;
        _name = name;
        UpdatedAt = updatedAt;
    }

    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private DateTimeOffset _updatedAt;

    public void Touch(DateTimeOffset updatedAt, string? name)
    {
        UpdatedAt = updatedAt;
        if (!string.IsNullOrWhiteSpace(name)) Name = name;
    }

    public override string ToString() => Name;
}

public partial class AiChatViewModel : ViewModelBase
{
    private const string LegacyWelcomeMessage = "你好，我可以结合当前页面帮助定位问题。";
    private readonly IOpenAiChatClient _client;
    private readonly IAiToolRegistry _tools;
    private readonly AiToolDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly IAgentSkillStore _skills;
    private readonly IAgentMemoryStore _memory;
    private readonly AgentContextCompactor _context;
    private readonly IAgentSessionStore? _sessionStore;
    private readonly AcpContextRegistry? _acpRegistry;
    private AcpContextStore? _acp;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private List<ChatMessage> _history = [];
    private string _summary = string.Empty;
    private CancellationTokenSource? _request;

    public AiChatViewModel(
        IOpenAiChatClient client,
        IAiToolRegistry tools,
        AiToolDispatcher dispatcher,
        IEventBus eventBus,
        ISettingsService settings,
        IAgentSkillStore skills,
        IAgentMemoryStore memory,
        AgentContextCompactor context,
        IAgentSessionStore? sessions = null,
        AcpContextRegistry? acpRegistry = null)
    {
        _client = client;
        _tools = tools;
        _dispatcher = dispatcher;
        _settings = settings;
        _skills = skills;
        _memory = memory;
        _context = context;
        _sessionStore = sessions;
        _acpRegistry = acpRegistry;
        SubscribeEvent<ActiveContentTabChangedEvent>(eventBus, UpdateActivePage);
        SubscribeEvent<UpdateDockTabTitleEvent>(eventBus, UpdateActivePageTitle);
        RestoreSession();
    }

    public ObservableCollection<AiChatLine> Messages { get; } = [];
    public ObservableCollection<AgentSessionOption> Sessions { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePage))]
    [NotifyPropertyChangedFor(nameof(ActivePageLabel))]
    private string? _activePageId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePageLabel))]
    private string? _activePageTitle;

    public bool HasActivePage => ActivePageId is not null;
    public string ActivePageLabel => string.IsNullOrWhiteSpace(ActivePageTitle)
        ? ActivePageId ?? string.Empty
        : ActivePageTitle;

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private string _model = "gpt-4.1-mini";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _tokenUsage;
    /// <summary>One-line ACP context usage shown next to the token counter.</summary>
    [ObservableProperty] private string? _contextUsage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionLabel))]
    private AgentSessionOption? _selectedSession;

    public bool HasSessions => Sessions.Count > 0;
    public string SessionLabel => SelectedSession?.Name ?? "当前会话";

    /// <summary>
    /// Assigns the selection without switch semantics. Construction, restore and the
    /// new-session command use this because the target session's stored state does not
    /// exist yet or was just loaded; going through the property would persist/reload mid-flight.
    /// </summary>
    private void SetSelectedSessionSilently(AgentSessionOption? value)
    {
#pragma warning disable MVVMTK0034
        _selectedSession = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedSession));
        OnPropertyChanged(nameof(SessionLabel));
    }

    partial void OnSelectedSessionChanged(AgentSessionOption? value)
    {
        // User-initiated switches come through the generated setter; construction and the
        // new-session command assign the backing field directly to bypass this handler.
        if (IsBusy)
        {
            // Reject mid-request switches and snap the picker back to the live session.
            SetSelectedSessionSilently(Sessions.FirstOrDefault(option => option.Id == _sessionId));
            return;
        }
        if (value is null || value.Id == _sessionId) return;
        SwitchSession(value);
    }

    /// <summary>Starts a fresh named chat session; the previous one stays selectable in the session list.</summary>
    [RelayCommand]
    private void NewSession(string? name)
    {
        if (IsBusy) return;
        PersistSession();
        _history = [];
        _summary = string.Empty;
        ResetAcpStore();
        Messages.Clear();
        Error = null;
        TokenUsage = null;
        SessionPromptTokens = 0;
        SessionCompletionTokens = 0;
        _sessionId = Guid.NewGuid().ToString("N");
        var cleanName = string.IsNullOrWhiteSpace(name)
            ? $"新会话 {DateTimeOffset.Now:MM-dd HH:mm}"
            : name.Trim()[..Math.Min(name.Trim().Length, 120)];
        var option = new AgentSessionOption(_sessionId, cleanName, DateTimeOffset.Now);
        AddSessionOption(option);
        SetSelectedSessionSilently(option);
        PersistSession();
    }

    /// <summary>Renames a persisted session and refreshes its picker entry.</summary>
    public void RenameSession(string sessionId, string name)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;
        var clean = trimmed[..Math.Min(trimmed.Length, 120)];
        if (_sessionStore is not null)
        {
            try
            {
                var document = _sessionStore.Load();
                var entry = document.Sessions.FirstOrDefault(value => value.Id == sessionId);
                if (entry is not null)
                {
                    entry.Name = clean;
                    entry.UpdatedAt = DateTimeOffset.UtcNow;
                    _sessionStore.Save(document);
                }
            }
            catch (Exception exception) { Error = $"会话重命名失败：{exception.Message}"; }
        }
        Sessions.FirstOrDefault(value => value.Id == sessionId)?.Touch(DateTimeOffset.UtcNow, clean);
    }

    private void SwitchSession(AgentSessionOption target)
    {
        PersistSession();
        _sessionId = target.Id;
        _history = [];
        _summary = string.Empty;
        ResetAcpStore();
        Messages.Clear();
        Error = null;
        RestoreCurrentSession();
    }

    /// <summary>Cumulative prompt/completion tokens reported by the provider for this session.</summary>
    public int SessionPromptTokens { get; private set; }
    public int SessionCompletionTokens { get; private set; }

    private void UpdateActivePage(ActiveContentTabChangedEvent message)
    {
        var pageId = message.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal)
            ? id
            : null;

        ActivePageId = pageId;
        ActivePageTitle = pageId is null || string.IsNullOrWhiteSpace(message.Title)
            ? null
            : message.Title.Trim();
    }

    private void UpdateActivePageTitle(UpdateDockTabTitleEvent message)
    {
        if (!string.Equals(ActivePageId, message.TabId, StringComparison.Ordinal)) return;
        ActivePageTitle = string.IsNullOrWhiteSpace(message.Title) ? ActivePageId : message.Title.Trim();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsBusy) return;
        Input = string.Empty;
        Error = null;
        Messages.Add(new AiChatLine("user", text));
        _history.Add(new ChatMessage("user", text));
        EnsureAcpStore(_settings.Load().Ai)?.AppendUser(text);
        var answer = new AiChatLine("assistant", string.Empty);
        Messages.Add(answer);
        IsBusy = true;
        _request = new CancellationTokenSource();

        try
        {
            var pageId = ActivePageId;
            await RunToolLoopAsync(_history, answer, pageId, _request.Token).ConfigureAwait(true);
            // A turn that only invoked tools streams no prose; drop the leftover empty bubble.
            if (answer.Content.Length == 0) Messages.Remove(answer);
        }
        catch (OperationCanceledException) { answer.Content += "\n（已停止）"; }
        catch (Exception ex) { Error = ex.Message; answer.Content = "请求失败。"; }
        finally
        {
            PersistMemory();
            PersistSession();
            _request.Dispose(); _request = null; IsBusy = false;
        }
    }

    [RelayCommand]
    private void Stop() => _request?.Cancel();

    private async Task RunToolLoopAsync(
        List<ChatMessage> history, AiChatLine answer, string? pageId, CancellationToken ct)
    {
        var aiSettings = _settings.Load().Ai;
        var acp = EnsureAcpStore(aiSettings);
        var maxToolRounds = Math.Clamp(aiSettings.MaxToolRounds, 1, 256);
        for (var round = 0; round < maxToolRounds; round++)
        {
            var content = new StringBuilder();
            var calls = new Dictionary<int, ToolCallBuilder>();
            // Session summary is per chat session; operator notes stay global across sessions.
            var memory = new AgentMemoryDocument { Summary = _summary, Notes = _memory.Load().Notes };
            // ACP owns request assembly when active; the legacy compactor is bypassed so
            // exactly one context manager runs per session (opencode-acp rule).
            var messages = acp is not null
                ? acp.BuildRequest(memory, _skills.Snapshot(), aiSettings)
                : _context.BuildRequest(_history, memory, _skills.Snapshot(), aiSettings);
            ContextUsage = acp?.UsageLine(aiSettings.MaxContextCharacters);
            var request = new OpenAiChatRequest(Model, messages, AvailableTools());

            await foreach (var delta in _client.StreamChatAsync(request, ct).ConfigureAwait(true))
            {
                if (delta.Content is { } text)
                {
                    content.Append(text);
                    answer.Content += text;
                }
                if (delta.Usage is { } usage)
                {
                    SessionPromptTokens += usage.PromptTokens;
                    SessionCompletionTokens += usage.CompletionTokens;
                    TokenUsage = $"↑{SessionPromptTokens} ↓{SessionCompletionTokens} tokens";
                }
                if (delta.ToolCall is { } part)
                {
                    if (!calls.TryGetValue(part.Index, out var call))
                        calls[part.Index] = call = new ToolCallBuilder();
                    if (!string.IsNullOrEmpty(part.Id)) call.Id = part.Id;
                    if (!string.IsNullOrEmpty(part.Name)) call.Name.Append(part.Name);
                    if (!string.IsNullOrEmpty(part.Arguments)) call.Arguments.Append(part.Arguments);
                }
            }

            if (calls.Count == 0)
            {
                if (answer.Content.Length == 0) answer.Content = "（模型未返回内容）";
                history.Add(new ChatMessage("assistant", content.ToString()));
                acp?.AppendAssistant(content.ToString());
                return;
            }

            var toolCalls = calls.OrderBy(pair => pair.Key).Select(pair => pair.Value.Build(pair.Key)).ToArray();
            history.Add(new ChatMessage("assistant", content.Length == 0 ? null : content.ToString(), ToolCalls: toolCalls));
            acp?.AppendAssistantToolCalls(content.Length == 0 ? null : content.ToString(), toolCalls);

            foreach (var call in toolCalls)
            {
                // Keep the streaming assistant reply pinned to the bottom of the transcript;
                // tool rows stack above it in execution order (OpenCode-style grouping).
                var toolLine = new AiToolCallLine(call.Name, SummarizeArguments(call.Arguments));
                var answerIndex = Messages.IndexOf(answer);
                if (answerIndex >= 0) Messages.Insert(answerIndex, toolLine); else Messages.Add(toolLine);
                ToolResult result;
                try
                {
                    using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
                    result = await _dispatcher.InvokeAsync(new ToolInvocation(
                        call.Name, args.RootElement.Clone(), pageId, _sessionId), ct).ConfigureAwait(true);
                }
                catch (JsonException ex)
                {
                    result = ToolResult.Fail("工具参数不是有效 JSON: " + ex.Message);
                }

                history.Add(new ChatMessage("tool", result.Content, ToolCallId: call.Id));
                acp?.AppendToolResult(call.Id, result.Content, call.Name);
                toolLine.Complete(result.Success, FormatToolDetail(call.Name, call.Arguments, result.Content));
            }
        }

        answer.Content += $"\n\n（已达到 {maxToolRounds} 轮工具调用上限）";
        history.Add(new ChatMessage("assistant", answer.Content));
    }

    private IReadOnlyList<AiToolDefinition> AvailableTools()
    {
        var active = _skills.Snapshot().Where(skill => skill.Enabled).ToArray();
        var listed = active.SelectMany(skill => skill.ToolNames).ToHashSet(StringComparer.Ordinal);
        if (listed.Count == 0) return _tools.All;

        // Workflow management remains available so an Agent can repair a restrictive workflow,
        // but any mutation still travels through the shared policy gate.
        string[] control = ["agent_skill_list", "agent_skill_upsert", "agent_skill_remove", "agent_memory_read", "agent_memory_write", "agent_memory_clear"];
        foreach (var tool in control) listed.Add(tool);
        // ACP context management must stay reachable even under restrictive workflows —
        // it is the session's only mechanism for reclaiming context.
        if (_acp is not null)
            foreach (var tool in (string[])["context_compress", "context_decompress", "context_search", "context_status"])
                listed.Add(tool);
        return _tools.All.Where(tool => listed.Contains(tool.Name)).ToArray();
    }

    private void RestoreSession()
    {
        var settings = _settings.Load().Ai;
        EnsureAcpStore(settings);
        if (_sessionStore is null)
        {
            // No session store (tests/headless): keep the legacy global-memory restore path.
            if (!settings.MemoryEnabled) return;
            RestoreFromDocument(_memory.Load(), settings);
            return;
        }

        var document = _sessionStore.Load();
        foreach (var entry in document.Sessions)
            AddSessionOption(new AgentSessionOption(entry.Id, entry.Name, entry.UpdatedAt));

        var activeId = document.ActiveId.Length > 0 ? document.ActiveId : document.Sessions.FirstOrDefault()?.Id;
        SetSelectedSessionSilently(Sessions.FirstOrDefault(option => option.Id == activeId) ?? Sessions.FirstOrDefault());
        var current = document.Sessions.FirstOrDefault(session => session.Id == (SelectedSession?.Id ?? string.Empty));
        if (current is not null)
        {
            _sessionId = current.Id;
            _summary = current.Summary;
            RestoreMessages(current.RecentMessages, settings);
            return;
        }

        // First run after upgrade: migrate the legacy global conversation into a named session.
        NewSessionInternal(settings);
    }

    /// <summary>Creates and activates an empty session without touching the UI command surface.</summary>
    private void NewSessionInternal(AiSettings settings)
    {
        _sessionId = Guid.NewGuid().ToString("N");
        var option = new AgentSessionOption(_sessionId, $"新会话 {DateTimeOffset.Now:MM-dd HH:mm}", DateTimeOffset.Now);
        AddSessionOption(option);
        SetSelectedSessionSilently(option);
        OnPropertyChanged(nameof(HasSessions));

        // Seed the first-ever session from the legacy global memory so nothing is silently lost.
        var legacy = settings.MemoryEnabled ? _memory.Load() : new AgentMemoryDocument();
        if (legacy.RecentMessages.Count > 0 || legacy.Summary.Length > 0)
        {
            _summary = legacy.Summary;
            RestoreMessages(legacy.RecentMessages, settings);
        }
        PersistSession();
    }

    private void RestoreCurrentSession()
    {
        var settings = _settings.Load().Ai;
        var entry = _sessionStore?.Load().Sessions.FirstOrDefault(session => string.Equals(session.Id, _sessionId, StringComparison.Ordinal));
        if (entry is null) return;
        _summary = entry.Summary;
        RestoreMessages(entry.RecentMessages, settings);
    }

    private void RestoreFromDocument(AgentMemoryDocument stored, AiSettings settings)
    {
        _summary = stored.Summary;
        RestoreMessages(stored.RecentMessages, settings);
    }

    private void RestoreMessages(IEnumerable<AgentMemoryMessage> messages, AiSettings settings)
    {
        foreach (var message in messages
                     .Where(message => !string.Equals(message.Content, LegacyWelcomeMessage, StringComparison.Ordinal))
                     .TakeLast(settings.MaxRecentMessages))
        {
            _history.Add(new ChatMessage(message.Role, message.Content));
            Messages.Add(new AiChatLine(message.Role, message.Content));
            if (_acp is { } store)
            {
                if (message.Role == "user") store.AppendUser(message.Content);
                else if (message.Role == "assistant") store.AppendAssistant(message.Content);
            }
        }
    }

    /// <summary>
    /// Creates (or reuses) the per-session ACP store and publishes it to the shared tool
    /// registry bridge. Returns null when ACP is disabled or no registry was supplied.
    /// </summary>
    private AcpContextStore? EnsureAcpStore(AiSettings settings)
    {
        if (!settings.AcpEnabled || _acpRegistry is null) return null;
        if (_acp is null)
        {
            _acp = new AcpContextStore(() => AgentContextCompactor.BuildSystemMessage(
                new AgentMemoryDocument { Summary = _summary, Notes = _memory.Load().Notes },
                _skills.Snapshot(), _settings.Load().Ai), settings.MaxContextCharacters);
        }
        _acpRegistry.Current = _acp;
        return _acp;
    }

    private void ResetAcpStore()
    {
        _acp = null;
        if (_acpRegistry is not null) _acpRegistry.Current = null;
    }

    private void PersistMemory()
    {
        var settings = _settings.Load().Ai;
        if (!settings.MemoryEnabled) return;
        if (_acp is null) _summary = _context.CompactCompletedTurns(_history, _summary, settings);
        var recent = RecentForPersistence(settings);
        _memory.SaveConversation(_summary, recent);
    }

    /// <summary>Writes the active session's compacted state into the persistent session store.</summary>
    private void PersistSession()
    {
        if (_sessionStore is null) return;
        try
        {
            var settings = _settings.Load().Ai;
            // ACP active: it owns context management, so legacy turn compaction is skipped.
            if (_acp is null) _summary = _context.CompactCompletedTurns(_history, _summary, settings);
            var recent = RecentForPersistence(settings);
            var document = _sessionStore.Load();
            var entry = document.Sessions.FirstOrDefault(value => string.Equals(value.Id, _sessionId, StringComparison.Ordinal));
            if (entry is null)
            {
                entry = new AgentSessionEntry { Id = _sessionId, CreatedAt = DateTimeOffset.UtcNow };
                document.Sessions.Add(entry);
            }
            entry.Name = SelectedSession?.Name ?? $"会话 {entry.CreatedAt:MM-dd HH:mm}";
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.Summary = _summary;
            entry.RecentMessages = recent.Select(message => new AgentMemoryMessage { Role = message.Role, Content = message.Content }).ToList();
            document.ActiveId = _sessionId;
            _sessionStore.Save(document);

            Sessions.FirstOrDefault(value => value.Id == _sessionId)?.Touch(entry.UpdatedAt, entry.Name);
        }
        catch (Exception exception)
        {
            Error = $"会话保存失败：{exception.Message}";
        }
    }

    private AgentMemoryMessage[] RecentForPersistence(AiSettings settings) =>
        _history.Where(message => message.Role is "user" or "assistant")
            .Where(message => message.ToolCalls is null || message.ToolCalls.Count == 0)
            .Select(message => new AgentMemoryMessage { Role = message.Role, Content = message.Content ?? string.Empty })
            .Where(message => message.Content.Length > 0)
            .TakeLast(settings.MaxRecentMessages).ToArray();

    private void AddSessionOption(AgentSessionOption option)
    {
        if (Sessions.Any(value => value.Id == option.Id)) return;
        Sessions.Insert(0, option);
        OnPropertyChanged(nameof(HasSessions));
    }

    /// <summary>Compact single-line digest of a tool call's arguments for the transcript row.</summary>
    private static string SummarizeArguments(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Shorten(doc.RootElement.ToString(), 80);
            var parts = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                var text = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Array => $"{property.Value.GetArrayLength()} 项",
                    JsonValueKind.Object => "{…}",
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(text)) continue;
                parts.Add($"{property.Name}={Shorten(text!, 42)}");
                if (parts.Count == 4) break;
            }
            var joined = string.Join("  ", parts);
            return joined.Length == 0 ? string.Empty : Shorten(joined, 96);
        }
        catch (JsonException)
        {
            return Shorten(argumentsJson ?? string.Empty, 80);
        }
    }

    /// <summary>Collapsed-by-default detail body: pretty-printed arguments plus the bounded result.</summary>
    private static string FormatToolDetail(string toolName, string argumentsJson, string resultContent)
    {
        const int MaximumResultCharacters = 6_000;
        const int MaximumArgumentCharacters = 2_000;
        var args = TryPrettyJson(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (args.Length > MaximumArgumentCharacters) args = args[..MaximumArgumentCharacters] + "\n…[参数过长已截断]";
        var result = resultContent ?? string.Empty;
        if (result.Length > MaximumResultCharacters)
            result = result[..MaximumResultCharacters] + $"\n…[结果共 {result.Length} 字符，仅显示前 {MaximumResultCharacters}]";
        return $"工具 {toolName}\n\n【参数】\n{args}\n\n【结果】\n{result}";
    }

    private static string TryPrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string Shorten(string value, int max)
    {
        var flat = value.Replace("\r", string.Empty).Replace('\n', ' ');
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();

        public AssistantToolCall Build(int index) => new(
            Id ?? $"call_{index}_{Guid.NewGuid():N}", Name.ToString(), Arguments.ToString());
    }
}
