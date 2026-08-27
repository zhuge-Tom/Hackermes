using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.AiPanel.Tools;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AgentSubtaskTests
{
    [Fact]
    public async Task Subtask_runs_a_nested_readonly_tool_and_returns_the_assistant_conclusion()
    {
        var tools = new AiToolRegistry();
        var probed = 0;
        tools.Register(new AiToolDefinition("probe", "probe", JsonSerializer.SerializeToElement(new { }),
            AiToolRisk.ReadOnly, (_, _) =>
            {
                Interlocked.Increment(ref probed);
                return ValueTask.FromResult(ToolResult.Ok("probe-ok"));
            }));
        var client = new ScriptedClient()
            .Then(new ChatStreamDelta(null, new ToolCallDelta(0, "c1", "probe", "{}"), "tool_calls"))
            .Then(new ChatStreamDelta("nested done", null, "stop"));
        new AgentSubtaskToolAdapter(client, new AiToolDispatcher(tools, new DefaultToolPolicyGate(),
            new RejectingToolConfirmationService()), () => new AiSettings(), () => tools.All, () => "test")
            .RegisterAll(tools);

        Assert.True(tools.TryGet("agent_subtask", out var subtask));
        var result = await subtask!.Handler(new ToolInvocation("agent_subtask",
            JsonSerializer.SerializeToElement(new { goal = "check the page" }), "page-1", "s1"), default);

        Assert.True(result.Success, result.Content);
        Assert.Equal(1, probed);
        Assert.Contains("nested done", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subtask_rejects_nesting()
    {
        var tools = new AiToolRegistry();
        tools.Register(new AiToolDefinition("reenter", "reenter", JsonSerializer.SerializeToElement(new { }),
            AiToolRisk.ReadOnly, async (_, ct) =>
            {
                var nested = tools.All.Single(tool => tool.Name == "agent_subtask");
                return await nested.Handler(new ToolInvocation("agent_subtask",
                    JsonSerializer.SerializeToElement(new { goal = "inner" })), ct);
            }));
        var client = new ScriptedClient()
            .Then(new ChatStreamDelta(null, new ToolCallDelta(0, "c1", "reenter", "{}"), "tool_calls"))
            .Then(new ChatStreamDelta("outer done", null, "stop"));
        new AgentSubtaskToolAdapter(client, new AiToolDispatcher(tools, new DefaultToolPolicyGate(),
                new RejectingToolConfirmationService()), () => new AiSettings { PermissionMode = AiPermissionMode.FullAccess },
            () => tools.All, () => "test")
            .RegisterAll(tools);

        var result = await tools.All.Single(tool => tool.Name == "agent_subtask")
            .Handler(new ToolInvocation("agent_subtask",
                JsonSerializer.SerializeToElement(new { goal = "outer" })), default);

        Assert.True(result.Success, result.Content);
        Assert.Contains("禁止再嵌套", result.Content, StringComparison.Ordinal);
    }

    private sealed class ScriptedClient : IOpenAiChatClient
    {
        private readonly Queue<ChatStreamDelta[]> _responses = new();

        public ScriptedClient Then(params ChatStreamDelta[] deltas)
        {
            _responses.Enqueue(deltas);
            return this;
        }

        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var delta in _responses.Count == 0 ? [new ChatStreamDelta("empty", null, "stop")] : _responses.Dequeue())
                yield return delta;
        }
    }
}
