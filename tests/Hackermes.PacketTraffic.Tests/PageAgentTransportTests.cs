using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.Services;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.PageAgent;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class PageAgentTransportTests
{
    [Fact]
    public void Direct_message_is_preserved()
    {
        var sut = new PageAgentMessageReassembler();
        const string payload = "{\"t\":\"lifecycle\",\"k\":\"ready\"}";

        Assert.True(sut.TryAccept(payload, out var result));
        Assert.Equal(payload, result);
    }

    [Fact]
    public void Ordered_chunks_are_reassembled_without_data_loss()
    {
        var sut = new PageAgentMessageReassembler();
        var payload = JsonSerializer.Serialize(new { t = "net", k = "fetch", body = new string('汉', 40_000) });
        var chunks = Chunk(payload, "message-1");

        for (var i = 0; i < chunks.Count - 1; i++)
            Assert.False(sut.TryAccept(chunks[i], out _));

        Assert.True(sut.TryAccept(chunks[^1], out var result));
        Assert.Equal(payload, result);
    }

    [Fact]
    public void Duplicate_or_out_of_order_chunk_invalidates_the_message()
    {
        var payload = JsonSerializer.Serialize(new { t = "action", value = new string('x', 20_000) });
        var chunks = Chunk(payload, "duplicate");
        var duplicate = new PageAgentMessageReassembler();

        Assert.False(duplicate.TryAccept(chunks[0], out _));
        Assert.False(duplicate.TryAccept(chunks[0], out _));
        Assert.False(duplicate.TryAccept(chunks[1], out _));

        var outOfOrder = new PageAgentMessageReassembler();
        Assert.False(outOfOrder.TryAccept(chunks[1], out _));
        Assert.False(outOfOrder.TryAccept(chunks[0], out _));
        Assert.True(outOfOrder.TryAccept(chunks[1], out var recovered));
        Assert.Equal(payload, recovered);
    }

    [Fact]
    public void Expired_partial_message_cannot_be_completed()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var sut = new PageAgentMessageReassembler(() => now);
        var payload = JsonSerializer.Serialize(new { t = "net", body = new string('x', 20_000) });
        var chunks = Chunk(payload, "expires");

        Assert.False(sut.TryAccept(chunks[0], out _));
        now += PageAgentMessageReassembler.MessageTimeout + TimeSpan.FromMilliseconds(1);
        Assert.False(sut.TryAccept(chunks[1], out _));
    }

    [Fact]
    public void Concurrent_and_size_limits_are_enforced()
    {
        var sut = new PageAgentMessageReassembler();
        var firstData = new string('x', PageAgentMessageReassembler.ChunkDataChars);

        for (var i = 0; i < PageAgentMessageReassembler.MaxConcurrentMessages; i++)
            Assert.False(sut.TryAccept(Envelope($"id-{i}", 0, 2, firstData), out _));

        Assert.False(sut.TryAccept(Envelope("overflow", 0, 2, firstData), out _));
        Assert.False(sut.TryAccept(Envelope("overflow", 1, 2, "x"), out _));

        Assert.True(sut.TryAccept(Envelope("id-0", 1, 2, "x"), out var completed));
        Assert.Equal(PageAgentMessageReassembler.ChunkDataChars + 1, completed!.Length);

        Assert.False(sut.TryAccept(Envelope("replacement", 0, 2, firstData), out _));
        Assert.True(sut.TryAccept(Envelope("replacement", 1, 2, "x"), out _));

        var oversizedDirect = JsonSerializer.Serialize(new
        {
            t = "net",
            value = new string('x', PageAgentMessageReassembler.MaxDirectMessageChars)
        });
        Assert.False(sut.TryAccept(oversizedDirect, out _));
        Assert.False(sut.TryAccept(Envelope("too-many", 0, PageAgentMessageReassembler.MaxChunks + 1, firstData), out _));
    }

    [Fact]
    public async Task Injector_pairs_named_world_with_context_scoped_binding()
    {
        var events = new EventBus();
        var session = new FakeSession("page-isolated");
        var sut = new PageAgentInjector(events, new NullLogger());

        Assert.True(await sut.InstallAsync(session));

        var bindings = session.Calls.Where(call => call.Method == "Runtime.addBinding").ToArray();
        var scripts = session.Calls.Where(call => call.Method == "Page.addScriptToEvaluateOnNewDocument").ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.Equal(2, scripts.Length);

        using var mainBinding = JsonDocument.Parse(bindings[0].Parameters!);
        Assert.False(mainBinding.RootElement.TryGetProperty("executionContextName", out _));

        using var isolatedBinding = JsonDocument.Parse(bindings[1].Parameters!);
        using var isolatedScript = JsonDocument.Parse(scripts.Single(call =>
            CdpJson.TryGetString(call.Parameters!, "worldName") is not null).Parameters!);
        var worldName = isolatedBinding.RootElement.GetProperty("executionContextName").GetString();
        Assert.StartsWith("Hackermes.Isolated.", worldName, StringComparison.Ordinal);
        Assert.Equal(worldName, isolatedScript.RootElement.GetProperty("worldName").GetString());
        Assert.False(isolatedScript.RootElement.GetProperty("includeCommandLineAPI").GetBoolean());
        Assert.True(isolatedScript.RootElement.GetProperty("runImmediately").GetBoolean());

        var mainSource = CdpJson.TryGetString(scripts.Single(call =>
            CdpJson.TryGetString(call.Parameters!, "worldName") is null).Parameters!, "source")!;
        var isoSource = isolatedScript.RootElement.GetProperty("source").GetString()!;
        Assert.DoesNotContain("installRecordingHook", mainSource, StringComparison.Ordinal);
        Assert.Contains("installRecordingHook", isoSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Injector_publishes_only_after_complete_chunk_sequence()
    {
        var events = new EventBus();
        var observed = new List<PageAgentMessageEvent>();
        events.Subscribe<PageAgentMessageEvent>(observed.Add);
        var session = new FakeSession("page-chunks");
        var sut = new PageAgentInjector(events, new NullLogger());
        Assert.True(await sut.InstallAsync(session));

        var mainBindingCall = session.Calls.First(call => call.Method == "Runtime.addBinding");
        var binding = CdpJson.TryGetString(mainBindingCall.Parameters!, "name")!;
        var payload = JsonSerializer.Serialize(new { t = "net", k = "fetch", body = new string('x', 20_000) });
        var chunks = Chunk(payload, "host-roundtrip");

        session.EmitBinding(binding, chunks[0]);
        Assert.Empty(observed);
        session.EmitBinding(binding, chunks[1]);

        var message = Assert.Single(observed);
        Assert.Equal("page-chunks", message.PageId);
        Assert.Equal("net", message.Kind);
        Assert.Equal("fetch", message.SubKind);
        Assert.Equal(payload, message.PayloadJson);
    }

    [Fact]
    public async Task Isolated_world_failure_degrades_without_disabling_main_world()
    {
        var session = new FakeSession("page-fallback") { RejectNamedWorld = true };
        using var sut = new PageAgentInjector(new EventBus(), new NullLogger());

        Assert.True(await sut.InstallAsync(session));
        Assert.Contains(session.Calls, call => call.Method == "Page.addScriptToEvaluateOnNewDocument"
            && CdpJson.TryGetString(call.Parameters!, "worldName") is null);
        var capability = sut.GetCapability(session.PageId);
        Assert.Equal(PageAgentWorldState.Ready, capability.MainWorld);
        Assert.Equal(PageAgentWorldState.Degraded, capability.IsolatedWorld);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EvaluateInIsolatedWorldAsync(session.PageId, "document.title"));
        Assert.DoesNotContain(session.Calls, call => call.Method == "Runtime.evaluate");
    }

    [Fact]
    public async Task Runtime_evaluates_only_in_the_exact_named_isolated_context()
    {
        var session = new FakeSession("page-runtime");
        using var sut = new PageAgentInjector(new EventBus(), new NullLogger());
        Assert.True(await sut.InstallAsync(session));

        var before = sut.GetCapability(session.PageId);
        Assert.Equal(PageAgentWorldState.Ready, before.MainWorld);
        Assert.Equal(PageAgentWorldState.Unavailable, before.IsolatedWorld);

        await sut.EvaluateInIsolatedWorldAsync(session.PageId, "(()=>document.title)()");

        var create = Assert.Single(session.Calls.Where(call => call.Method == "Page.createIsolatedWorld"));
        Assert.Equal(session.FrameId, CdpJson.TryGetString(create.Parameters!, "frameId"));
        var worldName = CdpJson.TryGetString(create.Parameters!, "worldName");
        Assert.StartsWith("Hackermes.Isolated.", worldName, StringComparison.Ordinal);
        using var evaluation = JsonDocument.Parse(Assert.Single(session.Calls.Where(call => call.Method == "Runtime.evaluate")).Parameters!);
        Assert.Equal(session.ContextId, evaluation.RootElement.GetProperty("contextId").GetInt32());
        Assert.False(evaluation.RootElement.GetProperty("includeCommandLineAPI").GetBoolean());
        var expression = evaluation.RootElement.GetProperty("expression").GetString()!;
        Assert.Contains("__hackermesEmit", expression, StringComparison.Ordinal);
        Assert.Contains("document.title", expression, StringComparison.Ordinal);
        Assert.Equal(PageAgentWorldState.Ready, sut.GetCapability(session.PageId).IsolatedWorld);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EvaluateInIsolatedWorldAsync("page-unknown", "document.title"));
        Assert.Single(session.Calls.Where(call => call.Method == "Runtime.evaluate"));
    }

    [Fact]
    public async Task Navigation_invalidates_context_and_session_close_cleans_runtime_subscriptions()
    {
        var session = new FakeSession("page-lifecycle");
        var registry = new FakeRegistry(session);
        using var sut = new PageAgentInjector(new EventBus(), new NullLogger(), registry);
        Assert.True(await sut.InstallAsync(session));
        await sut.EvaluateInIsolatedWorldAsync(session.PageId, "true");
        Assert.Equal(PageAgentWorldState.Ready, sut.GetCapability(session.PageId).IsolatedWorld);

        session.Emit("Page.frameNavigated", JsonSerializer.Serialize(new
        {
            frame = new { id = "frame-next", url = "https://example.test/next" }
        }));

        Assert.Equal(PageAgentWorldState.Unavailable, sut.GetCapability(session.PageId).IsolatedWorld);
        registry.Close(session.PageId);
        var closed = sut.GetCapability(session.PageId);
        Assert.Equal(PageAgentWorldState.Unavailable, closed.MainWorld);
        Assert.Equal(PageAgentWorldState.Unavailable, closed.IsolatedWorld);
        Assert.Equal(0, session.ActiveSubscriptionCount);
    }

    private static List<string> Chunk(string payload, string id)
    {
        var total = (int)Math.Ceiling((double)payload.Length / PageAgentMessageReassembler.ChunkDataChars);
        return Enumerable.Range(0, total)
            .Select(index => Envelope(
                id,
                index,
                total,
                payload.Substring(
                    index * PageAgentMessageReassembler.ChunkDataChars,
                    Math.Min(PageAgentMessageReassembler.ChunkDataChars,
                        payload.Length - index * PageAgentMessageReassembler.ChunkDataChars))))
            .ToList();
    }

    private static string Envelope(string id, int index, int total, string data) =>
        JsonSerializer.Serialize(new { __hmChunk = 1, id, index, total, data });

    private sealed class FakeSession(string pageId) : ICdpSession
    {
        private readonly Dictionary<string, List<Action<CdpEventArgs>>> _handlers = new(StringComparer.Ordinal);

        public string PageId { get; } = pageId;
        public bool IsAlive => true;
        public bool RejectNamedWorld { get; init; }
        public string FrameId { get; } = "frame-main";
        public int ContextId { get; } = 73;
        public List<(string Method, string? Parameters)> Calls { get; } = [];
        public int ActiveSubscriptionCount => _handlers.Values.Sum(handlers => handlers.Count);

        public Task<string> SendAsync(string method, string? parametersJson = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, parametersJson));
            if (RejectNamedWorld && method == "Page.addScriptToEvaluateOnNewDocument"
                && CdpJson.TryGetString(parametersJson!, "worldName") is not null)
            {
                throw new CdpException("worldName unsupported") { Method = method };
            }

            return method switch
            {
                "Page.getFrameTree" => Task.FromResult(JsonSerializer.Serialize(new
                {
                    frameTree = new { frame = new { id = FrameId, url = "about:blank" } }
                })),
                "Page.createIsolatedWorld" => Task.FromResult(JsonSerializer.Serialize(new
                {
                    executionContextId = ContextId
                })),
                "Runtime.evaluate" => Task.FromResult("{\"result\":{\"value\":true}}"),
                _ => Task.FromResult("{}")
            };
        }

        public Task<IDisposable> SubscribeAsync(string eventName, Action<CdpEventArgs> handler, CancellationToken cancellationToken = default)
        {
            if (!_handlers.TryGetValue(eventName, out var handlers))
            {
                handlers = [];
                _handlers.Add(eventName, handlers);
            }
            handlers.Add(handler);
            return Task.FromResult<IDisposable>(new CallbackDisposable(() => handlers.Remove(handler)));
        }

        public Task EnableDomainAsync(string domain, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void EmitBinding(string name, string payload)
        {
            Emit("Runtime.bindingCalled", CdpJson.Params(("name", name), ("payload", payload)));
        }

        public void Emit(string eventName, string parametersJson)
        {
            if (!_handlers.TryGetValue(eventName, out var handlers)) return;
            foreach (var handler in handlers.ToArray())
                handler(new CdpEventArgs(eventName, parametersJson));
        }
    }

    private sealed class FakeRegistry(params ICdpSession[] sessions) : ICdpSessionRegistry
    {
        public IReadOnlyList<ICdpSession> All { get; } = sessions;
        public event Action<ICdpSession>? SessionOpened;
        public event Action<string>? SessionClosed;
        public ICdpSession? Get(string pageId) => All.FirstOrDefault(session => session.PageId == pageId);
        public IDisposable Register(ICdpSession session) => throw new NotSupportedException();
        public void Close(string pageId) => SessionClosed?.Invoke(pageId);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;
        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
