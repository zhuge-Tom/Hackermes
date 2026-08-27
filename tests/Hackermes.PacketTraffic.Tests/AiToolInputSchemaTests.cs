using Hackermes.AiPanel.Tools;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AiToolInputSchemaTests
{
    [Fact]
    public async Task Missing_required_field_fails_and_names_the_field()
    {
        var reached = false;
        var dispatcher = CreateDispatcher(RequiredIdSchema(), (_, _) =>
        {
            reached = true;
            return ValueTask.FromResult(ToolResult.Ok("ok"));
        });

        var result = await dispatcher.InvokeAsync(Invocation("{}"));

        Assert.False(result.Success);
        Assert.False(reached);
        Assert.Contains("id", result.Content, StringComparison.Ordinal);
        Assert.Contains("不符合模式", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_field_present_reaches_the_handler()
    {
        var reached = false;
        var dispatcher = CreateDispatcher(RequiredIdSchema(), (_, _) =>
        {
            reached = true;
            return ValueTask.FromResult(ToolResult.Ok("ok"));
        });

        var result = await dispatcher.InvokeAsync(Invocation("""{"id":"x"}"""));

        Assert.True(result.Success);
        Assert.True(reached);
        Assert.Equal("ok", result.Content);
    }

    [Fact]
    public async Task Extra_properties_do_not_fail_validation()
    {
        var reached = false;
        var dispatcher = CreateDispatcher(RequiredIdSchema(), (_, _) =>
        {
            reached = true;
            return ValueTask.FromResult(ToolResult.Ok("ok"));
        });

        var result = await dispatcher.InvokeAsync(Invocation("""{"id":"x","extra":1}"""));

        Assert.True(result.Success);
        Assert.True(reached);
    }

    [Fact]
    public async Task Legacy_arguments_string_satisfies_missing_typed_required_fields()
    {
        var reached = false;
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                selector = new { type = "string" },
                arguments = new { type = "string" }
            },
            required = new[] { "selector" }
        });
        var dispatcher = CreateDispatcher(schema, (_, _) =>
        {
            reached = true;
            return ValueTask.FromResult(ToolResult.Ok("ok"));
        });

        var result = await dispatcher.InvokeAsync(Invocation("""{"arguments":"#submit"}"""));

        Assert.True(result.Success);
        Assert.True(reached);
    }

    [Fact]
    public async Task Non_object_arguments_fail_with_actionable_guidance()
    {
        var reached = false;
        var dispatcher = CreateDispatcher(RequiredIdSchema(), (_, _) =>
        {
            reached = true;
            return ValueTask.FromResult(ToolResult.Ok("ok"));
        });

        var result = await dispatcher.InvokeAsync(Invocation("""["id"]"""));

        Assert.False(result.Success);
        Assert.False(reached);
        Assert.Contains("工具参数必须是 JSON 对象", result.Content, StringComparison.Ordinal);
    }

    private static JsonElement RequiredIdSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { id = new { type = "string" } },
        required = new[] { "id" }
    });

    private static AiToolDispatcher CreateDispatcher(
        JsonElement schema,
        Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> handler)
    {
        var registry = new AiToolRegistry();
        registry.Register(new AiToolDefinition(
            "lookup", "lookup", schema, AiToolRisk.ReadOnly, handler));
        return new AiToolDispatcher(
            registry,
            new DefaultToolPolicyGate(),
            new RejectingToolConfirmationService(),
            TimeProvider.System,
            AiToolDispatcher.DefaultSessionGrantLifetime);
    }

    private static ToolInvocation Invocation(string arguments) =>
        new("lookup", JsonDocument.Parse(arguments).RootElement.Clone(), "page-one", "session-one");
}
