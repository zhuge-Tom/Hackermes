using Hackermes.Base.Events;
using Hackermes.Cdp.Session;
using Hackermes.Inspector.Services;
using Hackermes.Inspector.ViewModels;
using Hackermes.Platform.Events;
using Hackermes.Platform.Registries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PageInspectionServiceTests
{
    [Fact]
    public async Task Inspectors_use_selected_page_and_deserialize_bounded_results()
    {
        var first = new FakeSession("page-first");
        var selected = new FakeSession("page-selected");
        var registry = new FakeRegistry(first, selected);
        var events = new EventBus();
        string? openedUrl = null;
        events.Subscribe<OpenBrowserTabRequestedEvent>(value => openedUrl = value.Url);
        using var service = new PageInspectionService(registry, events);
        events.Publish(new ActiveContentTabChangedEvent(selected.PageId, "Selected"));

        var dom = await service.ReadDomAsync(CancellationToken.None);
        var storage = await service.ReadStorageAsync(CancellationToken.None);
        var resources = await service.ReadResourcesAsync(CancellationToken.None);
        var highlighted = await service.HighlightResourceElementsAsync(resources[0].Url, CancellationToken.None);
        var highlightedDom = await service.HighlightDomElementAsync("1/0", CancellationToken.None);
        var details = await service.ReadDomNodeDetailsAsync("1/0", CancellationToken.None);
        var cssResult = await service.ApplyInlineCssAsync("1/0", dom[3].NodeKey, "color: rebeccapurple;", CancellationToken.None);
        var ruleResult = await service.ApplyCssRuleAsync("r1", "display: grid;", CancellationToken.None);
        service.OpenResourceInBrowser(resources[0].Url);

        Assert.Equal("HTML", dom[0].NodeName);
        Assert.False(string.IsNullOrWhiteSpace(dom[3].NodeKey));
        Assert.Equal("localStorage", storage[0].Area);
        Assert.Equal("script", resources[0].Type);
        Assert.Equal(1, resources[0].ElementCount);
        Assert.Equal("script#app", resources[0].ElementSummary);
        Assert.Equal(2, highlighted);
        Assert.True(highlightedDom);
        Assert.NotNull(details);
        Assert.Equal("div#root.page", details!.Selector);
        Assert.Equal("class", details.Attributes[0].Name);
        Assert.Equal("display", details.ComputedStyles[0].Name);
        Assert.Single(details.MatchedRules!);
        Assert.True(cssResult.Applied);
        Assert.Equal("color: rebeccapurple;", cssResult.StyleText);
        Assert.True(ruleResult.Applied);
        Assert.Equal(resources[0].Url, openedUrl);
        Assert.Equal(3, selected.Expressions.Count(expression => expression.Contains("2000", StringComparison.Ordinal)));
        Assert.Contains(selected.Expressions, expression => expression.Contains("outline", StringComparison.Ordinal));
        Assert.Contains(selected.Expressions, expression => expression.Contains("scrollIntoView", StringComparison.Ordinal));
        Assert.Empty(first.Expressions);
    }

    [Fact]
    public async Task Dom_view_model_builds_a_nested_outline_from_depths()
    {
        var session = new FakeSession("page-selected");
        var events = new EventBus();
        using var service = new PageInspectionService(new FakeRegistry(session), events);
        var viewModel = new DomInspectorViewModel(service);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        var html = Assert.Single(viewModel.RootItems);
        Assert.Equal("HTML", html.Item.NodeName);
        Assert.Equal(2, html.Children.Count);
        Assert.Equal("HEAD", html.Children[0].Item.NodeName);
        Assert.Single(html.Children[1].Children);
        Assert.Equal("DIV", html.Children[1].Children[0].Item.NodeName);
    }

    [Fact]
    public async Task Dom_tab_activation_loads_the_latest_tree_without_manual_refresh()
    {
        var session = new FakeSession("page-selected");
        var events = new EventBus();
        using var service = new PageInspectionService(new FakeRegistry(session), events);
        events.Publish(new ActiveContentTabChangedEvent(session.PageId, "Selected"));
        var viewModel = new DomInspectorViewModel(service);

        Assert.IsAssignableFrom<ITabActivationAware>(viewModel);
        await viewModel.ActivateAsync();

        Assert.NotEmpty(viewModel.RootItems);
        Assert.Contains("elements", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(session.Expressions, expression => expression.Contains("document.documentElement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Picker_installation_uses_a_cdp_binding_and_a_hover_click_overlay()
    {
        var session = new FakeSession("page-selected");
        using var service = new PageInspectionService(new FakeRegistry(session), new EventBus());

        await service.SetPickerEnabledAsync(session.PageId, true, CancellationToken.None);

        Assert.Contains(session.Methods, method => method == "Runtime.addBinding");
        Assert.Contains(session.Expressions, expression => expression.Contains("mousemove", StringComparison.Ordinal));
        Assert.Contains(session.Expressions, expression => expression.Contains("stopImmediatePropagation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dom_navigation_discards_stale_tree_items()
    {
        var session = new FakeSession("page-selected");
        var events = new EventBus();
        using var service = new PageInspectionService(new FakeRegistry(session), events);
        var viewModel = new DomInspectorViewModel(service);
        events.Publish(new ActiveContentTabChangedEvent(session.PageId, "Selected"));
        await viewModel.RefreshCommand.ExecuteAsync(null);

        events.Publish(new BrowserPageNavigatedEvent(session.PageId, "https://example.test/next"));

        Assert.Empty(viewModel.RootItems);
        Assert.Contains("stale DOM nodes were discarded", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dom_hover_preview_uses_an_overlay_without_scrolling_the_page()
    {
        var session = new FakeSession("page-selected");
        var events = new EventBus();
        using var service = new PageInspectionService(new FakeRegistry(session), events);
        events.Publish(new ActiveContentTabChangedEvent(session.PageId, "Selected"));

        var previewed = await service.PreviewDomElementAsync("1/0", "n4", CancellationToken.None);

        Assert.True(previewed);
        var expression = Assert.Single(session.Expressions);
        Assert.Contains("__hackermes-inspector-preview__", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollIntoView", expression, StringComparison.Ordinal);
    }

    private sealed class FakeRegistry(params ICdpSession[] sessions) : ICdpSessionRegistry
    {
        public IReadOnlyList<ICdpSession> All { get; } = sessions;
        public event Action<ICdpSession>? SessionOpened;
        public event Action<string>? SessionClosed;
        public ICdpSession? Get(string pageId) => Array.Find(sessions, item => item.PageId == pageId);
        public IDisposable Register(ICdpSession session) => throw new NotSupportedException();
    }

    private sealed class FakeSession(string pageId) : ICdpSession
    {
        public string PageId { get; } = pageId;
        public bool IsAlive => true;
        public List<string> Expressions { get; } = [];
        public List<string> Methods { get; } = [];
        public Task<string> SendAsync(string method, string? parametersJson = null, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);
            using var parameters = JsonDocument.Parse(parametersJson!);
            var expression = parameters.RootElement.TryGetProperty("expression", out var valueElement)
                ? valueElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrEmpty(expression)) Expressions.Add(expression);
            if (method != "Runtime.evaluate") return Task.FromResult("{\"result\":{}}");
            object value = expression.Contains("scrollIntoView", StringComparison.Ordinal)
                ? true
                : expression.Contains("let overlay=document.getElementById('__hackermes-inspector-preview__')", StringComparison.Ordinal)
                ? true
                : expression.Contains("const target=", StringComparison.Ordinal)
                ? 2
                : expression.Contains("getComputedStyle", StringComparison.Ordinal)
                ? new
                {
                    selector = "div#root.page",
                    path = "0",
                    childCount = 0,
                    attributes = new[] { new { name = "class", value = "page" } },
                    computedStyles = new[] { new { name = "display", value = "block" } },
                    resourceUrl = (string?)null,
                    matchedRules = new[] { new { ruleKey = "r1", selector = ".page", cssText = "display: block;", source = "inline", isInline = true } }
                }
                : expression.Contains("rules?.get", StringComparison.Ordinal)
                ? new { applied = true, error = (string?)null, styleText = "display: grid;" }
                : expression.Contains("setAttribute('style'", StringComparison.Ordinal)
                ? new { applied = true, error = (string?)null, styleText = "color: rebeccapurple;" }
                : expression.Contains("documentElement", StringComparison.Ordinal)
                ? new[]
                {
                    new { depth = 0, nodeName = "HTML", id = (string?)null, classes = (string?)null, text = (string?)null, path = "", childCount = 2, nodeKey = "n1" },
                    new { depth = 1, nodeName = "HEAD", id = (string?)null, classes = (string?)null, text = (string?)null, path = "0", childCount = 0, nodeKey = "n2" },
                    new { depth = 1, nodeName = "BODY", id = (string?)null, classes = (string?)null, text = (string?)null, path = "1", childCount = 1, nodeKey = "n3" },
                    new { depth = 2, nodeName = "DIV", id = (string?)"root", classes = (string?)"page", text = (string?)"hello", path = "1/0", childCount = 0, nodeKey = "n4" }
                }
                : expression.Contains("localStorage", StringComparison.Ordinal)
                    ? new[] { new { area = "localStorage", key = "theme", value = "dark" } }
                    : new[] { new { type = "script", name = "app.js", url = "https://example.test/app.js", transferSize = 123L, duration = 4.5, elementCount = 1, elementSummary = "script#app" } };
            return Task.FromResult(JsonSerializer.Serialize(new { result = new { type = "object", value } }));
        }
        public Task<IDisposable> SubscribeAsync(string eventName, Action<CdpEventArgs> handler, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(new Subscription());
        public Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
