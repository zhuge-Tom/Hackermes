using Hackermes.AiPanel.OpenAI;
using Hackermes.Platform.Models;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AiProviderPresetTests
{
    [Fact]
    public void Presets_AreCoreProvidersWithConciseDisplayNames()
    {
        Assert.Collection(
            AiProviderPresets.All,
            item => Assert.Equal("OpenAI", item.ToString()),
            item => Assert.Equal("DeepSeek", item.ToString()),
            item => Assert.Equal("OpenRouter", item.ToString()),
            item => Assert.Equal("自定义（OpenAI 兼容）", item.ToString()));
    }

    [Fact]
    public void ResolveChatEndpoint_CombinesCustomBaseAndRoute()
    {
        var uri = AiProviderPresets.ResolveChatEndpoint(
            "https://tokenrhythm.studio/v1",
            "/chat/completions");

        Assert.Equal("https://tokenrhythm.studio/v1/chat/completions", uri.AbsoluteUri);
    }

    [Fact]
    public void ResolveModelsEndpoint_UsesOpenAiDefaultRoute()
    {
        var uri = AiProviderPresets.ResolveModelsEndpoint("https://tokenrhythm.studio/v1");

        Assert.Equal("https://tokenrhythm.studio/v1/models", uri.AbsoluteUri);
    }

    [Fact]
    public void ResolveChatEndpoint_DoesNotDuplicateLegacyFullRoute()
    {
        var uri = AiProviderPresets.ResolveChatEndpoint(
            "https://example.test/v1/chat/completions",
            "/chat/completions");

        Assert.Equal("https://example.test/v1/chat/completions", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)", "/chat/completions")]
    [InlineData("https://example.test/v1", "https://other.test/chat/completions")]
    [InlineData("https://example.test/v1", "/chat/completions?token=leak")]
    public void ResolveChatEndpoint_RejectsUnsafeConfiguration(string endpoint, string route)
    {
        Assert.Throws<ArgumentException>(() => AiProviderPresets.ResolveChatEndpoint(endpoint, route));
    }

    [Fact]
    public void BrowserSettings_DefaultToBingNavigationPage()
    {
        Assert.Equal("https://www.bing.com/", new BrowserSettings().HomePage);
    }

    [Fact]
    public async Task TestConnection_UsesConfiguredEndpointModelAndBearerToken()
    {
        var handler = new RecordingHandler();
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var endpoint = new Uri("https://api.example.test/v1/chat/completions");

        await client.TestConnectionAsync(endpoint, "custom-model", "secret-token");

        Assert.Equal(endpoint, handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-token", handler.AuthorizationParameter);
        Assert.Contains("\"model\":\"custom-model\"", handler.Body);
        Assert.Contains("\"stream\":false", handler.Body);
    }

    [Fact]
    public async Task ListModels_ReturnsSortedDistinctModelIds()
    {
        var handler = new RecordingHandler
        {
            ResponseJson = "{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"a-model\"}]}"
        };
        var client = new OpenAiCompatibleClient(new HttpClient(handler));

        var models = await client.ListModelsAsync(new Uri("https://api.example.test/v1/models"), "key");

        Assert.Equal(new[] { "a-model", "z-model" }, models);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string ResponseJson { get; set; } = "{\"choices\":[]}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
