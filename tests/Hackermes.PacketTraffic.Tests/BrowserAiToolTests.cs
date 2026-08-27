using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Execution;
using Hackermes.Automation.Recording;
using Hackermes.Automation.Timeline;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.Services;
using Hackermes.Browser.ViewModels;
using Hackermes.Cdp.Session;
using Hackermes.Inspector.Models;
using Hackermes.Inspector.Services;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BrowserAiToolTests
{
    [Fact]
    public void Assessment_cli_is_not_projected_as_generic_page_tool()
    {
        var commands = CreateCommands();
        commands.Register(new CommandDefinition
        {
            Name = "assessment",
            Summary = "authorized assessment CLI",
            Usage = "assessment <command>",
            IsMutating = true,
            Handler = (_, _) => Task.FromResult(CommandResult.Ok())
        });

        var tools = new AiToolRegistry();
        new CommandToolAdapter(commands).RegisterAll(tools);

        Assert.DoesNotContain(tools.All, tool => tool.Name == "page_assessment");
        Assert.Contains(tools.All, tool => tool.Name == "page_navigate");
    }

    [Fact]
    public void Identity_and_signing_keys_are_not_projected()
    {
        var commands = CreateCommands();
        commands.Register(new CommandDefinition
        {
            Name = "identity",
            Summary = "operator identity CLI",
            Usage = "identity list",
            IsMutating = true,
            Handler = (_, _) => Task.FromResult(CommandResult.Ok())
        });
        commands.Register(new CommandDefinition
        {
            Name = "signing-keys",
            Summary = "signing key CLI",
            Usage = "signing-keys list",
            IsMutating = true,
            Handler = (_, _) => Task.FromResult(CommandResult.Ok())
        });

        var tools = new AiToolRegistry();
        new CommandToolAdapter(commands).RegisterAll(tools);

        Assert.DoesNotContain(tools.All, tool => tool.Name == "page_identity");
        Assert.DoesNotContain(tools.All, tool => tool.Name == "page_signing_keys");
    }

    [Fact]
    public void Core_browser_tools_expose_typed_input_schemas()
    {
        var tools = new AiToolRegistry();
        new CommandToolAdapter(CreateCommands()).RegisterAll(tools);

        var navigate = tools.All.Single(tool => tool.Name == "page_navigate");
        var click = tools.All.Single(tool => tool.Name == "page_click");

        Assert.True(navigate.InputSchema.GetProperty("properties").TryGetProperty("url", out var url));
        Assert.Equal("string", url.GetProperty("type").GetString());
        Assert.Contains("url", RequiredNames(navigate.InputSchema));
        Assert.True(click.InputSchema.GetProperty("properties").TryGetProperty("selector", out var selector));
        Assert.Equal("string", selector.GetProperty("type").GetString());
    }

    [Fact]
    public void Page_eval_read_is_readonly_and_shares_the_eval_handler()
    {
        var tools = new AiToolRegistry();
        new CommandToolAdapter(CreateCommands()).RegisterAll(tools);
        var write = tools.All.Single(tool => tool.Name == "page_eval");
        var read = tools.All.Single(tool => tool.Name == "page_eval_read");
        Assert.Equal(AiToolRisk.Mutating, write.Risk);
        Assert.Equal(AiToolRisk.ReadOnly, read.Risk);
        Assert.Equal(write.InputSchema.GetRawText(), read.InputSchema.GetRawText());
    }

    [Fact]
    public async Task Page_navigate_typed_url_is_forwarded_to_open()
    {
        var commands = CreateCommands();
        var tools = new AiToolRegistry();
        new CommandToolAdapter(commands).RegisterAll(tools);

        string? received = null;
        commands.Register(new CommandDefinition
        {
            Name = "open",
            Summary = "导航到指定地址",
            Usage = "open <url>",
            IsMutating = true,
            Handler = (context, _) =>
            {
                received = context.RawInput;
                return Task.FromResult(CommandResult.Ok());
            }
        });

        var tool = tools.All.Single(candidate => candidate.Name == "page_navigate");
        var result = await tool.Handler(
            new ToolInvocation(tool.Name, JsonSerializer.SerializeToElement(new { url = "https://typed.test/" }), "page-selected"),
            default);

        Assert.True(result.Success);
        Assert.Equal("open https://typed.test/", received);
    }

    [Fact]
    public async Task Browser_command_tools_forward_selected_page_and_classify_writes()
    {
        var commands = CreateCommands();
        string? receivedPageId = null;
        commands.Register(new CommandDefinition
        {
            Name = "probe-page",
            Summary = "test",
            Usage = "probe-page",
            IsMutating = true,
            Handler = (context, _) =>
            {
                receivedPageId = context.PageId;
                return Task.FromResult(CommandResult.Ok());
            }
        });
        var tools = new AiToolRegistry();
        new CommandToolAdapter(commands).RegisterAll(tools);

        var probe = tools.All.Single(tool => tool.Name == "page_probe_page");
        var result = await probe.Handler(new ToolInvocation(probe.Name, EmptyArguments(), "page-selected"), default);

        Assert.True(result.Success);
        Assert.Equal("page-selected", receivedPageId);
        Assert.Equal(AiToolRisk.Mutating, probe.Risk);
        Assert.All(new[] { "page_navigate", "page_click", "page_type" }, name =>
            Assert.Equal(AiToolRisk.Mutating, tools.All.Single(tool => tool.Name == name).Risk));
        Assert.Equal(AiToolRisk.ReadOnly, tools.All.Single(tool => tool.Name == "page_query").Risk);
    }

    [Fact]
    public async Task Inspection_tools_require_and_forward_selected_page()
    {
        var console = new RecordingConsoleQuery();
        var network = new RecordingNetworkQuery();
        var tools = new AiToolRegistry();
        new InspectionToolAdapter(console, network).RegisterAll(tools);

        var consoleTool = tools.All.Single(tool => tool.Name == "console_read");
        var networkTool = tools.All.Single(tool => tool.Name == "network_list");
        var missingPage = await consoleTool.Handler(new ToolInvocation(consoleTool.Name, EmptyArguments()), default);
        var consoleResult = await consoleTool.Handler(new ToolInvocation(consoleTool.Name, EmptyArguments(), "page-selected"), default);
        var networkResult = await networkTool.Handler(new ToolInvocation(networkTool.Name, EmptyArguments(), "page-selected"), default);

        Assert.False(missingPage.Success);
        Assert.True(consoleResult.Success);
        Assert.True(networkResult.Success);
        Assert.Equal("page-selected", console.PageId);
        Assert.Equal("page-selected", network.PageId);
        Assert.Equal(AiToolRisk.ReadOnly, consoleTool.Risk);
        Assert.Equal(AiToolRisk.ReadOnly, networkTool.Risk);
    }

    [Fact]
    public async Task Page_context_requires_active_page_and_matches_page_id_exactly()
    {
        var contexts = new BrowserPageContextService();
        var selected = new BrowserTabViewModel("page-12", "https://selected.invalid/start")
        {
            Title = "Selected page",
            IsCdpReady = true,
            IsAgentReady = false
        };
        var similarlyNamed = new BrowserTabViewModel("page-123", "https://other.invalid/")
        {
            Title = "Other page",
            IsCdpReady = false,
            IsAgentReady = true
        };
        contexts.Track(selected);
        contexts.Track(similarlyNamed);
        selected.CurrentUrl = "https://selected.invalid/current";
        selected.Title = "Selected page (current)";

        var tools = new AiToolRegistry();
        new PageContextToolAdapter(contexts).RegisterAll(tools);
        var tool = tools.All.Single(candidate => candidate.Name == "page_context");

        var missingActivePage = await tool.Handler(
            new ToolInvocation(tool.Name, EmptyArguments()), default);
        var unknownPage = await tool.Handler(
            new ToolInvocation(tool.Name, EmptyArguments(), "page-1"), default);
        var result = await tool.Handler(
            new ToolInvocation(tool.Name, EmptyArguments(), "page-12"), default);

        Assert.False(missingActivePage.Success);
        Assert.False(unknownPage.Success);
        Assert.True(result.Success);
        Assert.Equal(AiToolRisk.ReadOnly, tool.Risk);

        using var json = JsonDocument.Parse(result.Content);
        var root = json.RootElement;
        Assert.Equal("page-12", root.GetProperty("PageId").GetString());
        Assert.Equal("https://selected.invalid/current", root.GetProperty("Url").GetString());
        Assert.Equal("Selected page (current)", root.GetProperty("Title").GetString());
        Assert.True(root.GetProperty("IsCdpReady").GetBoolean());
        Assert.False(root.GetProperty("IsPageAgentReady").GetBoolean());
    }

    [Fact]
    public void Page_context_removes_closed_page_without_exposing_another_tab()
    {
        var contexts = new BrowserPageContextService();
        var closed = new BrowserTabViewModel("page-closed", "https://closed.invalid/");
        var remaining = new BrowserTabViewModel("page-open", "https://open.invalid/");
        contexts.Track(closed);
        contexts.Track(remaining);

        contexts.Untrack(closed.PageId);

        Assert.Null(contexts.Read(closed.PageId));
        Assert.Equal(remaining.PageId, contexts.Read(remaining.PageId)?.PageId);
    }

    [Fact]
    public void Inspection_stores_filter_observations_to_selected_page()
    {
        var logger = new NullLogger();
        var sessions = new EmptySessionRegistry();
        var console = new ConsoleStore(sessions, logger);
        console.Entries.Add(new ConsoleEntry(DateTime.Now, "info", "selected", "console", "page-selected"));
        console.Entries.Add(new ConsoleEntry(DateTime.Now, "info", "other", "console", "page-other"));
        var network = new NetworkStore(sessions, new EventBus(), logger);
        network.Entries.Add(new NetworkEntry { PageId = "page-selected", RequestId = "selected", Method = "GET", Url = "https://selected.invalid/" });
        network.Entries.Add(new NetworkEntry { PageId = "page-other", RequestId = "other", Method = "GET", Url = "https://other.invalid/" });

        var consoleResult = console.Read(pageId: "page-selected");
        var networkResult = network.Read(pageId: "page-selected");

        Assert.Equal("selected", Assert.Single(consoleResult).Text);
        Assert.Equal("selected", Assert.Single(networkResult).RequestId);
    }

    [Fact]
    public async Task Mutating_browser_tool_requires_existing_policy_confirmation()
    {
        var tools = new AiToolRegistry();
        var invoked = false;
        tools.Register(new AiToolDefinition("page_click", "click", EmptyArguments(), AiToolRisk.Mutating,
            (_, _) => { invoked = true; return ValueTask.FromResult(ToolResult.Ok()); }));
        var confirmation = new RecordingConfirmation();
        var dispatcher = new AiToolDispatcher(tools, new DefaultToolPolicyGate(), confirmation);

        var result = await dispatcher.InvokeAsync(
            new ToolInvocation("page_click", EmptyArguments(), "page-selected", "session"));

        Assert.True(result.Success);
        Assert.True(invoked);
        Assert.Equal(1, confirmation.Count);
        Assert.Equal("page-selected", confirmation.Invocation?.PageId);
    }

    private static CommandRegistry CreateCommands()
    {
        var logger = new NullLogger();
        var timeline = new ActionTimelineStore();
        var executor = new ActionExecutor(new EmptySessionRegistry(), logger, timeline);
        var recorder = new ActionRecorder(new EventBus(), executor, timeline);
        return new CommandRegistry(executor, recorder, logger, timeline, new ActionPersistence());
    }

    private static JsonElement EmptyArguments() => JsonSerializer.SerializeToElement(new { });

    private static string[] RequiredNames(JsonElement schema) =>
        schema.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();

    private sealed class RecordingConsoleQuery : IConsoleQueryService
    {
        public string? PageId { get; private set; }
        public IReadOnlyList<ConsoleObservation> Read(int last = 100, string? level = null, string? pageId = null)
        {
            PageId = pageId;
            return [];
        }
    }

    private sealed class RecordingNetworkQuery : INetworkQueryService
    {
        public string? PageId { get; private set; }
        public IReadOnlyList<NetworkObservation> Read(int last = 100, bool failuresOnly = false, string? pageId = null)
        {
            PageId = pageId;
            return [];
        }
    }

    private sealed class RecordingConfirmation : IToolConfirmationService
    {
        public int Count { get; private set; }
        public ToolInvocation? Invocation { get; private set; }
        public ValueTask<ToolConfirmation> ConfirmAsync(ToolInvocation invocation, string reason, CancellationToken ct)
        {
            Count++;
            Invocation = invocation;
            return ValueTask.FromResult(new ToolConfirmation(true));
        }
    }

    private sealed class EmptySessionRegistry : ICdpSessionRegistry
    {
        public ICdpSession? Get(string pageId) => null;
        public IReadOnlyList<ICdpSession> All => [];
        public IDisposable Register(ICdpSession session) => throw new NotSupportedException();
        public event Action<ICdpSession>? SessionOpened;
        public event Action<string>? SessionClosed;
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
