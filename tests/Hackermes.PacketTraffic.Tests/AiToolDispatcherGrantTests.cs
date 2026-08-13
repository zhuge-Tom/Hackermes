using Hackermes.AiPanel.Tools;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AiToolDispatcherGrantTests
{
    [Fact]
    public async Task Remembered_grant_accepts_equivalent_canonical_arguments_on_same_page()
    {
        var confirmation = new RememberingConfirmation();
        var dispatcher = CreateDispatcher(confirmation);

        var first = await dispatcher.InvokeAsync(Invocation(
            "page-one", """{"selector":"#save","options":{"force":true,"timeout":10}}"""));
        var reordered = await dispatcher.InvokeAsync(Invocation(
            "page-one", """{"options":{"timeout":10.0,"force":true},"selector":"#save"}"""));

        Assert.True(first.Success);
        Assert.True(reordered.Success);
        Assert.Equal(1, confirmation.Count);
    }

    [Fact]
    public async Task Remembered_grant_does_not_cross_page_boundary()
    {
        var confirmation = new RememberingConfirmation();
        var dispatcher = CreateDispatcher(confirmation);

        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save"}"""));
        await dispatcher.InvokeAsync(Invocation("page-two", """{"selector":"#save"}"""));

        Assert.Equal(2, confirmation.Count);
    }

    [Fact]
    public async Task Remembered_grant_does_not_apply_to_changed_arguments()
    {
        var confirmation = new RememberingConfirmation();
        var dispatcher = CreateDispatcher(confirmation);

        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save","value":"secret-one"}"""));
        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save","value":"secret-two"}"""));

        Assert.Equal(2, confirmation.Count);
    }

    [Fact]
    public async Task Clearing_session_removes_all_scoped_grants()
    {
        var confirmation = new RememberingConfirmation();
        var dispatcher = CreateDispatcher(confirmation);

        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save"}"""));
        await dispatcher.InvokeAsync(Invocation("page-two", """{"selector":"#save"}"""));
        dispatcher.ClearSessionGrants("session-one");
        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save"}"""));
        await dispatcher.InvokeAsync(Invocation("page-two", """{"selector":"#save"}"""));

        Assert.Equal(4, confirmation.Count);
    }

    [Fact]
    public async Task Remembered_grant_expires_after_absolute_lifetime()
    {
        var confirmation = new RememberingConfirmation();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        var dispatcher = CreateDispatcher(confirmation, clock, TimeSpan.FromMinutes(5));

        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save"}"""));
        clock.Advance(TimeSpan.FromMinutes(4));
        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save"}"""));
        clock.Advance(TimeSpan.FromMinutes(2));
        await dispatcher.InvokeAsync(Invocation("page-one", """{"selector":"#save"}"""));

        Assert.Equal(2, confirmation.Count);
    }

    private static AiToolDispatcher CreateDispatcher(IToolConfirmationService confirmation)
        => CreateDispatcher(confirmation, TimeProvider.System, AiToolDispatcher.DefaultSessionGrantLifetime);

    private static AiToolDispatcher CreateDispatcher(
        IToolConfirmationService confirmation, TimeProvider timeProvider, TimeSpan lifetime)
    {
        var registry = new AiToolRegistry();
        registry.Register(new AiToolDefinition(
            "page_type",
            "type",
            JsonSerializer.SerializeToElement(new { }),
            AiToolRisk.Mutating,
            (_, _) => ValueTask.FromResult(ToolResult.Ok())));
        return new AiToolDispatcher(registry, new DefaultToolPolicyGate(), confirmation, timeProvider, lifetime);
    }

    private static ToolInvocation Invocation(string pageId, string arguments) =>
        new("page_type", JsonDocument.Parse(arguments).RootElement.Clone(), pageId, "session-one");

    private sealed class RememberingConfirmation : IToolConfirmationService
    {
        public int Count { get; private set; }

        public ValueTask<ToolConfirmation> ConfirmAsync(
            ToolInvocation invocation, string reason, CancellationToken ct)
        {
            Count++;
            return ValueTask.FromResult(new ToolConfirmation(true, RememberForSession: true));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
