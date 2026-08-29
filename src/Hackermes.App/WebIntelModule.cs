using Hackermes.AiPanel.Tools;
using Hackermes.Base;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.Services;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>One bounded search result handed to the model.</summary>
public sealed record WebSearchResultItem(string Title, string Url, string Snippet);

/// <summary>Outcome of one web_search invocation: provider name plus structured results.</summary>
public sealed record WebSearchOutcome(string Provider, IReadOnlyList<WebSearchResultItem> Results, string? Note = null);

/// <summary>
/// Bounded public-web intelligence for the agent: web_search (Brave/Serper API when a key
/// is configured, otherwise the embedded browser driven against Bing) and CVE lookups
/// (NVD API 2.0 with OSV fallback). Everything is data-only — nothing fetched here is
/// ever executed, and responses are truncated before they reach the model.
/// </summary>
public sealed class WebSearchService
{
    private const int MaxResponseBytes = 1_500_000;
    private const string BraveEndpoint = "https://api.search.brave.com/res/v1/web/search";
    private const string SerperEndpoint = "https://google.serper.dev/search";
    private const string NvdEndpoint = "https://services.nvd.nist.gov/rest/json/cves/2.0";
    private const string OsvEndpoint = "https://api.osv.dev/v1/vulns/";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly IBrowserTabOpener? _tabs;
    private readonly ICdpSessionRegistry? _cdp;
    private readonly IEventBus? _events;
    private readonly IAppLogger _logger;

    public WebSearchService(HttpClient http, ISettingsService settings, ISecretStore secrets,
        IAppLogger logger, IBrowserTabOpener? tabs = null, ICdpSessionRegistry? cdp = null, IEventBus? events = null)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _logger = logger.ForCategory(nameof(WebSearchService));
        _tabs = tabs;
        _cdp = cdp;
        _events = events;
    }

    public async ValueTask<WebSearchOutcome> SearchAsync(string query, int count, CancellationToken ct)
    {
        var settings = _settings.Load().Ai;
        var apiKey = _secrets.Get("ai.webSearchApiKey");
        var provider = (settings.WebSearchProvider ?? "auto").Trim().ToLowerInvariant();
        count = Math.Clamp(count, 1, 10);
        return provider switch
        {
            "browser" => await SearchWithBrowserAsync(query, count, ct).ConfigureAwait(false),
            "brave" => await SearchWithBraveAsync(query, count, apiKey, ct).ConfigureAwait(false),
            "serper" => await SearchWithSerperAsync(query, count, apiKey, ct).ConfigureAwait(false),
            _ => apiKey is { Length: > 0 }
                ? await SearchWithBraveAsync(query, count, apiKey, ct).ConfigureAwait(false)
                : await SearchWithBrowserAsync(query, count, ct).ConfigureAwait(false)
        };
    }

    private async ValueTask<WebSearchOutcome> SearchWithBraveAsync(string query, int count, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Brave Search API key is not configured (AI 设置 → 联网情报)。");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BraveEndpoint}?q={Uri.EscapeDataString(query)}&count={count}");
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.Add("Accept", "application/json");
        var body = await SendBoundedAsync(request, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var results = new List<WebSearchResultItem>();
        if (document.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new WebSearchResultItem(
                    Text(item, "title"), Text(item, "url"), Text(item, "description")));
                if (results.Count >= count) break;
            }
        }
        return new WebSearchOutcome("brave", results);
    }

    private async ValueTask<WebSearchOutcome> SearchWithSerperAsync(string query, int count, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Serper API key is not configured (AI 设置 → 联网情报)。");
        using var request = new HttpRequestMessage(HttpMethod.Post, SerperEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { q = query, num = count }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-KEY", apiKey);
        var body = await SendBoundedAsync(request, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var results = new List<WebSearchResultItem>();
        if (document.RootElement.TryGetProperty("organic", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new WebSearchResultItem(
                    Text(item, "title"), Text(item, "link"), Text(item, "snippet")));
                if (results.Count >= count) break;
            }
        }
        return new WebSearchOutcome("serper", results);
    }

    private async ValueTask<WebSearchOutcome> SearchWithBrowserAsync(string query, int count, CancellationToken ct)
    {
        if (_tabs is null || _cdp is null || _events is null)
            throw new InvalidOperationException("The embedded browser is unavailable in this context; configure a search API key instead.");
        var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(count, 1, 20)}";
        var pageId = _tabs.OpenTab(url);
        try
        {
            var session = await WaitForSessionAsync(pageId, ct).ConfigureAwait(false);
            await WaitDocumentReadyAsync(session, ct).ConfigureAwait(false);
            var responseJson = await session.SendAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = BuildExtractionExpression(count),
                returnByValue = true
            }), ct).ConfigureAwait(false);
            var (items, note) = ParseEvaluateResults(responseJson, count);
            return new WebSearchOutcome("browser", items, note);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warn($"Browser search failed: {exception.Message}");
            throw new InvalidOperationException(
                "内置浏览器搜索失败（搜索引擎 DOM 变更或被拦截）。可在 AI 设置 → 联网情报 配置 Brave/Serper API Key 后重试。" +
                $" 原因：{exception.Message}");
        }
        finally
        {
            _events.Publish(new RemoveDockTabEvent(DockPosition.Content, pageId));
        }
    }

    private async ValueTask<ICdpSession> WaitForSessionAsync(string pageId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (_cdp!.Get(pageId) is { IsAlive: true } session)
                return session;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The search tab never reported a CDP session.");
    }

    private static async ValueTask WaitDocumentReadyAsync(ICdpSession session, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await session.SendAsync("Runtime.evaluate", JsonSerializer.Serialize(new
                {
                    expression = "document.readyState",
                    returnByValue = true
                }), ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("value", out var value) &&
                    string.Equals(value.GetString(), "complete", StringComparison.Ordinal))
                    return;
            }
            catch (CdpException)
            {
                // Navigation not attached yet — keep polling until the deadline.
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    internal static string BuildExtractionExpression(int count) =>
        "(()=>{const n=" + Math.Clamp(count, 1, 20).ToString(CultureInfo.InvariantCulture) + ";" +
        "const out=[];const seen=new Set();" +
        "const nodes=document.querySelectorAll('#b_results > li.b_algo');" +
        "for (const li of nodes){const a=li.querySelector('h2 a')||li.querySelector('a[href^=\"http\"]');" +
        "if(!a||!a.href)continue;const title=(a.textContent||'').trim();if(!title)continue;" +
        "if(seen.has(a.href))continue;seen.add(a.href);" +
        "const cap=li.querySelector('.b_caption p')||li.querySelector('.b_caption')||li.querySelector('p');" +
        "const snippet=((cap&&cap.textContent)||'').trim().slice(0,300);" +
        "out.push({title:title.slice(0,200),url:a.href.slice(0,500),snippet:snippet});" +
        "if(out.length>=n)break;}return JSON.stringify(out);})()";

    internal static (IReadOnlyList<WebSearchResultItem> Items, string? Note) ParseEvaluateResults(string responseJson, int count)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
            return (Array.Empty<WebSearchResultItem>(), "搜索引擎未返回可解析的结果。");
        var items = new List<WebSearchResultItem>();
        using var parsed = JsonDocument.Parse(value.GetString() ?? "[]");
        foreach (var item in parsed.RootElement.EnumerateArray())
        {
            items.Add(new WebSearchResultItem(Text(item, "title"), Text(item, "url"), Text(item, "snippet")));
            if (items.Count >= count) break;
        }
        var note = items.Count == 0 ? "搜索引擎返回 0 条结果（可能被拦截或结果页 DOM 变更）；可改用 API 提供商。" : null;
        return (items, note);
    }

    public async ValueTask<string> LookupCveAsync(string cveId, CancellationToken ct)
    {
        cveId = (cveId ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(cveId, @"^CVE-\d{4}-\d{4,}$"))
            throw new ArgumentException("cveId must look like CVE-2021-44228.");
        var nvdKey = _secrets.Get("ai.nvdApiKey");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{NvdEndpoint}?cveId={Uri.EscapeDataString(cveId)}");
            if (nvdKey is { Length: > 0 })
                request.Headers.Add("apiKey", nvdKey);
            var body = await SendBoundedAsync(request, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var total = document.RootElement.TryGetProperty("totalResults", out var totalElement)
                ? totalElement.GetInt32() : 0;
            if (total > 0 && document.RootElement.TryGetProperty("vulnerabilities", out var list) &&
                list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any())
            {
                var cve = list.EnumerateArray().First().GetProperty("cve");
                return JsonSerializer.Serialize(new
                {
                    source = "nvd",
                    id = Text(cve, "id"),
                    status = Text(cve, "vulnStatus"),
                    published = Text(cve, "published"),
                    lastModified = Text(cve, "lastModified"),
                    description = FirstDescription(cve),
                    severity = ExtractSeverity(cve, out var score, out var vector),
                    cvssScore = score,
                    cvssVector = vector,
                    references = ExtractReferences(cve)
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.Warn($"NVD lookup failed for {cveId}: {exception.Message}");
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{OsvEndpoint}{Uri.EscapeDataString(cveId)}");
            var body = await SendBoundedAsync(request, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(new
            {
                source = "osv",
                id = Text(document.RootElement, "id"),
                summary = Text(document.RootElement, "summary"),
                details = Text(document.RootElement, "details"),
                published = Text(document.RootElement, "published"),
                severity = ExtractOsvSeverity(document.RootElement),
                references = ExtractReferences(document.RootElement)
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.Warn($"OSV lookup failed for {cveId}: {exception.Message}");
            return JsonSerializer.Serialize(new
            {
                source = "none",
                cveId,
                note = "NVD 与 OSV 均未返回该编号（网络受限、限流或编号无效）。可改用 web_search 查询。"
            });
        }
    }

    private static string FirstDescription(JsonElement cve)
    {
        if (cve.TryGetProperty("descriptions", out var descriptions) && descriptions.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in descriptions.EnumerateArray())
                if (Text(entry, "lang") == "en")
                    return Text(entry, "value");
            return Text(descriptions.EnumerateArray().First(), "value");
        }
        return string.Empty;
    }

    private static string ExtractSeverity(JsonElement cve, out double score, out string vector)
    {
        score = 0; vector = string.Empty;
        foreach (var family in new[] { "cvssMetricV31", "cvssMetricV30", "cvssMetricV2" })
        {
            if (!cve.TryGetProperty("metrics", out var metrics) ||
                !metrics.TryGetProperty(family, out var list) || list.ValueKind != JsonValueKind.Array ||
                !list.EnumerateArray().Any())
                continue;
            var data = list.EnumerateArray().First();
            if (data.TryGetProperty("cvssData", out var cvss))
            {
                if (cvss.TryGetProperty("baseScore", out var baseScore)) score = baseScore.GetDouble();
                vector = Text(cvss, "vectorString");
                return Text(cvss, "baseSeverity", "UNKNOWN");
            }
        }
        return "UNKNOWN";
    }

    private static string[] ExtractReferences(JsonElement element)
    {
        if (!element.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Array)
            return [];
        return references.EnumerateArray()
            .Select(entry => Text(entry, "url"))
            .Where(url => url.Length > 0)
            .Take(8)
            .ToArray();
    }

    private static string ExtractOsvSeverity(JsonElement element)
    {
        if (element.TryGetProperty("severity", out var severities) && severities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in severities.EnumerateArray())
                if (Text(entry, "type") == "CVSS_V3")
                    return Text(entry, "score");
        }
        return "UNKNOWN";
    }

    private async ValueTask<string> SendBoundedAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[MaxResponseBytes];
        var read = await reader.ReadAsync(buffer.AsMemory(0, MaxResponseBytes), ct).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback : fallback;
}

/// <summary>Registers web_search and vuln_cve_lookup for the agent (data-only web intelligence).</summary>
public sealed class WebIntelModule : IModule
{
    public string Name => "Web Intelligence";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<WebSearchService>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var registry = serviceProvider.GetRequiredService<IAiToolRegistry>();
        var search = serviceProvider.GetRequiredService<WebSearchService>();

        registry.Register(new AiToolDefinition(
            "web_search",
            "Search the public web for vulnerability intelligence and return bounded results " +
            "(title/url/snippet). Uses the configured Brave/Serper API when a key is stored, " +
            "otherwise drives the embedded browser against Bing. Data only — nothing is executed.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "search phrase, at most 200 characters" },
                    count = new { type = "integer", description = "results to return, 1-10 (default 5)" }
                },
                required = new[] { "query" },
                additionalProperties = false
            }),
            AiToolRisk.ReadOnly,
            async (invocation, token) =>
            {
                try
                {
                    var query = (JsonText(invocation.Arguments, "query") ?? string.Empty).Trim();
                    if (query.Length is 0 or > 200)
                        return ToolResult.Fail("query must be 1-200 characters.");
                    var count = Number(invocation.Arguments, "count", 5);
                    var outcome = await search.SearchAsync(query, count, token).ConfigureAwait(false);
                    return ToolResult.Ok(JsonSerializer.Serialize(outcome));
                }
                catch (OperationCanceledException) { return ToolResult.Fail("web_search timed out."); }
                catch (Exception exception) { return ToolResult.Fail(exception.Message); }
            },
            Timeout: TimeSpan.FromSeconds(90)));

        registry.Register(new AiToolDefinition(
            "vuln_cve_lookup",
            "Look up one CVE's bounded summary (description, CVSS, references) on NVD with OSV " +
            "as fallback. Data only — nothing is executed and no payload is downloaded.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { cveId = new { type = "string", description = "e.g. CVE-2021-44228" } },
                required = new[] { "cveId" },
                additionalProperties = false
            }),
            AiToolRisk.ReadOnly,
            async (invocation, token) =>
            {
                try
                {
                    return ToolResult.Ok(await search.LookupCveAsync(
                        JsonText(invocation.Arguments, "cveId") ?? string.Empty, token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) { return ToolResult.Fail("vuln_cve_lookup timed out."); }
                catch (Exception exception) { return ToolResult.Fail(exception.Message); }
            },
            Timeout: TimeSpan.FromSeconds(45)));
    }

    private static string? JsonText(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static int Number(JsonElement arguments, string name, int fallback) =>
        arguments.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;
}
