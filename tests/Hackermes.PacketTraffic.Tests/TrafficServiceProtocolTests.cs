using Hackermes.Base.Diagnostics;
using Hackermes.Cdp.Session;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using Hackermes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficServiceProtocolTests
{
    [Fact]
    public async Task Continue_request_omits_unset_optional_cdp_fields()
    {
        var (service, session, store) = CreateService();
        await using var lifetime = service;
        var message = PausedMessage("page-test:fetch-1", TrafficStage.Request);
        store.Import(message);
        service.SetModificationsEnabled(true);

        await service.ContinueAsync(message.Id, new TrafficRequestEdit(Body: [0, 1, 255]));

        var call = Assert.Single(session.Calls, item => item.Method == "Fetch.continueRequest");
        using var json = JsonDocument.Parse(call.ParametersJson);
        Assert.Equal("fetch-1", json.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("AAH/", json.RootElement.GetProperty("postData").GetString());
        Assert.False(json.RootElement.TryGetProperty("url", out _));
        Assert.False(json.RootElement.TryGetProperty("method", out _));
        Assert.False(json.RootElement.TryGetProperty("headers", out _));
    }

    [Fact]
    public async Task Fulfill_response_omits_unset_optional_cdp_fields()
    {
        var (service, session, store) = CreateService();
        await using var lifetime = service;
        var message = PausedMessage("page-test:fetch-2", TrafficStage.Response);
        store.Import(message);
        service.SetModificationsEnabled(true);

        await service.FulfillAsync(message.Id, new TrafficResponseEdit(Status: 204));

        var call = Assert.Single(session.Calls, item => item.Method == "Fetch.fulfillRequest");
        using var json = JsonDocument.Parse(call.ParametersJson);
        Assert.Equal("fetch-2", json.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(204, json.RootElement.GetProperty("responseCode").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("responsePhrase", out _));
        Assert.False(json.RootElement.TryGetProperty("responseHeaders", out _));
        Assert.False(json.RootElement.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Concurrent_capture_start_registers_only_one_fetch_subscription()
    {
        var (service, session, _) = CreateService();
        await using var lifetime = service;
        session.SubscriptionDelay = TimeSpan.FromMilliseconds(80);

        await Task.WhenAll(
            service.StartCaptureAsync(session.PageId),
            service.StartCaptureAsync(session.PageId));

        Assert.Equal(1, session.SubscriptionCount);
        Assert.Equal(1, session.Calls.Count(item => item.Method == "Fetch.enable"));
    }

    private static (TrafficService Service, FakeSession Session, TrafficStore Store) CreateService()
    {
        var session = new FakeSession("page-test");
        var store = new TrafficStore();
        var service = new TrafficService(new FakeRegistry(session), store, new TrafficRuleSet(), new NullLogger());
        return (service, session, store);
    }

    private static TrafficMessage PausedMessage(string id, TrafficStage stage) => new(
        id, "page-test", stage, TrafficState.Paused, "POST", "https://example.test/echo",
        [], null, stage == TrafficStage.Response ? 200 : null, stage == TrafficStage.Response ? "OK" : null,
        [], null, "Fetch", DateTimeOffset.UtcNow);

    private sealed class FakeRegistry(FakeSession session) : ICdpSessionRegistry
    {
        public IReadOnlyList<ICdpSession> All { get; } = [session];
        public event Action<ICdpSession>? SessionOpened;
        public event Action<string>? SessionClosed;
        public ICdpSession? Get(string pageId) => pageId == session.PageId ? session : null;
        public IDisposable Register(ICdpSession value) => throw new NotSupportedException();
    }

    private sealed class FakeSession(string pageId) : ICdpSession
    {
        public string PageId { get; } = pageId;
        public bool IsAlive => true;
        public List<(string Method, string ParametersJson)> Calls { get; } = [];
        public TimeSpan SubscriptionDelay { get; set; }
        public int SubscriptionCount { get; private set; }

        public Task<string> SendAsync(string method, string? parametersJson = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, parametersJson ?? "{}"));
            return Task.FromResult("{}");
        }

        public async Task<IDisposable> SubscribeAsync(string eventName, Action<CdpEventArgs> handler, CancellationToken cancellationToken = default)
        {
            SubscriptionCount++;
            if (SubscriptionDelay > TimeSpan.Zero) await Task.Delay(SubscriptionDelay, cancellationToken);
            return new Subscription();
        }

        public Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
