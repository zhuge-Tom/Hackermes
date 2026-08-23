using Hackermes.AiPanel.Tools;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Dispatcher hardening: bounded tool results, per-call timeout, and actionable
/// failure guidance so the model can self-correct instead of retrying blindly.
/// </summary>
public sealed class AiToolDispatcherHardeningTests
{
    [Fact]
    public async Task Oversized_success_result_is_truncated_with_explicit_marker()
    {
        var dispatcher = CreateDispatcher(
            "packet_query",
            AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(new string('x', 500))),
            maxToolResultCharacters: 256);

        var result = await dispatcher.InvokeAsync(Invocation("packet_query", "{}"));

        Assert.True(result.Success);
        Assert.StartsWith(new string('x', 256), result.Content, StringComparison.Ordinal);
        Assert.Contains("已截断", result.Content, StringComparison.Ordinal);
        Assert.Contains("500", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < 500, "Truncation must not keep the full payload.");
    }

    [Fact]
    public async Task Short_result_passes_through_unchanged()
    {
        var dispatcher = CreateDispatcher(
            "packet_query",
            AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok("compact")),
            maxToolResultCharacters: 256);

        var result = await dispatcher.InvokeAsync(Invocation("packet_query", "{}"));

        Assert.Equal("compact", result.Content);
    }

    [Fact]
    public async Task Timed_out_handler_returns_bounded_failure()
    {
        var dispatcher = CreateDispatcher(
            "page_wait",
            AiToolRisk.ReadOnly,
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return ToolResult.Ok("unreachable");
            },
            toolCallTimeout: TimeSpan.FromMilliseconds(150));

        var result = await dispatcher.InvokeAsync(Invocation("page_wait", "{}"));

        Assert.False(result.Success);
        Assert.Contains("已按超时取消", result.Content, StringComparison.Ordinal);
        Assert.Contains("拆小", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var dispatcher = CreateDispatcher(
            "page_wait",
            AiToolRisk.ReadOnly,
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return ToolResult.Ok("unreachable");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.InvokeAsync(Invocation("page_wait", "{}"), cts.Token).AsTask());
    }

    [Fact]
    public async Task Handler_exception_is_prefixed_with_tool_name()
    {
        var dispatcher = CreateDispatcher(
            "packet_show",
            AiToolRisk.ReadOnly,
            (_, _) => throw new InvalidOperationException("boom"));

        var result = await dispatcher.InvokeAsync(Invocation("packet_show", "{}"));

        Assert.False(result.Success);
        Assert.Contains("packet_show", result.Content, StringComparison.Ordinal);
        Assert.Contains("boom", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unapproved_operation_returns_actionable_guidance()
    {
        var dispatcher = CreateDispatcher(
            "packet_replay",
            AiToolRisk.Mutating,
            (_, _) => ValueTask.FromResult(ToolResult.Ok()),
            confirmation: new RejectingToolConfirmationService());

        var result = await dispatcher.InvokeAsync(Invocation("packet_replay", "{}"));

        Assert.False(result.Success);
        Assert.Contains("操作者未批准", result.Content, StringComparison.Ordinal);
        Assert.Contains("降低风险", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_denial_returns_actionable_guidance()
    {
        var dispatcher = CreateDispatcher(
            "page_eval",
            AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok()));

        var result = await dispatcher.InvokeAsync(Invocation("page_eval", """{"arguments":"rm -rf /"}"""));

        Assert.False(result.Success);
        Assert.Contains("不可恢复", result.Content, StringComparison.Ordinal);
        Assert.Contains("不要尝试绕过", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_out_of_bounds_limits()
    {
        var registry = new AiToolRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            registry, maxToolResultCharacters: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            registry, toolCallTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            registry, toolCallTimeout: TimeSpan.FromHours(2)));
    }

    private static AiToolDispatcher CreateDispatcher(
        string toolName,
        AiToolRisk risk,
        Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> handler,
        int maxToolResultCharacters = AiToolDispatcher.DefaultMaxToolResultCharacters,
        TimeSpan? toolCallTimeout = null,
        IToolConfirmationService? confirmation = null)
    {
        var registry = new AiToolRegistry();
        registry.Register(new AiToolDefinition(
            toolName, "tool", JsonSerializer.SerializeToElement(new { }), risk, handler));
        return Create(registry, maxToolResultCharacters, toolCallTimeout, confirmation);
    }

    private static AiToolDispatcher Create(
        IAiToolRegistry registry,
        int maxToolResultCharacters = AiToolDispatcher.DefaultMaxToolResultCharacters,
        TimeSpan? toolCallTimeout = null,
        IToolConfirmationService? confirmation = null) =>
        new(registry,
            new DefaultToolPolicyGate(),
            confirmation ?? new RejectingToolConfirmationService(),
            TimeProvider.System,
            AiToolDispatcher.DefaultSessionGrantLifetime,
            maxToolResultCharacters,
            toolCallTimeout ?? AiToolDispatcher.DefaultToolCallTimeout);

    private static ToolInvocation Invocation(string toolName, string arguments) =>
        new(toolName, JsonDocument.Parse(arguments).RootElement.Clone(), "page-one", "session-one");
}
