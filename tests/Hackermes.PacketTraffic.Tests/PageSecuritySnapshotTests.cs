using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.Inspector.Services;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PageSecuritySnapshotTests
{
    [Fact]
    public async Task Snapshot_is_bound_to_the_exact_invocation_page()
    {
        var pages = new FakePages(new(
            "page-selected", "https://selected.test/path?token=secret#fragment", "Selected", true, true));
        var runtime = new FakeRuntime("page-selected", DomResult(new
        {
            formCount = 0,
            forms = Array.Empty<object>(),
            externalScriptCount = 0,
            inlineScriptCount = 0,
            externalScripts = Array.Empty<object>(),
            passwordInputCount = 0,
            hiddenInputCount = 0,
            mixedContentResourceCount = 0
        }));
        var network = new FakeNetwork();
        var service = new PageSecuritySnapshotService(pages, runtime, network);

        var snapshot = await service.ReadAsync("page-selected");

        Assert.Equal("page-selected", snapshot.PageId);
        Assert.Equal("page-selected", runtime.PageId);
        Assert.Equal("page-selected", network.PageId);
        Assert.Equal("https://selected.test/path", snapshot.Url);
        Assert.Equal("https://selected.test", snapshot.Origin);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_bounds_lists_and_never_exposes_sensitive_values_or_url_components()
    {
        var forms = new List<object>();
        for (var index = 0; index < 55; index++)
            forms.Add(new { method = "post", action = $"https://user:password@forms.test/submit/{index}?token=super-secret#private", isCrossOrigin = true, inputCount = 4, passwordInputCount = 1, autocompleteDisabled = false });
        var scripts = new List<object>();
        for (var index = 0; index < 130; index++)
            scripts.Add(new { source = $"https://cdn.test/script/{index}.js?access_token=super-secret#private", origin = "https://cdn.test", isCrossOrigin = true, hasIntegrity = false, crossOriginMode = "anonymous" });
        var pages = new FakePages(new("page-limits", "https://user:password@target.test/app?token=super-secret#private", new string('T', 500), true, true));
        var runtime = new FakeRuntime("page-limits", DomResult(new
        {
            formCount = 55, forms,
            externalScriptCount = 130, inlineScriptCount = 8, externalScripts = scripts,
            passwordInputCount = 55, hiddenInputCount = 70, mixedContentResourceCount = 3
        }));
        var service = new PageSecuritySnapshotService(pages, runtime, new FakeNetwork());

        var snapshot = await service.ReadAsync("page-limits");
        var json = JsonSerializer.Serialize(snapshot);

        Assert.Equal(PageSecuritySnapshotService.MaximumForms, snapshot.Dom.Forms.Count);
        Assert.Equal(15, snapshot.Dom.TruncatedFormCount);
        Assert.Equal(PageSecuritySnapshotService.MaximumExternalScripts, snapshot.Dom.ExternalScripts.Count);
        Assert.Equal(30, snapshot.Dom.TruncatedScriptCount);
        Assert.Equal(PageSecuritySnapshotService.MaximumTitleCharacters, snapshot.Title.Length);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password@", json, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("?", snapshot.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("#", snapshot.Url, StringComparison.Ordinal);
        Assert.All(snapshot.Dom.Forms, form => Assert.DoesNotContain("?", form.Action, StringComparison.Ordinal));
        Assert.All(snapshot.Dom.ExternalScripts, script => Assert.DoesNotContain("?", script.Source, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_emits_value_free_observation_codes()
    {
        var pages = new FakePages(new(
            "page-obs", "https://selected.test/app?token=super-secret#fragment", "App", true, true));
        var runtime = new FakeRuntime("page-obs", DomResult(new
        {
            formCount = 1,
            forms = new object[]
            {
                new
                {
                    method = "post",
                    action = "https://other.test/submit?token=super-secret",
                    isCrossOrigin = true,
                    inputCount = 2,
                    passwordInputCount = 0,
                    autocompleteDisabled = false
                }
            },
            externalScriptCount = 1,
            inlineScriptCount = 0,
            externalScripts = new object[]
            {
                new
                {
                    source = "https://cdn.test/app.js?access_token=super-secret",
                    origin = "https://cdn.test",
                    isCrossOrigin = true,
                    hasIntegrity = false,
                    crossOriginMode = "anonymous"
                }
            },
            passwordInputCount = 0,
            hiddenInputCount = 0,
            mixedContentResourceCount = 2
        }));
        var network = new FakeNetwork(new NetworkSecurityMetadata(
            true, 200, false, false, false, [], false, false, false,
            false, false, false, false, false, false, false,
            new PageSecurityCookieSummary(2, 0, 1, 0, 0, 0, 0)));
        var service = new PageSecuritySnapshotService(pages, runtime, network);

        var snapshot = await service.ReadAsync("page-obs");
        var codes = snapshot.Observations.Select(item => item.Code).ToArray();
        var json = JsonSerializer.Serialize(snapshot);

        Assert.Contains("missing-hsts", codes);
        Assert.Contains("missing-csp", codes);
        Assert.Contains("missing-xcto", codes);
        Assert.Contains("missing-frame-protection", codes);
        Assert.Contains("cookie-missing-secure", codes);
        Assert.Contains("cookie-missing-httponly", codes);
        Assert.Contains("mixed-content", codes);
        Assert.Contains("cross-origin-form", codes);
        Assert.Contains("script-missing-integrity", codes);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", json, StringComparison.Ordinal);
        Assert.All(snapshot.Observations, item =>
        {
            Assert.DoesNotContain("?", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("access_token", item.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Snapshot_fails_closed_for_unknown_closed_or_navigated_page()
    {
        var page = new PageContextObservation("page-one", "https://one.test/", "One", true, true);
        var pages = new FakePages(page);
        var runtime = new FakeRuntime("page-one", DomResult(new
        {
            formCount = 0, forms = Array.Empty<object>(), externalScriptCount = 0,
            inlineScriptCount = 0, externalScripts = Array.Empty<object>(), passwordInputCount = 0,
            hiddenInputCount = 0, mixedContentResourceCount = 0
        }));
        var service = new PageSecuritySnapshotService(pages, runtime, new FakeNetwork());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync("page-unknown"));
        pages.Page = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync("page-one"));

        pages.Page = page;
        runtime.AfterEvaluate = () => pages.Page = page with { Url = "https://one.test/navigated" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync("page-one"));
    }

    [Fact]
    public async Task Ai_tool_is_read_only_has_closed_schema_and_forwards_exact_page()
    {
        var snapshots = new RecordingSnapshotService();
        var registry = new AiToolRegistry();
        new PageSecuritySnapshotToolAdapter(snapshots).RegisterAll(registry);
        var tool = Assert.Single(registry.All);

        var missing = await tool.Handler(new ToolInvocation(tool.Name, JsonSerializer.SerializeToElement(new { })), default);
        var result = await tool.Handler(new ToolInvocation(tool.Name, JsonSerializer.SerializeToElement(new { }), "page-exact"), default);

        Assert.False(missing.Success);
        Assert.True(result.Success);
        Assert.Equal("page-exact", snapshots.PageId);
        Assert.Equal(AiToolRisk.ReadOnly, tool.Risk);
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task Network_store_derives_security_and_cookie_flags_without_retaining_values_in_snapshot()
    {
        var session = new EventSession("page-secure");
        var store = new NetworkStore(new SingleSessionRegistry(session), new EventBus(), new NullLogger());
        await session.WaitForSubscriptionAsync("Network.responseReceived");
        session.Raise("Network.requestWillBeSent", """
            {"requestId":"doc-1","type":"Document","request":{"method":"GET","url":"https://secure.test/app?view=1"}}
            """);
        session.Raise("Network.responseReceived", """
            {"requestId":"doc-1","response":{"status":200,"statusText":"OK","mimeType":"text/html","headers":{
              "strict-transport-security":"max-age=31536000",
              "content-security-policy":"default-src 'self'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'",
              "x-content-type-options":"nosniff",
              "referrer-policy":"strict-origin-when-cross-origin",
              "permissions-policy":"camera=()",
              "cross-origin-opener-policy":"same-origin",
              "set-cookie":"session=super-secret-token; Path=/; Secure; HttpOnly; SameSite=Strict\npreference=private-value; Secure; SameSite=None; Partitioned"
            }}}
            """);

        var metadata = store.ReadSecurityMetadata("page-secure", "https://secure.test/app?view=1#fragment");
        var serialized = JsonSerializer.Serialize(metadata);

        Assert.True(metadata.HasDocumentResponse);
        Assert.True(metadata.HasStrictTransportSecurity);
        Assert.True(metadata.HasContentSecurityPolicy);
        Assert.True(metadata.ContentSecurityPolicyAllowsUnsafeInline);
        Assert.True(metadata.HasFrameProtection);
        Assert.True(metadata.HasXContentTypeOptions);
        Assert.Equal(2, metadata.Cookies.SetCookieCount);
        Assert.Equal(2, metadata.Cookies.SecureCount);
        Assert.Equal(1, metadata.Cookies.HttpOnlyCount);
        Assert.Equal(1, metadata.Cookies.SameSiteStrictCount);
        Assert.Equal(1, metadata.Cookies.SameSiteNoneCount);
        Assert.Equal(1, metadata.Cookies.PartitionedCount);
        Assert.DoesNotContain("super-secret-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", serialized, StringComparison.Ordinal);
        Assert.False(store.ReadSecurityMetadata("page-other", "https://secure.test/app?view=1").HasDocumentResponse);
    }

    private static string DomResult(object value) => JsonSerializer.Serialize(new
    {
        result = new { value }
    });

    private sealed class FakePages(PageContextObservation page) : IPageContextQueryService
    {
        public PageContextObservation? Page { get; set; } = page;
        public PageContextObservation? Read(string pageId) =>
            Page is not null && string.Equals(pageId, Page.PageId, StringComparison.Ordinal) ? Page : null;
    }

    private sealed class FakeRuntime(string expectedPageId, string response) : IPageAgentRuntime
    {
        public string? PageId { get; private set; }
        public Action? AfterEvaluate { get; set; }
        public PageAgentRuntimeCapability GetCapability(string pageId) =>
            new(pageId, PageAgentWorldState.Ready, PageAgentWorldState.Ready);

        public Task<string> EvaluateInIsolatedWorldAsync(
            string pageId, string expression, CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedPageId, pageId);
            PageId = pageId;
            AfterEvaluate?.Invoke();
            return Task.FromResult(response);
        }
    }

    private sealed class FakeNetwork(NetworkSecurityMetadata? metadata = null) : INetworkSecurityMetadataQueryService
    {
        public string? PageId { get; private set; }
        public NetworkSecurityMetadata ReadSecurityMetadata(string pageId, string documentUrl)
        {
            PageId = pageId;
            return metadata ?? NetworkSecurityMetadata.Empty;
        }
    }

    private sealed class RecordingSnapshotService : IPageSecuritySnapshotService
    {
        public string? PageId { get; private set; }
        public Task<PageSecuritySnapshot> ReadAsync(string pageId, CancellationToken cancellationToken = default)
        {
            PageId = pageId;
            return Task.FromResult(new PageSecuritySnapshot(
                pageId, "https://safe.test/", "https://safe.test", "Safe",
                new PageSecurityTransportSnapshot(false, 0, true, false, false, false, [], false, false, false, false, false, false, false, false, false, false, new(0, 0, 0, 0, 0, 0, 0)),
                new PageSecurityDomSnapshot(0, 0, [], 0, 0, 0, [], 0, 0, 0)));
        }
    }

    private sealed class SingleSessionRegistry(ICdpSession session) : ICdpSessionRegistry
    {
        public IReadOnlyList<ICdpSession> All { get; } = [session];
        public ICdpSession? Get(string pageId) => string.Equals(pageId, session.PageId, StringComparison.Ordinal) ? session : null;
        public IDisposable Register(ICdpSession value) => throw new NotSupportedException();
        public event Action<ICdpSession>? SessionOpened;
        public event Action<string>? SessionClosed;
    }

    private sealed class EventSession(string pageId) : ICdpSession
    {
        private readonly Dictionary<string, Action<CdpEventArgs>> _handlers = new(StringComparer.Ordinal);
        public string PageId { get; } = pageId;
        public bool IsAlive => true;
        public Task<string> SendAsync(string method, string? parametersJson = null, CancellationToken cancellationToken = default) => Task.FromResult("{}");
        public Task<IDisposable> SubscribeAsync(string eventName, Action<CdpEventArgs> handler, CancellationToken cancellationToken = default)
        {
            lock (_handlers) _handlers[eventName] = handler;
            return Task.FromResult<IDisposable>(new Subscription());
        }
        public Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Raise(string eventName, string parametersJson)
        {
            Action<CdpEventArgs> handler;
            lock (_handlers) handler = _handlers[eventName];
            handler(new CdpEventArgs(eventName, parametersJson));
        }
        public async Task WaitForSubscriptionAsync(string eventName)
        {
            for (var index = 0; index < 100; index++)
            {
                lock (_handlers) if (_handlers.ContainsKey(eventName)) return;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Subscription '{eventName}' was not installed.");
        }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
