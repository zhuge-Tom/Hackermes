using Hackermes.AiPanel.OpenAI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Transient provider failures (429/5xx/connection resets) are retried before any
/// stream content is consumed, and usage chunks surface as StreamUsage deltas.
/// </summary>
public sealed class OpenAiCompatibleClientResilienceTests
{
    [Fact]
    public async Task Transient_status_failures_are_retried_and_usage_is_parsed()
    {
        var handler = new StubHandler(
            Text(HttpStatusCode.InternalServerError, "boom"),
            Text(HttpStatusCode.TooManyRequests, "slow down"),
            Sse(
                """{"choices":[{"delta":{"content":"ok"}}]}""",
                """{"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}"""));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, maxRetries: 2, backoff: _ => TimeSpan.Zero);

        var deltas = await DrainAsync(client);

        Assert.Equal(3, handler.Calls);
        Assert.Contains(deltas, delta => delta.Content == "ok");
        var usage = Assert.Single(deltas.Where(d => d.Usage is not null)).Usage!;
        Assert.Equal(7, usage.PromptTokens);
        Assert.Equal(3, usage.CompletionTokens);
        Assert.Equal(10, usage.TotalTokens);
    }

    [Fact]
    public async Task Connection_level_failures_are_retried()
    {
        var handler = new StubHandler(
            Throw(new HttpRequestException("connection reset")),
            Sse("""{"choices":[{"delta":{"content":"recovered"}}]}"""));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, maxRetries: 1, backoff: _ => TimeSpan.Zero);

        var deltas = await DrainAsync(client);

        Assert.Equal(2, handler.Calls);
        Assert.Contains(deltas, delta => delta.Content == "recovered");
    }

    [Fact]
    public async Task Non_retryable_failure_fails_immediately_with_detail()
    {
        var handler = new StubHandler(Text(HttpStatusCode.BadRequest, """{"error":{"message":"bad model"}}"""));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, maxRetries: 2, backoff: _ => TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => DrainTask(client));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
        Assert.Contains("bad model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exhausted_retries_surface_the_last_status()
    {
        var handler = new StubHandler(
            Text(HttpStatusCode.ServiceUnavailable, "unavailable"),
            Text(HttpStatusCode.ServiceUnavailable, "unavailable"),
            Text(HttpStatusCode.ServiceUnavailable, "unavailable"));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, maxRetries: 2, backoff: _ => TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => DrainTask(client));

        Assert.Equal(3, handler.Calls);
        Assert.Contains("503", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_count_is_bounded_by_construction()
    {
        using var http = new HttpClient(new StubHandler(() => null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenAiCompatibleClient(http, maxRetries: 6, backoff: null));
    }

    private static async Task<List<ChatStreamDelta>> DrainAsync(IOpenAiChatClient client)
    {
        var deltas = new List<ChatStreamDelta>();
        await foreach (var delta in client.StreamChatAsync(Request()).ConfigureAwait(false))
            deltas.Add(delta);
        return deltas;
    }

    private static Task DrainTask(IOpenAiChatClient client) => DrainAsync(client);

    private static OpenAiChatRequest Request() => new("test-model", [new ChatMessage("user", "hi")]);

    private static Func<HttpResponseMessage> Sse(params string[] chunks) => () =>
    {
        var builder = new StringBuilder();
        foreach (var chunk in chunks) builder.Append("data: ").Append(chunk).Append("\n\n");
        builder.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    };

    private static Func<HttpResponseMessage> Text(HttpStatusCode status, string body) => () =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> Throw(HttpRequestException exception) => () => throw exception;

    private sealed class StubHandler(params Func<HttpResponseMessage>[] factories) : HttpMessageHandler
    {
        private int _index;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_index >= factories.Length) throw new InvalidOperationException("Stub response queue exhausted.");
            return Task.FromResult(factories[_index++]());
        }
    }
}
