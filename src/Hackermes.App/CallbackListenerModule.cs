using Hackermes.AiPanel.Tools;
using Hackermes.Base;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.App;

public sealed record JndiListenerState(string Token, string Host, int Port, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt);

public sealed record JndiListenerHit(DateTimeOffset At, string RemoteEndpoint, string FirstBytes);

public sealed record JndiListenerSnapshot(JndiListenerState State, bool Active, IReadOnlyList<JndiListenerHit> Hits);

/// <summary>
/// Local JNDI callback listener for deserialization detection (fastjson/JNDI payloads).
/// Binds 127.0.0.1 only, records any inbound connection as a hit (a connection alone is
/// proof that the target executed the injected callback address), and auto-expires.
/// Listeners never serve objects — detection only, no exploitation.
/// </summary>
public sealed class JndiListenerService
{
    private const int MaxListeners = 4;
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    private sealed class ListenerInstance
    {
        public required JndiListenerState State;
        public required TcpListener Listener;
        public required CancellationTokenSource Cancellation;
        public required List<JndiListenerHit> Hits;
        public bool Stopped;
    }

    private readonly Dictionary<string, ListenerInstance> _listeners = new(StringComparer.Ordinal);
    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    public JndiListenerService(IAppLogger logger) => _logger = logger.ForCategory(nameof(JndiListenerService));

    public JndiListenerState Start(int durationMinutes)
    {
        durationMinutes = Math.Clamp(durationMinutes, 1, 15);
        lock (_gate)
        {
            StopExpiredUnsafe();
            if (_listeners.Count >= MaxListeners)
                throw new InvalidOperationException($"At most {MaxListeners} concurrent listeners are allowed; stop one first.");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var state = new JndiListenerState(Guid.NewGuid().ToString("N"), "127.0.0.1", port,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(durationMinutes));
            var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(durationMinutes));
            var instance = new ListenerInstance
            {
                State = state, Listener = listener, Cancellation = cancellation,
                Hits = [], Stopped = false
            };
            _listeners[state.Token] = instance;
            var acceptLoop = Task.Run(() => AcceptLoopAsync(instance), cancellation.Token);
            _logger.Info($"JNDI listener {state.Token} started on 127.0.0.1:{port}, expires {state.ExpiresAt:O}");
            return state;
        }
    }

    private async Task AcceptLoopAsync(ListenerInstance instance)
    {
        try
        {
            while (!instance.Cancellation.IsCancellationRequested)
            {
                var client = await instance.Listener.AcceptTcpClientAsync(instance.Cancellation.Token).ConfigureAwait(false);
                var remote = "unknown";
                var firstBytes = string.Empty;
                try
                {
                    remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    var buffer = new byte[64];
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var read = await client.Client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                    if (read > 0)
                        firstBytes = Convert.ToHexString(buffer, 0, Math.Min(read, 32));
                }
                catch { /* a plain TCP connect without payload is still a hit */ }
                finally { try { client.Close(); } catch { } }
                lock (_gate)
                {
                    if (!instance.Stopped)
                        instance.Hits.Add(new JndiListenerHit(DateTimeOffset.UtcNow, remote, firstBytes));
                }
                _logger.Info($"JNDI listener hit from {remote} on port {instance.State.Port}");
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public JndiListenerSnapshot? Read(string token)
    {
        lock (_gate)
        {
            StopExpiredUnsafe();
            if (!_listeners.TryGetValue(token ?? string.Empty, out var instance))
                return null;
            return new JndiListenerSnapshot(instance.State, !instance.Stopped && instance.Cancellation.IsCancellationRequested == false,
                instance.Hits.ToArray());
        }
    }

    public bool Stop(string token)
    {
        lock (_gate)
        {
            if (!_listeners.TryGetValue(token ?? string.Empty, out var instance)) return false;
            StopInstance(instance);
            _listeners.Remove(token ?? string.Empty);
            return true;
        }
    }

    private void StopExpiredUnsafe()
    {
        foreach (var expired in _listeners.Values.Where(value => value.State.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
        {
            StopInstance(expired);
            _listeners.Remove(expired.State.Token);
        }
    }

    private void StopInstance(ListenerInstance instance)
    {
        instance.Stopped = true;
        try { instance.Cancellation.Cancel(); } catch { }
        try { instance.Listener.Stop(); } catch { }
    }
}

/// <summary>Registers the jndi_listener_* AI tools (callback detection infrastructure).</summary>
public sealed class CallbackListenerModule : IModule
{
    public string Name => "Callback Listener";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<JndiListenerService>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var registry = serviceProvider.GetRequiredService<IAiToolRegistry>();
        var service = serviceProvider.GetRequiredService<JndiListenerService>();

        registry.Register(new AiToolDefinition(
            "jndi_listener_start",
            "Start a local JNDI callback listener on 127.0.0.1 (auto port, auto-expires). Use the returned " +
            "host:port as the ldap/rmi callback address in detect.fastjson_jndi.scan for local targets, then poll " +
            "jndi_listener_hits: any connection proves the target executed the injected callback. Detection only — " +
            "the listener never serves objects.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { durationMinutes = new { type = "integer", description = "1-15, default 10" } },
                additionalProperties = false
            }),
            AiToolRisk.Mutating,
            (invocation, _) => ValueTask.FromResult(Try(() =>
                service.Start(Number(invocation.Arguments, "durationMinutes", 10))))));

        registry.Register(new AiToolDefinition(
            "jndi_listener_hits",
            "Read the callback hits recorded by one listener (timestamp, remote endpoint, first bytes).",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { token = new { type = "string" } },
                required = new[] { "token" },
                additionalProperties = false
            }),
            AiToolRisk.ReadOnly,
            (invocation, _) => ValueTask.FromResult(Try(() =>
            {
                var snapshot = service.Read(Text(invocation.Arguments, "token"))
                    ?? throw new InvalidOperationException("Listener not found or expired; start a new one.");
                return JsonSerializer.Serialize(new
                {
                    snapshot.State.Token, snapshot.State.Host, snapshot.State.Port,
                    snapshot.Active, snapshot.State.ExpiresAt, HitCount = snapshot.Hits.Count, Hits = snapshot.Hits
                });
            }))));

        registry.Register(new AiToolDefinition(
            "jndi_listener_stop",
            "Stop one local JNDI callback listener.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { token = new { type = "string" } },
                required = new[] { "token" },
                additionalProperties = false
            }),
            AiToolRisk.Mutating,
            (invocation, _) => ValueTask.FromResult(
                service.Stop(Text(invocation.Arguments, "token"))
                    ? ToolResult.Ok("Listener stopped.")
                    : ToolResult.Fail("Listener not found or already expired."))));
    }

    private static ToolResult Try(Func<object> value)
    {
        try { return ToolResult.Ok(JsonSerializer.Serialize(value())); }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;

    private static int Number(JsonElement arguments, string name, int fallback) =>
        arguments.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;
}
