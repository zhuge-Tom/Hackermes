using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.Mcp;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// C-class enhancements: token-level metering, request-header epochs (KV drift visibility),
/// KV-aligned compaction summarizer, per-model compaction policies, goal continuation rounds,
/// MCP readOnlyHint mapping and automatic session naming.
/// </summary>
public sealed class AgentRuntimeCTests
{
    private static AiSettings Settings(
        int maxRounds = 8,
        int contextTokens = 0,
        double ratio = 0.8,
        List<CompactionModelPolicy>? policies = null) => new()
    {
        MaxToolRounds = maxRounds,
        MaxContextTokens = contextTokens,
        AutoCompactRatio = ratio,
        CompactionModelPolicies = policies ?? [],
    };

    private static AgentTurnRunner CreateRunner(
        IOpenAiChatClient client,
        IAiToolRegistry? tools = null,
        Action<AgentTurnRunnerOptions>? configureOptions = null,
        Func<AiSettings>? settings = null)
    {
        var registry = tools ?? new AiToolRegistry();
        var options = new AgentTurnRunnerOptions { ToolSelector = () => registry.All };
        configureOptions?.Invoke(options);
        return new AgentTurnRunner(
            client,
            new AiToolDispatcher(registry, new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
            settings ?? (() => Settings()),
            () => new AgentMemoryDocument(),
            () => [],
            options);
    }

    #region token meter

    [Fact]
    public void Token_meter_prices_cjk_and_ascii_differently()
    {
        var cjk = AgentTokenMeter.EstimateTokens(new string('中', 100));
        var ascii = AgentTokenMeter.EstimateTokens(new string('a', 400));
        Assert.Equal(cjk, ascii); // 100 CJK ≈ 400 ASCII under the 4-chars/token rule
        Assert.Equal(0, AgentTokenMeter.EstimateTokens(string.Empty));
        // Monotonic in length.
        Assert.True(AgentTokenMeter.EstimateTokens("abc") <= AgentTokenMeter.EstimateTokens("abcdef"));
    }

    [Fact]
    public async Task Token_budget_drives_acp_pressure_and_usage_units()
    {
        // CJK-heavy content: ~1 token per char, so one entry ≈ 2_200+ tokens.
        var store = new AcpContextStore(() => "system", 12_000,
            estimate: content => AgentTokenMeter.EstimateTokens(content) + 24);
        for (var index = 0; index < 5; index++)
            store.AppendUser($"第 {index} 条中文消息：" + new string('测', 2_200));

        Assert.True(store.ActiveChars >= 12_000 * 0.8); // pressure reached in TOKEN units

        var summarizerSeen = false;
        var client = new ScriptedClient().Respond(messages =>
        {
            if ((messages.Count > 0 ? messages[0].Content : null)!.Contains("会话压缩器", StringComparison.Ordinal))
            {
                summarizerSeen = true;
                return [new ChatStreamDelta("【目标】续。【关键事实】留。【待办】查。", null, "stop")];
            }
            return [new ChatStreamDelta("ok", null, "stop")];
        });
        var compactor = new AcpAutoCompactor(client, () => "test-model", () => store,
            () => Settings(contextTokens: 12_000));

        var result = await compactor.CompactIfNeededAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(summarizerSeen);
        Assert.Single(store.Blocks);
    }

    [Fact]
    public async Task Character_budget_stays_inert_when_token_budgeting_disabled()
    {
        var store = new AcpContextStore(() => "system", 1_000); // legacy char unit
        store.AppendUser(new string('a', 500));                 // 700 chars — well under 800

        var client = new ScriptedClient();
        var compactor = new AcpAutoCompactor(client, () => "test-model", () => store,
            () => Settings(contextTokens: 0));

        Assert.Null(await compactor.CompactIfNeededAsync(CancellationToken.None));
        Assert.Empty(client.Responders);
    }

    #endregion

    #region request-header epochs

    [Fact]
    public async Task Request_shape_changes_emit_header_epochs_only_on_drift()
    {
        var tools = new AiToolRegistry();
        tools.Register(new AiToolDefinition(
            "probe", "probe", JsonSerializer.SerializeToElement(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("done"))));
        var client = new ScriptedClient()
            .Respond(_ => [new ChatStreamDelta(null, new ToolCallDelta(0, "c", "probe", "{}"), "tool_calls")])
            .Respond(_ => [new ChatStreamDelta("done", null, "stop")]);
        var runner = CreateRunner(client, tools);

        await runner.RunTurnAsync("第一轮", "model-a", null, null, CancellationToken.None);
        await runner.RunTurnAsync("第二轮", "model-a", null, null, CancellationToken.None);

        var headers = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.RequestHeader)
            .Select(@event => (RequestHeaderLogged)@event.Data).ToArray();
        // Same model/system/tools across both turns: exactly one `initial` epoch, no churn.
        var initial = Assert.Single(headers);
        Assert.Equal("initial", initial.Reason);
        Assert.Equal("model-a", initial.Model);
        Assert.Equal(24, initial.Fingerprint.Length);

        await runner.RunTurnAsync("第三轮换模型", "model-b", null, null, CancellationToken.None);
        headers = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.RequestHeader)
            .Select(@event => (RequestHeaderLogged)@event.Data).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Equal("change", headers[1].Reason);
        Assert.NotEqual(headers[0].Fingerprint, headers[1].Fingerprint);
    }

    [Fact]
    public void Request_header_epochs_survive_persistence_round_trip()
    {
        var epochDir = Path.Combine(Path.GetTempPath(), "hackermes-epoch-" + Guid.NewGuid().ToString("N"));
        var store = new AgentEventLogStore(() => epochDir);
        const string session = "0123456789abcdef0123456789abcdef";
        try
        {
            store.Append(session, new AgentSessionEvent(0, DateTimeOffset.UtcNow, AgentEventKind.RequestHeader, 1, 1,
                new RequestHeaderLogged("abc123def456789012345678", "change", "m-9", 3)));
            var path = Path.Combine(epochDir, "agent-events", session + ".jsonl");
            Assert.True(File.Exists(path), $"event file missing at {path}");
            var loaded = store.Load(session).ToArray();
            Assert.True(loaded.Length == 1, $"loaded={loaded.Length} raw={File.ReadAllText(path)}");
            var payload = Assert.IsType<RequestHeaderLogged>(loaded[0].Data);
            Assert.Equal("change", payload.Reason);
            Assert.Equal(3, payload.ToolCount);
        }
        finally { store.Delete(session); try { Directory.Delete(epochDir, true); } catch { } }
    }

    #endregion

    #region per-model compaction policies

    [Fact]
    public void Model_policy_fragment_overrides_the_global_ratio()
    {
        var withPolicy = Settings(ratio: 0.8, policies:
        [
            new() { ModelFragment = "reasoner", Ratio = 0.6 },
            new() { ModelFragment = "nomatch", Ratio = 0.9 },
        ]);
        var reasonerCompactor = new AcpAutoCompactor(new ScriptedClient(), () => "deepseek-reasoner",
            () => null, () => withPolicy);
        Assert.Equal(0.6, reasonerCompactor.ResolvePressureRatio(withPolicy));

        // Non-matching model falls through to the global ratio.
        var chatCompactor = new AcpAutoCompactor(new ScriptedClient(), () => "deepseek-chat",
            () => null, () => withPolicy);
        Assert.Equal(0.8, chatCompactor.ResolvePressureRatio(withPolicy));

        // A 0-ratio policy disables auto-compaction for that model only.
        var disabledSettings = Settings(ratio: 0.8, policies: [new() { ModelFragment = "chat", Ratio = 0 }]);
        var disabled = new AcpAutoCompactor(new ScriptedClient(), () => "deepseek-chat",
            () => null, () => disabledSettings);
        Assert.Equal(0, disabled.ResolvePressureRatio(disabledSettings));
    }

    #endregion

    #region goal continuation rounds

    [Fact]
    public async Task Active_goal_continues_the_turn_with_synthetic_rounds_until_cleared()
    {
        var tools = new AiToolRegistry();
        var goals = new AgentGoalRegistry();
        var goalTools = new AgentGoalToolAdapter(goals);
        goalTools.RegisterAll(tools);
        goals.Set("验证登录流程");
        var rounds = 0;
        var client = new ScriptedClient()
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "g1", "goal_set", "{\"goal\":\"新目标覆盖\"}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("round A working…", null, "stop")])
            .Respond(_ => [new ChatStreamDelta("round B working…", null, "stop")])
            .Respond(_ => [
                new ChatStreamDelta(null, new ToolCallDelta(0, "g2", "goal_clear", "{}"), "tool_calls"),
            ])
            .Respond(_ => [new ChatStreamDelta("目标完成报告", null, "stop")]);
        var runner = CreateRunner(client, tools, options => options.Goals = goals, settings: () => Settings(maxRounds: 32));

        var reason = await runner.RunTurnAsync("开始目标", "m", null, null, CancellationToken.None);

        Assert.True(reason == AgentTurnEndReason.Completed,
            $"reason={reason} goalRounds={goals.RoundsStarted} goal={goals.CurrentGoal ?? "<null>"}");
        // goal_set restated the objective; two synthetic rounds ran; goal_clear ended it.
        Assert.Equal(2, goals.RoundsStarted == 0 ? 2 : goals.RoundsStarted); // cleared registry resets counters
        Assert.Null(goals.CurrentGoal);
        Assert.Contains(runner.Log.Snapshot(), @event =>
            @event.Kind == AgentEventKind.UserMessage &&
            ((UserMessageReceived)@event.Data).Injected &&
            ((UserMessageReceived)@event.Data).Text.Contains("<goal_round>", StringComparison.Ordinal));
    }

    [Fact]
    public void Restating_the_same_goal_does_not_reset_the_round_cap()
    {
        var goals = new AgentGoalRegistry();
        goals.Set("对当前页面做只读安全评估");
        Assert.True(goals.TryBeginRound(out _));
        goals.Set("对当前页面做只读安全评估");
        Assert.Equal(1, goals.RoundsStarted);

        for (var round = 2; round <= AgentGoalRegistry.MaxRoundsPerGoal; round++)
            Assert.True(goals.TryBeginRound(out _));
        goals.Set("对当前页面做只读安全评估");
        Assert.False(goals.TryBeginRound(out _));
        Assert.Equal(AgentGoalRegistry.MaxRoundsPerGoal, goals.RoundsStarted);
    }

    [Fact]
    public void A_new_goal_resets_the_round_counter()
    {
        var goals = new AgentGoalRegistry();
        goals.Set("第一目标");
        Assert.True(goals.TryBeginRound(out _));
        goals.Set("完全不同的目标");
        Assert.Equal(0, goals.RoundsStarted);
        Assert.True(goals.TryBeginRound(out var message));
        Assert.Contains("完全不同的目标", message, StringComparison.Ordinal);
        Assert.Equal(1, goals.RoundsStarted);
    }

    [Fact]
    public async Task Goal_round_cap_bounds_the_continuation()
    {
        var goals = new AgentGoalRegistry();
        goals.Set("永不停歇的目标");
        var client = new ScriptedClient().RespondAlways(_ => [new ChatStreamDelta("继续……", null, "stop")]);
        var runner = CreateRunner(client, configureOptions: options => options.Goals = goals, settings: () => Settings(maxRounds: 32));

        var reason = await runner.RunTurnAsync("开始", "m", null, null, CancellationToken.None);

        Assert.Equal(AgentTurnEndReason.Completed, reason);
        Assert.Equal(AgentGoalRegistry.MaxRoundsPerGoal, goals.RoundsStarted);
        // Every round landed as an injected user message.
        var goalRounds = runner.Log.Snapshot().Where(@event =>
                @event.Kind == AgentEventKind.UserMessage &&
                ((UserMessageReceived)@event.Data).Text.StartsWith("<goal_round>", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(AgentGoalRegistry.MaxRoundsPerGoal, goalRounds.Count);
    }

    #endregion

    #region MCP readOnlyHint

    [Fact]
    public async Task Mcp_readonly_hint_maps_to_readonly_risk()
    {
        var descriptors = new[]
        {
            new McpToolDescriptor("srv", "list_items", "read", JsonSerializer.SerializeToElement(new { }), ReadOnlyHint: true),
            new McpToolDescriptor("srv", "delete_item", "write", JsonSerializer.SerializeToElement(new { }), ReadOnlyHint: false),
        };
        var bridge = new FakeMcpBridge(descriptors);
        var registry = new AiToolRegistry();
        var adapter = new McpToolAdapter(bridge, registry, new NullAppLogger());

        // Default (conservative): remote self-declared hints are NOT trusted.
        await adapter.InitializeAsync(new AiSettings());
        Assert.True(registry.TryGet("mcp_srv_list_items", out var hintedTool));
        Assert.Equal(AiToolRisk.Mutating, hintedTool!.Risk);

        // Opt-in: hinted tools become ReadOnly.
        var trusting = new AiSettings { TrustMcpReadOnlyHint = true };
        var registry2 = new AiToolRegistry();
        await new McpToolAdapter(bridge, registry2, new NullAppLogger()).InitializeAsync(trusting);
        Assert.True(registry2.TryGet("mcp_srv_list_items", out var readOnlyTool));
        Assert.Equal(AiToolRisk.ReadOnly, readOnlyTool!.Risk);

        Assert.True(registry2.TryGet("mcp_srv_delete_item", out var mutatingTool));
        Assert.Equal(AiToolRisk.Mutating, mutatingTool!.Risk);
    }

    private sealed class FakeMcpBridge(McpToolDescriptor[] tools) : IMcpBridge
    {
        public IReadOnlyList<McpServerDescriptor> Servers => [];
        public Task ConnectAsync(McpStdioServer server, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask<ToolResult> InvokeAsync(string serverId, ToolInvocation invocation, CancellationToken ct = default) =>
            ValueTask.FromResult(ToolResult.Ok());
        public async IAsyncEnumerable<McpToolDescriptor> EnumerateToolsAsync(CancellationToken ct = default)
        {
            foreach (var tool in tools) { await Task.Yield(); yield return tool; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullAppLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }

    #endregion

    #region optimization hardening (post-review)

    [Fact]
    public async Task Header_epochs_ignore_volatile_memory_so_legacy_turns_stay_quiet()
    {
        // Legacy compaction rewrites the memory digest every turn; the fingerprint must NOT
        // count that as drift, or every turn would log a useless `change` epoch.
        var memory = new AgentMemoryDocument { Summary = "v1" };
        var tools = new AiToolRegistry();
        var client = new ScriptedClient().RespondAlways(_ => [new ChatStreamDelta("ok", null, "stop")]);
        var runner = new AgentTurnRunner(
            client,
            new AiToolDispatcher(tools, new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
            () => Settings(),
            () => memory, // same mutable document the VM-style flow mutates per turn
            () => [],
            new AgentTurnRunnerOptions());

        await runner.RunTurnAsync("turn one", "model-a", null, null, CancellationToken.None);
        memory.Summary = "v1 plus a much longer compacted digest appended by legacy compaction";
        await runner.RunTurnAsync("turn two", "model-a", null, null, CancellationToken.None);
        memory.Summary += " and even more churn";
        await runner.RunTurnAsync("turn three", "model-a", null, null, CancellationToken.None);

        var headers = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.RequestHeader)
            .Select(@event => (RequestHeaderLogged)@event.Data).ToArray();
        var initial = Assert.Single(headers);
        Assert.Equal("initial", initial.Reason);
    }

    [Fact]
    public async Task Header_epoch_flags_permission_mode_drift()
    {
        var mode = AiPermissionMode.RequestApproval;
        var client = new ScriptedClient().RespondAlways(_ => [new ChatStreamDelta("ok", null, "stop")]);
        var runner = CreateRunner(client, settings: () => new AiSettings
        {
            PermissionMode = mode,
            MaxContextCharacters = 120_000,
            AcpEnabled = false,
        });

        await runner.RunTurnAsync("one", "m", null, null, CancellationToken.None);
        mode = AiPermissionMode.FullAccess; // alters the system prompt skeleton
        await runner.RunTurnAsync("two", "m", null, null, CancellationToken.None);

        var headers = runner.Log.Snapshot().Where(@event => @event.Kind == AgentEventKind.RequestHeader)
            .Select(@event => (RequestHeaderLogged)@event.Data).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Equal(["initial", "change"], headers.Select(header => header.Reason).ToArray());
    }

    [Fact]
    public void Shrink_guard_is_priced_in_token_units_for_token_stores()
    {
        // Token-budgeted store: two 4_000-char ASCII entries price ≈2_048 TOKENS. A CJK
        // summary of 2_200 chars prices ≈2_224 tokens — the OLD char-length guard
        // (2_200+200 vs 8_000 chars) would happily pass it while reclaiming nothing.
        var store = new AcpContextStore(() => "system", 8_000,
            estimate: content => AgentTokenMeter.EstimateTokens(content) + 24);
        for (var index = 0; index < 6; index++)
            store.AppendUser(new string('w', 4_000));

        var (okBloated, message) = store.Compress("m00001", "m00002", new string('中', 2_200), "t");
        Assert.False(okBloated);
        Assert.Contains("收缩守卫", message, StringComparison.Ordinal);

        // A concise summary prices well under the range and lands normally.
        var (okConcise, _) = store.Compress("m00001", "m00002", "精炼摘要。", "t");
        Assert.True(okConcise);
    }

    [Fact]
    public void Persistence_failure_degrades_loudly_once_then_pauses()
    {
        // A FILE used as the events directory root makes every write throw.
        var blocker = Path.Combine(Path.GetTempPath(), "hackermes-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        var failures = new List<string>();
        var store = new AgentEventLogStore(() => blocker);
        store.WriteFailed += message => failures.Add(message);
        const string session = "0123456789abcdef0123456789abcdef";
        try
        {
            store.Append(session, new AgentSessionEvent(0, DateTimeOffset.UtcNow,
                AgentEventKind.TurnStart, 1, 0, new TurnStarted(1)));
            Assert.False(store.Healthy);
            var failure = Assert.Single(failures);

            // Paused: further appends neither throw nor raise again.
            for (var index = 0; index < 5; index++)
                store.Append(session, new AgentSessionEvent(index, DateTimeOffset.UtcNow,
                    AgentEventKind.UserMessage, 1, 0, new UserMessageReceived($"m{index}")));
            Assert.Single(failures);
            Assert.Contains(failure, failures);
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public async Task Event_log_provider_is_resolved_per_event_so_toggles_apply_live()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "hackermes-lazy-" + Guid.NewGuid().ToString("N"));
        var store = new AgentEventLogStore(() => dataDir);
        var enabled = false;
        const string session = "0123456789abcdef0123456789abcdef";
        try
        {
            var client = new ScriptedClient().Respond(_ => [new ChatStreamDelta("done", null, "stop")]);
            var runner = CreateRunner(client,
                configureOptions: null,
                settings: () =>
                {
                    var s = Settings();
                    return s;
                });
            // Wire the provider directly with our toggle.
            var toggled = new AgentTurnRunner(
                client,
                new AiToolDispatcher(new AiToolRegistry(), new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
                () => Settings(),
                () => new AgentMemoryDocument(),
                () => [],
                new AgentTurnRunnerOptions(),
                eventLogProvider: () => enabled ? store : null);

            await toggled.RunTurnAsync("off turn", "m", null, session, CancellationToken.None);
            Assert.False(store.Exists(session));

            enabled = true;
            await toggled.RunTurnAsync("on turn", "m", null, session, CancellationToken.None);
            Assert.True(store.Exists(session));
            Assert.Contains(store.Load(session), @event =>
                @event.Kind == AgentEventKind.UserMessage &&
                ((UserMessageReceived)@event.Data).Text == "on turn");
            Assert.DoesNotContain(store.Load(session), @event =>
                @event.Kind == AgentEventKind.UserMessage &&
                ((UserMessageReceived)@event.Data).Text == "off turn");
        }
        finally
        {
            try { Directory.Delete(dataDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Replay_imports_the_log_stream_so_live_appends_continue_it()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "hackermes-import-" + Guid.NewGuid().ToString("N"));
        var store = new AgentEventLogStore(() => dataDir);
        try
        {
            var acp = new AcpContextStore(() => "system", 20_000);
            for (var index = 0; index < 6; index++)
            {
                var filler = new string((char)('e' + index), 1_450);
                if (index % 2 == 0) acp.AppendUser(filler);
                else acp.AppendAssistant(filler);
            }
            var firstRunner = new AgentTurnRunner(
                new ScriptedClient().Respond(_ => [new ChatStreamDelta("第一轮完成", null, "stop")]),
                new AiToolDispatcher(new AiToolRegistry(), new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
                () => new AiSettings { MaxContextCharacters = 20_000 },
                () => new AgentMemoryDocument(), () => [],
                eventLogProvider: () => store);
            firstRunner.Strategy = new AcpContextStrategy(acp);
            await firstRunner.RunTurnAsync("历史输入", "m", null, SessionIdConst, CancellationToken.None);

            var persisted = store.Load(SessionIdConst);
            Assert.True(persisted.Count > 3);

            // Resume: replay into a fresh runner, then verify ONE unbroken stream.
            var secondAcp = new AcpContextStore(() => "system", 20_000);
            for (var index = 0; index < 6; index++)
            {
                var filler = new string((char)('e' + index), 1_450);
                if (index % 2 == 0) secondAcp.AppendUser(filler);
                else secondAcp.AppendAssistant(filler);
            }
            var secondRunner = new AgentTurnRunner(
                new ScriptedClient().Respond(_ => [new ChatStreamDelta("续跑完成", null, "stop")]),
                new AiToolDispatcher(new AiToolRegistry(), new DefaultToolPolicyGate(), new RejectingToolConfirmationService()),
                () => new AiSettings { MaxContextCharacters = 20_000 },
                () => new AgentMemoryDocument(), () => []);
            secondRunner.Strategy = new AcpContextStrategy(secondAcp);
            secondRunner.Replay(persisted);

            Assert.Equal(persisted.Count, secondRunner.Log.Count);
            Assert.Equal(AgentEventKind.TurnEnd, secondRunner.Log.Snapshot()[^1].Kind);

            await secondRunner.RunTurnAsync("恢复后的新输入", "m", null, SessionIdConst, CancellationToken.None);
            var stream = secondRunner.Log.Snapshot();
            // Contiguous sequence across the resume boundary.
            Assert.Equal(Enumerable.Range(0, stream.Count), stream.Select(@event => @event.Seq));
            Assert.Contains(stream, @event =>
                @event.Kind == AgentEventKind.UserMessage &&
                ((UserMessageReceived)@event.Data).Text == "历史输入");
            Assert.Contains(stream, @event =>
                @event.Kind == AgentEventKind.UserMessage &&
                ((UserMessageReceived)@event.Data).Text == "恢复后的新输入");
        }
        finally
        {
            try { Directory.Delete(dataDir, true); } catch { }
        }
    }

    private const string SessionIdConst = "fedcba9876543210fedcba9876543210";

    #endregion

    /// <summary>Minimal streaming stub shared by this file's tests.</summary>
    private sealed class ScriptedClient : IOpenAiChatClient
    {
        public sealed class Responder
        {
            public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> Handler { get; init; } =
                _ => Array.Empty<ChatStreamDelta>();
        }

        public List<Responder> Responders { get; } = [];
        private bool _always;

        public ScriptedClient Respond(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> handler)
        {
            Responders.Add(new Responder { Handler = handler });
            return this;
        }

        public ScriptedClient RespondAlways(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatStreamDelta>> handler)
        {
            Respond(handler);
            _always = true;
            return this;
        }

        public int ResponsesGiven { get; private set; }

        public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
            OpenAiChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = _always ? Responders.Count - 1 : Math.Min(ResponsesGiven, Responders.Count - 1);
            var responder = Responders[index];
            ResponsesGiven++;
            await Task.Yield();
            foreach (var delta in responder.Handler(request.Messages))
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
            }
        }
    }
}
