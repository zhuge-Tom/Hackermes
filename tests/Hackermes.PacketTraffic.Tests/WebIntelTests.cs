using Hackermes.App;
using Hackermes.AiPanel.Agent;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class WebIntelTests
{
    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? SubscriptionToken, string? ApiKey)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-Subscription-Token", out var token) ? token.First() : null,
                request.Headers.TryGetValues("X-API-KEY", out var apiKey) ? apiKey.First() : null));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public void Set(string key, string? value) { if (value is null) _values.Remove(key); else _values[key] = value; }
        public bool Contains(string key) => _values.ContainsKey(key);
        public void Remove(string key) => _values.Remove(key);
    }

    private sealed class TempSettings(string path) : ISettingsService
    {
        private readonly AppSettings _value = new();
        public AppSettings Load() => _value;
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null) { mutate(_value); return true; }
        public string SettingsFilePath => path;
    }

    private static (string Dir, TempSettings Settings) TempStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hackermes-webintel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, new TempSettings(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public async Task BraveApiSearchParsesBoundedResults()
    {
        var (_, settings) = TempStore();
        var secrets = new MemorySecrets();
        secrets.Set("ai.webSearchApiKey", "brave-key");
        FakeHandler handler = new(_ =>
        {
            const string body = """{"web":{"results":[{"title":"CVE-2021-44228 writeup","url":"https://example.com/a","description":"log4j shell"},{"title":"second","url":"https://example.com/b","description":"more"}]}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var service = new WebSearchService(new HttpClient(handler), settings, secrets, new NullLogger());
        settings.Update(s => { s.Ai.WebSearchProvider = "brave"; }, SettingsSection.Ai);

        var outcome = await service.SearchAsync("log4j poc", 1, CancellationToken.None);

        Assert.Equal("brave", outcome.Provider);
        var result = Assert.Single(outcome.Results);
        Assert.Equal("https://example.com/a", result.Url);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("api.search.brave.com/res/v1/web/search", request.Uri);
        Assert.Equal("brave-key", request.SubscriptionToken);
    }

    [Fact]
    public async Task SerperApiSearchParsesOrganicResults()
    {
        var (_, settings) = TempStore();
        var secrets = new MemorySecrets();
        secrets.Set("ai.webSearchApiKey", "serper-key");
        FakeHandler handler = new(_ =>
        {
            const string body = """{"organic":[{"title":"nacos exploit","link":"https://example.com/n","snippet":"unauth"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var service = new WebSearchService(new HttpClient(handler), settings, secrets, new NullLogger());
        settings.Update(s => { s.Ai.WebSearchProvider = "serper"; }, SettingsSection.Ai);

        var outcome = await service.SearchAsync("nacos", 5, CancellationToken.None);

        Assert.Equal("serper", outcome.Provider);
        Assert.Equal("https://example.com/n", Assert.Single(outcome.Results).Url);
        var serperRequest = Assert.Single(handler.Requests);
        Assert.Contains("google.serper.dev/search", serperRequest.Uri);
        Assert.Equal("serper-key", serperRequest.ApiKey);
    }

    [Fact]
    public async Task AutoProviderWithoutKeyFallsBackToBrowserAndNeedsDesktopServices()
    {
        var (_, settings) = TempStore();
        var service = new WebSearchService(new HttpClient(new FakeHandler(_ =>
            throw new InvalidOperationException("no HTTP call expected"))), settings, new MemorySecrets(), new NullLogger());
        settings.Update(s => { s.Ai.WebSearchProvider = "auto"; }, SettingsSection.Ai);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchAsync("anything", 5, CancellationToken.None).AsTask());
        Assert.Contains("embedded browser", exception.Message);
    }

    [Fact]
    public async Task CveLookupPrefersNvdAndFallsBackToOsv()
    {
        var (dir, settings) = TempStore();
        var responses = new Queue<string>([
            """{"totalResults":0}""",
            """{"id":"CVE-2021-44228","summary":"RCE in log4j","published":"2021-12-10","severity":[{"type":"CVSS_V3","score":"CVSS:3.1/AV:N/AC:L"}],"references":[{"url":"https://example.com/ref"}]}"""
        ]);
        FakeHandler handler = new(request =>
        {
            var body = responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var service = new WebSearchService(new HttpClient(handler), settings, new MemorySecrets(), new NullLogger());

        var payload = await service.LookupCveAsync("cve-2021-44228", CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal("osv", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("RCE in log4j", document.RootElement.GetProperty("summary").GetString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("services.nvd.nist.gov", handler.Requests[0].Uri);
        Assert.Contains("api.osv.dev", handler.Requests[1].Uri);

        // NVD happy path: description, CVSS and bounded references are projected.
        FakeHandler nvdHandler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"totalResults":1,"vulnerabilities":[{"cve":{"id":"CVE-2021-44228","vulnStatus":"Analyzed","published":"2021-12-10","lastModified":"2021-12-20","descriptions":[{"lang":"en","value":"Apache Log4j2 JNDI RCE"}],"metrics":{"cvssMetricV31":[{"cvssData":{"baseScore":10.0,"baseSeverity":"CRITICAL","vectorString":"CVSS:3.1/AV:N"}}]},"references":[{"url":"https://a"},{"url":"https://b"}]}}]}""", Encoding.UTF8, "application/json")
        });
        var nvdService = new WebSearchService(new HttpClient(nvdHandler), settings, new MemorySecrets(), new NullLogger());
        var nvdPayload = await nvdService.LookupCveAsync("CVE-2021-44228", CancellationToken.None);
        using var nvd = System.Text.Json.JsonDocument.Parse(nvdPayload);
        Assert.Equal("nvd", nvd.RootElement.GetProperty("source").GetString());
        Assert.Equal(10.0, nvd.RootElement.GetProperty("cvssScore").GetDouble());
        Assert.Equal("CRITICAL", nvd.RootElement.GetProperty("severity").GetString());
        Assert.Contains("Apache Log4j2 JNDI RCE", nvd.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public void CveLookupRejectsMalformedIds()
    {
        var (_, settings) = TempStore();
        var service = new WebSearchService(new HttpClient(new FakeHandler(_ =>
            throw new InvalidOperationException("no HTTP call expected"))), settings, new MemorySecrets(), new NullLogger());
        Assert.Throws<ArgumentException>(() => service.LookupCveAsync("not-a-cve; rm", CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void BingExtractionExpressionParsesEvaluateResponse()
    {
        var expression = WebSearchService.BuildExtractionExpression(5);
        Assert.Contains("#b_results > li.b_algo", expression);
        var items = WebSearchService.ParseEvaluateResults(
            """{"result":{"type":"string","value":"[{\"title\":\"T\",\"url\":\"https://e/1\",\"snippet\":\"s\"}]"}}""", 5);
        var result = Assert.Single(items.Items);
        Assert.Equal("https://e/1", result.Url);

        var empty = WebSearchService.ParseEvaluateResults("""{"result":{"type":"string","value":"[]"}}""", 5);
        Assert.Empty(empty.Items);
        Assert.NotNull(empty.Note);
    }

    [Fact]
    public void ArtifactListAndReadServeTextAndRefuseBinaries()
    {
        var (dir, settings) = TempStore();
        var store = new AgentArtifactStore(new HttpClient(), settings);
        var storage = Path.Combine(dir, "agent-tools");
        Directory.CreateDirectory(storage);
        File.WriteAllText(Path.Combine(storage, "log4j-poc.txt"), "POC line one\nPOC line two");
        File.WriteAllBytes(Path.Combine(storage, "implant.exe"), [0x4D, 0x5A, 0x00, 0x01]);

        var listed = store.List().Select(item => item.FileName).ToArray();
        Assert.Contains("log4j-poc.txt", listed);
        Assert.Contains("implant.exe", listed);

        var page = store.ReadText("log4j-poc.txt", 13, 4);
        Assert.Equal("POC ", page.Content);
        Assert.Equal(13, page.Offset);
        Assert.Equal(17, page.NextOffset);
        Assert.Equal(25, page.TotalChars);

        var binary = Assert.Throws<InvalidOperationException>(() => store.ReadText("implant.exe", 0, 100));
        Assert.Contains("binary", binary.Message, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<Exception>(() => store.ReadText("../outside.txt", 0, 100));
    }
}
