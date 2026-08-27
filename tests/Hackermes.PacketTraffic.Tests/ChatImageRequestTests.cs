using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class ChatImageRequestTests
{
    [Fact]
    public async Task Client_serializes_images_as_multimodal_content_parts()
    {
        string? body = null;
        var http = new HttpClient(new CaptureHandler(payload => body = payload))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var client = new OpenAiCompatibleClient(http, maxRetries: 0, backoff: _ => TimeSpan.Zero)
        {
            Endpoint = new Uri("https://example.test/v1/chat/completions")
        };

        await foreach (var _ in client.StreamChatAsync(new OpenAiChatRequest("m",
        [
            new ChatMessage("user", "see this", Images: [new ChatImage("image/png", "AAAA")])
        ]), CancellationToken.None))
        { }

        Assert.False(string.IsNullOrEmpty(body));
        using var document = JsonDocument.Parse(body!);
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Contains("data:image/png;base64,AAAA", content[1].GetProperty("image_url").GetProperty("url").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Command_result_can_carry_screenshot_bytes()
    {
        var result = new CommandResult(true, "截图成功(4 字节 base64)", "image/png", "AAAA");
        Assert.Equal("image/png", result.MediaType);
        Assert.Equal("AAAA", result.MediaBase64);
    }

    private sealed class CaptureHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
