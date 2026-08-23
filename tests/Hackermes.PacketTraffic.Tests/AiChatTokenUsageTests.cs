using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.ViewModels;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>Provider usage chunks accumulate into per-session counters shown in the chat status.</summary>
public sealed class AiChatTokenUsageTests
{
    [Fact]
    public async Task Usage_deltas_accumulate_across_requests()
    {
        var client = new UsageChatClient();
        using var viewModel = new AiChatViewModel(
            client,
            new AiToolRegistry(),
            new AiToolDispatcher(
                new AiToolRegistry(), new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
            new EventBus(),
            new StaticSettings(),
            new EmptySkillStore(),
            new InMemoryMemoryStore(),
            new AgentContextCompactor());
        viewModel.Input = "统计 token 用量。";

        await viewModel.SendCommand.ExecuteAsync(null);
        viewModel.Input = "再问一次。";
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(100, viewModel.SessionPromptTokens);
        Assert.Equal(30, viewModel.SessionCompletionTokens);
        Assert.Equal("↑100 ↓30 tokens", viewModel.TokenUsage);
    }

    private sealed class UsageChatClient : IOpenAiChatClient
    {
        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatStreamDelta(null, null, null, new StreamUsage(50, 15, 65));
            yield return new ChatStreamDelta("done", null, "stop");
        }
    }

    private sealed class StaticSettings : ISettingsService
    {
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null)
        {
            mutate(Load());
            return true;
        }
        public string SettingsFilePath => string.Empty;
    }

    private sealed class EmptySkillStore : IAgentSkillStore
    {
        public IReadOnlyList<AgentSkill> Snapshot() => [];
        public AgentSkill Upsert(AgentSkill skill) => skill;
        public bool Remove(string id) => true;
    }

    private sealed class InMemoryMemoryStore : IAgentMemoryStore
    {
        public AgentMemoryDocument Load() => new();
        public void SaveConversation(string summary, IReadOnlyList<AgentMemoryMessage> recentMessages) { }
        public void SetNotes(string notes) { }
        public void Clear() { }
    }
}
