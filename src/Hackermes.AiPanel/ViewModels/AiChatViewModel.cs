using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.Agent;
using Hackermes.Base.Events;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Mvvm;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.ViewModels;

public partial class AiChatLine : ObservableObject
{
    public AiChatLine(string role, string content, string? displayLabel = null)
    {
        Role = role;
        Content = content;
        DisplayLabel = displayLabel;
    }
    public string Role { get; }
    [ObservableProperty] private string _content;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleLabel))]
    private string? _displayLabel;

    /// <summary>Localized role label for display ("user"/"assistant" stay internal).</summary>
    public string RoleLabel => !string.IsNullOrWhiteSpace(DisplayLabel)
        ? DisplayLabel!
        : Role switch
        {
            "user" => "用户",
            "assistant" => "助手",
            _ => Role
        };
}

/// <summary>
/// Reasoning-model thinking stream row: visually quieter than prose (dimmer, accent
/// border) so long chains-of-thought stay readable without competing with answers.
/// </summary>
public partial class AiReasoningLine : AiChatLine
{
    public AiReasoningLine() : base("assistant", string.Empty, "思考") { }
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

/// <summary>
/// Chat surface over the headless <see cref="AgentTurnRunner"/>. The runner owns the
/// turn/step loop, the steering inbox and an append-only event log; this view model only
/// projects <see cref="AgentSessionLog.Appended"/> events into transcript rows, so output
/// structure (turns, steps, tool protocol, retries, auto-compaction) is rendered from facts
/// instead of being interleaved with control flow (deepseek-harness session-event lineage).
/// </summary>
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
    private readonly AgentTodoRegistry? _todos;
    private readonly AgentGoalRegistry _goals;
    private readonly IAppLogger? _logger;
    private readonly AcpAutoCompactor _autoCompactor;
    private readonly AgentEventLogStore? _eventLogStore;
    private AcpContextStore? _acp;
    private AgentTurnRunner _runner = null!;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private string _summary = string.Empty;
    private CancellationTokenSource? _request;
    /// <summary>Suppresses token counter accumulation while projecting a restored log.</summary>
    private bool _restoring;

    /// <summary>Lazily created assistant row receiving stream deltas for the current step.</summary>
    private AiChatLine? _streamingAssistantLine;
    private AiChatLine? _streamingReasoningLine;
    private int _streamingAssistantStep = -1;
    private readonly Dictionary<string, (AiToolCallLine Line, string Arguments)> _openToolCalls = [];

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
        AcpContextRegistry? acpRegistry = null,
        IAppLogger? logger = null,
        AgentTodoRegistry? todos = null,
        AgentGoalRegistry? goals = null)
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
        _todos = todos;
        _goals = goals ?? new AgentGoalRegistry();
        // Created lazily per event so runtime toggles of ai.sessionEvents take effect
        // without an app restart; a write failure surfaces once, then pauses persistence.
        var settingsDirectory = Path.GetDirectoryName(settings.SettingsFilePath);
        _eventLogStore = new AgentEventLogStore(() => settingsDirectory ?? AppContext.BaseDirectory, logger);
        if (!string.IsNullOrEmpty(settingsDirectory))
            _eventLogStore.WriteFailed += message =>
                Error = $"会话事件日志写入失败，本会话已暂停持久化（历史消息不受影响）：{message}";
        _logger = logger?.ForCategory(nameof(AiChatViewModel));
        _autoCompactor = new AcpAutoCompactor(
            client, () => Model, () => _acp, () => settings.Load().Ai, _logger,
            prefixProvider: ProvideCompactionPrefix);
        if (_todos is not null)
        {
            _todos.Changed += OnTodosChanged;
            OnTodosChanged(_todos.Current);
        }
        CreateRunner();
        SubscribeEvent<ActiveContentTabChangedEvent>(eventBus, UpdateActivePage);
        SubscribeEvent<UpdateDockTabTitleEvent>(eventBus, UpdateActivePageTitle);
        RestoreSession();
    }

    public ObservableCollection<AiChatLine> Messages { get; } = [];
    public ObservableCollection<AgentSessionOption> Sessions { get; } = [];
    public ObservableCollection<string> Todos { get; } = [];

    [ObservableProperty] private string? _todoSummary;
    public bool HasTodos => Todos.Count > 0;

    private void OnTodosChanged(IReadOnlyList<AgentTodoItem> items)
    {
        Todos.Clear();
        foreach (var item in items)
        {
            var glyph = item.Status switch
            {
                AgentTodoStatus.Completed => "✔",
                AgentTodoStatus.InProgress => "◐",
                _ => "○",
            };
            Todos.Add($"{glyph} {item.Content}");
        }
        TodoSummary = items.Count == 0 ? null :
            $"任务清单 · 已完成 {items.Count(item => item.Status == AgentTodoStatus.Completed)} / {items.Count}";
        OnPropertyChanged(nameof(HasTodos));
    }
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendButtonLabel))]
    private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _tokenUsage;
    /// <summary>One-line ACP context usage shown next to the token counter.</summary>
    [ObservableProperty] private string? _contextUsage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingInstruction))]
    private string? _pendingInstructionSummary;
    [ObservableProperty] private string? _pendingInstructionHint;

    public bool HasPendingInstruction => _runner.PendingInstructionCount > 0;
    public string SendButtonLabel => IsBusy ? "追加" : "发送";

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

    /// <summary>
    /// Main-request prefix replayed in front of summarizer calls (dsh KV-cache alignment):
    /// identical system prompt and tool list make the auxiliary call share its longest
    /// prefix with recent traffic instead of invalidating provider caches.
    /// </summary>
    private CompactionPrefix? ProvideCompactionPrefix()
    {
        var ai = _settings.Load().Ai;
        var system = AgentContextCompactor.BuildSystemMessage(
            new AgentMemoryDocument { Summary = _summary, Notes = _memory.Load().Notes },
            _skills.Snapshot(), ai);
        return new CompactionPrefix(system, AvailableTools());
    }

    /// <summary>Builds a fresh runner (fresh log, history and steering inbox) for a new chat session.</summary>
    private void CreateRunner()
    {
        if (_runner is not null)
        {
            _runner.Log.Appended -= OnAgentEvent;
            _dispatcher.Audited -= OnToolAudited;
        }
        _runner = new AgentTurnRunner(
            _client,
            _dispatcher,
            () => _settings.Load().Ai,
            () => new AgentMemoryDocument { Summary = _summary, Notes = _memory.Load().Notes },
            () => _skills.Snapshot(),
            new AgentTurnRunnerOptions
            {
                ToolSelector = AvailableTools,
                AutoCompactor = _autoCompactor,
                TurnStarting = _todos is null ? null : () => _todos.BeginTurn(),
                Goals = _goals,
            },
            _logger,
            eventLogProvider: () => _settings.Load().Ai.SessionEventsEnabled ? _eventLogStore : null);
        _runner.Log.Appended += OnAgentEvent;
        _dispatcher.Audited += OnToolAudited;
    }

    private void OnToolAudited(AiToolAuditRecord record) => _runner.AppendAudit(record);

    /// <summary>Starts a fresh named chat session; the previous one stays selectable in the session list.</summary>
    [RelayCommand]
    private void NewSession(string? name)
    {
        if (IsBusy) return;
        PersistSession();
        _summary = string.Empty;
        ResetAcpStore();
        _goals.Clear();
        _todos?.BeginTurn(); // clears the previous checklist
        CreateRunner();
        Messages.Clear();
        Todos.Clear();
        TodoSummary = null;
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
        _summary = string.Empty;
        ResetAcpStore();
        _goals.Clear();
        _todos?.BeginTurn();
        CreateRunner();
        Messages.Clear();
        Todos.Clear();
        TodoSummary = null;
        Error = null;
        if (!TryRestoreFromEventLog()) RestoreCurrentSession();
    }

    /// <summary>
    /// Rebuilds transcript and model state from the persisted event log (resume, dsh
    /// log-as-truth lineage). Returns false when persistence is disabled or absent.
    /// </summary>
    private bool TryRestoreFromEventLog()
    {
        if (_eventLogStore is null || !_settings.Load().Ai.SessionEventsEnabled) return false;
        if (!_eventLogStore.Exists(_sessionId)) return false;
        var events = _eventLogStore.Load(_sessionId);
        if (events.Count == 0) return false;

        EnsureAcpStore(_settings.Load().Ai);
        _runner.Strategy = _acp is { } store
            ? new AcpContextStrategy(store)
            : new CompactorContextStrategy(_context);
        _runner.Replay(events);
        _restoring = true;
        try
        {
            foreach (var @event in events) OnAgentEvent(@event);
        }
        finally { _restoring = false; }
        return true;
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0) return;
        Input = string.Empty;
        if (IsBusy)
        {
            _runner.EnqueueInstruction(text);
            RefreshPendingInstructionState();
            return;
        }
        Error = null;
        EnsureAcpStore(_settings.Load().Ai);
        // Exactly one context manager runs per session: ACP owns request assembly when
        // enabled; otherwise the legacy compactor strategy is used.
        _runner.Strategy = _acp is { } store
            ? new AcpContextStrategy(store)
            : new CompactorContextStrategy(_context);
        IsBusy = true;
        _request = new CancellationTokenSource();

        try
        {
            await _runner.RunTurnAsync(text, Model, ActivePageId, _sessionId, _request.Token)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Defensive: RunTurnAsync reports its own failures via TurnEnd events.
            _logger?.Error("AI request failed.", ex);
            Error = ex.Message;
        }
        finally
        {
            PersistMemory();
            PersistSession();
            _request.Dispose(); _request = null; IsBusy = false;
            RefreshPendingInstructionState();
        }
    }

    [RelayCommand]
    private void Stop() => _request?.Cancel();

    [RelayCommand]
    private void PrioritizePendingInstruction()
    {
        if (_runner.PromoteLatestInstruction()) RefreshPendingInstructionState();
    }

    [RelayCommand]
    private void CancelPendingInstruction()
    {
        if (_runner.DropNextInstruction()) RefreshPendingInstructionState();
    }

    private void RefreshPendingInstructionState()
    {
        var count = _runner.PendingInstructionCount;
        if (count == 0)
        {
            PendingInstructionSummary = null;
            PendingInstructionHint = null;
        }
        else
        {
            PendingInstructionSummary = _runner.PeekNextInstruction();
            PendingInstructionHint = _runner.IsNextInstructionPriority()
                ? $"优先指示 · 将在当前协议安全收尾后转向（共 {count} 条）"
                : $"已排队 · 将在下一阶段执行（共 {count} 条）";
        }
        OnPropertyChanged(nameof(HasPendingInstruction));
    }

    #region Transcript projection from agent events

    private void OnAgentEvent(AgentSessionEvent evt)
    {
        switch (evt.Data)
        {
            case UserMessageReceived user:
                CloseStreamingAssistant();
                Messages.Add(new AiChatLine("user", user.Text,
                    user.Injected ? "上下文注入"
                        : user.Steered ? (user.Priority ? "追加指示 · 优先" : "追加指示")
                        : null));
                break;

            case AssistantDelta delta:
                SealReasoningLine();
                EnsureStreamingAssistant(evt.Step).Content += delta.Text;
                break;

            case ReasoningDelta reasoning:
                // Reasoning-model thinking stream: its own dimmed row, never model history.
                if (_streamingReasoningLine is null)
                {
                    _streamingReasoningLine = new AiReasoningLine();
                    Messages.Add(_streamingReasoningLine);
                }
                _streamingReasoningLine.Content += reasoning.Text;
                break;

            case AssistantReply reply:
                if (reply.HasToolCalls)
                {
                    // Preamble text (if any) stays as its own dimmed stage line ahead of the tool rows.
                    CloseStreamingAssistant();
                    break;
                }
                var finalLine = _streamingAssistantLine ?? EnsureStreamingAssistant(evt.Step);
                finalLine.Content = reply.Content.Length == 0 ? "（模型未返回内容）" : reply.Content;
                finalLine.DisplayLabel = reply.IsFinalReport ? "执行完成报告" : $"阶段 {evt.Step}";
                CloseStreamingAssistant();
                break;

            case ToolCallRequested call:
                CloseStreamingAssistant();
                var toolLine = new AiToolCallLine(call.Name, SummarizeArguments(call.ArgumentsJson));
                _openToolCalls[call.CallId] = (toolLine, call.ArgumentsJson);
                Messages.Add(toolLine);
                break;

            case ToolCallCompleted completed:
                if (_openToolCalls.Remove(completed.CallId, out var entry))
                    entry.Line.Complete(completed.Success,
                        FormatToolDetail(completed.Name, entry.Arguments, completed.Content));
                break;

            case UsageRecorded usage:
                if (!_restoring)
                {
                    SessionPromptTokens += usage.Usage.PromptTokens;
                    SessionCompletionTokens += usage.Usage.CompletionTokens;
                    TokenUsage = $"↑{SessionPromptTokens} ↓{SessionCompletionTokens} tokens";
                }
                break;

            case RequestRetried retry:
                CloseStreamingAssistant();
                Messages.Add(new AiChatLine("assistant",
                    $"请求暂时失败，正在重试（{retry.Attempt}/{retry.MaxAttempts}）：{Shorten(retry.Error, 120)}", "重试"));
                break;

            case ContextCompacted compacted:
                CloseStreamingAssistant();
                var compactedText =
                    $"已{(compacted.Automatic ? "自动" : string.Empty)}压缩 {compacted.Range}，活动上下文约减少 " +
                    $"{AcpContextStore.FormatSize(compacted.ReclaimedChars)} 字符；可用 context_search 检索归档内容。";
                if (!string.IsNullOrEmpty(compacted.Warning)) compactedText += "\n⚠️ " + compacted.Warning;
                Messages.Add(new AiChatLine("assistant", compactedText, "自动压缩"));
                break;

            case TurnEnded ended:
                CloseStreamingAssistant();
                _openToolCalls.Clear();
                switch (ended.Reason)
                {
                    case AgentTurnEndReason.Aborted:
                        Messages.Add(new AiChatLine("assistant", "任务已停止。", "已停止"));
                        break;
                    case AgentTurnEndReason.Error:
                        if (!string.IsNullOrEmpty(ended.Detail)) Error = ended.Detail;
                        Messages.Add(new AiChatLine("assistant", "请求失败。", "执行失败"));
                        break;
                    case AgentTurnEndReason.MaxRounds:
                        if (Messages.Count > 0 && Messages[^1].Role == "assistant")
                            Messages[^1].DisplayLabel = "执行结束";
                        break;
                    case AgentTurnEndReason.LengthCapped:
                        Messages.Add(new AiChatLine("assistant",
                            "已达到模型单次回复长度上限，本段回答被截断；可让模型继续输出或拆分任务。", "长度截断"));
                        break;
                }
                if (ended.Reason is AgentTurnEndReason.Completed or AgentTurnEndReason.LengthCapped)
                    MaybeAutoNameSession();
                break;
        }
    }

    private AiChatLine EnsureStreamingAssistant(int step)
    {
        if (_streamingAssistantLine is null || _streamingAssistantStep != step)
        {
            _streamingAssistantLine = new AiChatLine("assistant", string.Empty, $"阶段 {step}");
            _streamingAssistantStep = step;
            Messages.Add(_streamingAssistantLine);
        }
        return _streamingAssistantLine;
    }

    private void CloseStreamingAssistant()
    {
        _streamingAssistantLine = null;
        _streamingAssistantStep = -1;
        SealReasoningLine();
    }

    private void SealReasoningLine()
    {
        if (_streamingReasoningLine is null) return;
        if (_streamingReasoningLine.Content.Length == 0)
            _streamingReasoningLine.Content = "（无思考内容）";
        _streamingReasoningLine = null;
    }

    /// <summary>
    /// Names a default-named session after its first completed turn (dsh session-title
    /// lineage): an immediate truncation gives instant feedback, then a small LLM call
    /// refines the title in the background; failures silently keep the truncation.
    /// </summary>
    private void MaybeAutoNameSession()
    {
        if (!_settings.Load().Ai.AutoSessionNaming) return;
        if (Sessions.FirstOrDefault(option => option.Id == _sessionId) is not { } option) return;
        if (!option.Name.StartsWith("新会话", StringComparison.Ordinal)) return;
        var firstUser = _runner.History.FirstOrDefault(message => message.Role == "user")?.Content;
        if (string.IsNullOrWhiteSpace(firstUser)) return;

        RenameSession(_sessionId, Shorten(firstUser.Trim(), 18));

        var model = Model;
        var client = _client;
        _ = Task.Run(async () =>
        {
            var suggested = await AgentSessionTitleMaker.SuggestAsync(client, model, firstUser)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(suggested)) return;
            // Only refine while the session still carries the truncation name.
            if (Sessions.FirstOrDefault(option => option.Id == _sessionId)?.Name == Shorten(firstUser.Trim(), 18))
                RenameSession(_sessionId, suggested);
        });
    }

    /// <summary>
    /// Forks a persisted session into a fresh id with its full event stream — history,
    /// compaction blocks and audits resume intact while the source stays untouched.
    /// </summary>
    public bool ForkSession(string sourceSessionId)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(sourceSessionId)) return false;
        if (_eventLogStore is null || !_settings.Load().Ai.SessionEventsEnabled)
        {
            Error = "分叉需要开启会话事件持久化（ai.sessionEvents）。";
            return false;
        }
        if (!_eventLogStore.Exists(sourceSessionId))
        {
            Error = "该会话没有可分叉的事件记录。";
            return false;
        }
        PersistSession();
        var forkId = Guid.NewGuid().ToString("N");
        if (!_eventLogStore.Fork(sourceSessionId, forkId))
        {
            Error = "会话分叉失败：事件流复制出错。";
            return false;
        }
        var source = Sessions.FirstOrDefault(option => option.Id == sourceSessionId);
        var option = new AgentSessionOption(forkId, $"{source?.Name ?? "会话"} · 分叉", DateTimeOffset.Now);
        AddSessionOption(option);
        SetSelectedSessionSilently(option);
        SwitchSession(option);
        return true;
    }

    /// <summary>Markdown transcript of the live session's durable log (UI-agnostic builder).</summary>
    public string BuildTranscriptMarkdown() => AgentTranscriptExporter.BuildMarkdown(
        SelectedSession?.Name ?? SessionLabel,
        DateTimeOffset.Now,
        _runner.Log.Snapshot());

    /// <summary>Export is meaningful once the session has any durable events.</summary>
    public bool HasTranscript => _runner.Log.Count > 0;

    #endregion

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
            if (!TryRestoreFromEventLog()) RestoreMessages(current.RecentMessages, settings);
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
        var restored = new List<ChatMessage>();
        foreach (var message in messages
                     .Where(message => !string.Equals(message.Content, LegacyWelcomeMessage, StringComparison.Ordinal))
                     .TakeLast(settings.MaxRecentMessages))
        {
            restored.Add(new ChatMessage(message.Role, message.Content));
            Messages.Add(new AiChatLine(message.Role, message.Content));
            if (_acp is { } store)
            {
                if (message.Role == "user") store.AppendUser(message.Content);
                else if (message.Role == "assistant") store.AppendAssistant(message.Content);
            }
        }
        _runner.SeedHistory(restored);
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
            // Token budgeting swaps the store's unit estimator; everything downstream
            // (nudges, GC, auto-compaction, usage line) reads the same consistent unit.
            var budget = AcpContextStore.EffectiveBudget(settings);
            var estimate = settings.MaxContextTokens > 0
                ? (Func<string, int>)(content => AgentTokenMeter.EstimateTokens(content) + 24)
                : content => (content?.Length ?? 0) + AcpContextStore.LegacyEntryOverheadChars;
            _acp = new AcpContextStore(() => AgentContextCompactor.BuildSystemMessage(
                new AgentMemoryDocument { Summary = _summary, Notes = _memory.Load().Notes },
                _skills.Snapshot(), _settings.Load().Ai), budget, estimate);
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
        if (_acp is null) _summary = _context.CompactCompletedTurns(_runner.History, _summary, settings);
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
            if (_acp is null) _summary = _context.CompactCompletedTurns(_runner.History, _summary, settings);
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
        _runner.History.Where(message => message.Role is "user" or "assistant")
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

    protected override void OnDispose()
    {
        _runner.Log.Appended -= OnAgentEvent;
        _dispatcher.Audited -= OnToolAudited;
        if (_todos is not null) _todos.Changed -= OnTodosChanged;
        base.OnDispose();
    }
}
