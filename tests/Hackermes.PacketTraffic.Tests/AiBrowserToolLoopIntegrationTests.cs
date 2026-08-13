using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.ViewModels;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Execution;
using Hackermes.Automation.Recording;
using Hackermes.Automation.Timeline;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AiBrowserToolLoopIntegrationTests
{
    [Fact]
    public async Task Assistant_executes_an_approved_browser_tool_and_returns_its_result_to_the_model()
    {
        const string pageId = "page-authorized-target";
        var session = new RecordingCdpSession(pageId);
        var commands = CreateCommands(new SingleSessionRegistry(session));
        var tools = new AiToolRegistry();
        new CommandToolAdapter(commands).RegisterAll(tools);
        var confirmation = new RecordingConfirmation();
        var client = new TwoRoundChatClient();
        var events = new EventBus();
        using var viewModel = new AiChatViewModel(
            client,
            tools,
            new AiToolDispatcher(tools, new DefaultToolPolicyGate(), confirmation),
            events,
            new TestSettings(),
            new EmptySkillStore(),
            new InMemoryAgentMemoryStore(),
            new AgentContextCompactor());
        events.Publish(new ActiveContentTabChangedEvent(pageId, "Authorized target"));
        viewModel.Input = "Click the submit button on the current authorized page.";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(1, confirmation.Count);
        Assert.Equal(pageId, confirmation.Invocation?.PageId);
        Assert.Contains("final: submit button clicked", viewModel.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(3, session.Calls.Count(call => call.Method == "Input.dispatchMouseEvent"));
        Assert.All(session.Calls, call => Assert.Equal(pageId, call.PageId));

        var followUp = client.Requests[1];
        var assistantCall = Assert.Single(followUp.Messages, message => message.ToolCalls is { Count: > 0 });
        var call = Assert.Single(assistantCall.ToolCalls!);
        Assert.Equal("call-click", call.Id);
        Assert.Equal("page_click", call.Name);
        var toolResult = Assert.Single(followUp.Messages, message => message.Role == "tool");
        Assert.Equal("call-click", toolResult.ToolCallId);
        Assert.Contains("#submit", toolResult.Content, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(toolResult.Content));
    }

    private static CommandRegistry CreateCommands(ICdpSessionRegistry sessions)
    {
        var logger = new NullLogger();
        var timeline = new ActionTimelineStore();
        var executor = new ActionExecutor(sessions, logger, timeline);
        return new CommandRegistry(
            executor,
            new ActionRecorder(new EventBus(), executor, timeline),
            logger,
            timeline,
            new ActionPersistence());
    }

    private sealed class TwoRoundChatClient : IOpenAiChatClient
    {
        public List<OpenAiChatRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            if (Requests.Count == 1)
            {
                yield return new ChatStreamDelta(
                    null,
                    new ToolCallDelta(0, "call-click", "page_click", "{\"arguments\":\"#submit\"}"),
                    null);
                yield return new ChatStreamDelta(null, null, "tool_calls");
                yield break;
            }

            yield return new ChatStreamDelta("final: submit button clicked", null, "stop");
        }
    }

    private sealed class RecordingConfirmation : IToolConfirmationService
    {
        public int Count { get; private set; }
        public ToolInvocation? Invocation { get; private set; }

        public ValueTask<ToolConfirmation> ConfirmAsync(
            ToolInvocation invocation,
            string reason,
            CancellationToken ct)
        {
            Count++;
            Invocation = invocation;
            return ValueTask.FromResult(new ToolConfirmation(true));
        }
    }

    private sealed class RecordingCdpSession(string pageId) : ICdpSession
    {
        public string PageId { get; } = pageId;
        public bool IsAlive => true;
        public List<CdpCall> Calls { get; } = [];

        public Task<string> SendAsync(
            string method,
            string? parametersJson = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CdpCall(PageId, method, parametersJson));
            if (method != "Runtime.evaluate") return Task.FromResult("{\"result\":{}}");

            var probe = JsonSerializer.Serialize(new
            {
                found = true,
                x = 120.0,
                y = 48.0,
                visible = true,
                interactable = true,
                disabled = false,
                covered = false,
                inViewport = true,
                tag = "BUTTON"
            });
            return Task.FromResult(JsonSerializer.Serialize(new { result = new { value = probe } }));
        }

        public Task<IDisposable> SubscribeAsync(
            string eventName,
            Action<CdpEventArgs> handler,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(new Subscription());

        public Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public sealed record CdpCall(string PageId, string Method, string? ParametersJson);
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class SingleSessionRegistry(ICdpSession session) : ICdpSessionRegistry
    {
        public IReadOnlyList<ICdpSession> All { get; } = [session];
        public event Action<ICdpSession>? SessionOpened { add { } remove { } }
        public event Action<string>? SessionClosed { add { } remove { } }
        public ICdpSession? Get(string pageId) => pageId == session.PageId ? session : null;
        public IDisposable Register(ICdpSession value) => throw new NotSupportedException();
    }

    private sealed class TestSettings : ISettingsService
    {
        private readonly AppSettings _settings = new()
        {
            Ai = new AiSettings
            {
                MaxToolRounds = 4,
                MaxContextCharacters = 24_000,
                MaxRecentMessages = 16,
                MemoryEnabled = false
            }
        };

        public AppSettings Load() => _settings;
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null)
        {
            mutate(_settings);
            return true;
        }
        public string SettingsFilePath => "test-settings.json";
    }

    private sealed class EmptySkillStore : IAgentSkillStore
    {
        public IReadOnlyList<AgentSkill> Snapshot() => [];
        public AgentSkill Upsert(AgentSkill skill) => skill;
        public bool Remove(string id) => false;
    }

    private sealed class InMemoryAgentMemoryStore : IAgentMemoryStore
    {
        private AgentMemoryDocument _document = new();
        public AgentMemoryDocument Load() => _document;
        public void SaveConversation(string summary, IReadOnlyList<AgentMemoryMessage> recentMessages) =>
            _document = new AgentMemoryDocument
            {
                Summary = summary,
                RecentMessages = [.. recentMessages]
            };
        public void SetNotes(string notes) => _document.Notes = notes;
        public void Clear() => _document = new AgentMemoryDocument();
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
