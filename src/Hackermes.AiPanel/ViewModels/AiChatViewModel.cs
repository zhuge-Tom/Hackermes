using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Events;
using Hackermes.Base.Mvvm;
using Hackermes.Platform.Events;
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
}

public partial class AiChatViewModel : ViewModelBase
{
    private readonly IOpenAiChatClient _client;
    private readonly IAiToolRegistry _tools;
    private readonly AiToolDispatcher _dispatcher;
    private readonly int _maxToolRounds;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly List<ChatMessage> _history = [];
    private CancellationTokenSource? _request;

    public AiChatViewModel(
        IOpenAiChatClient client,
        IAiToolRegistry tools,
        AiToolDispatcher dispatcher,
        IEventBus eventBus,
        int maxToolRounds = 12)
    {
        _client = client;
        _tools = tools;
        _dispatcher = dispatcher;
        _maxToolRounds = Math.Clamp(maxToolRounds, 1, 64);
        SubscribeEvent<ActiveContentTabChangedEvent>(eventBus, e =>
            ActivePageId = e.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal) ? id : null);
        Messages.Add(new AiChatLine("assistant", "你好，我可以结合当前页面帮助定位问题。"));
    }

    public ObservableCollection<AiChatLine> Messages { get; } = [];
    public string? ActivePageId { get; private set; }
    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private string _model = "gpt-4.1-mini";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsBusy) return;
        Input = string.Empty;
        Error = null;
        Messages.Add(new AiChatLine("user", text));
        _history.Add(new ChatMessage("user", text));
        var answer = new AiChatLine("assistant", string.Empty);
        Messages.Add(answer);
        IsBusy = true;
        _request = new CancellationTokenSource();

        try
        {
            var pageId = ActivePageId;
            await RunToolLoopAsync(_history, answer, pageId, _request.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { answer.Content += "\n（已停止）"; }
        catch (Exception ex) { Error = ex.Message; answer.Content = "请求失败。"; }
        finally { _request.Dispose(); _request = null; IsBusy = false; }
    }

    [RelayCommand]
    private void Stop() => _request?.Cancel();

    private async Task RunToolLoopAsync(
        List<ChatMessage> history, AiChatLine answer, string? pageId, CancellationToken ct)
    {
        for (var round = 0; round < _maxToolRounds; round++)
        {
            var content = new StringBuilder();
            var calls = new Dictionary<int, ToolCallBuilder>();
            var request = new OpenAiChatRequest(Model, history, _tools.All);

            await foreach (var delta in _client.StreamChatAsync(request, ct).ConfigureAwait(true))
            {
                if (delta.Content is { } text)
                {
                    content.Append(text);
                    answer.Content += text;
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
                return;
            }

            var toolCalls = calls.OrderBy(pair => pair.Key).Select(pair => pair.Value.Build(pair.Key)).ToArray();
            history.Add(new ChatMessage("assistant", content.Length == 0 ? null : content.ToString(), ToolCalls: toolCalls));

            foreach (var call in toolCalls)
            {
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
                answer.Content += $"\n\n`{call.Name}` {(result.Success ? "✓" : "✗")}";
            }
        }

        answer.Content += $"\n\n（已达到 {_maxToolRounds} 轮工具调用上限）";
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
