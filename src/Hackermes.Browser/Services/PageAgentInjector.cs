using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.PageAgent;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Browser.Services;

/// <summary>
/// Installs and owns the Page Agent runtime for each exact browser page. Main-world
/// observation and named-isolated-world DOM operations deliberately have independent
/// capabilities so an isolated-world failure cannot disable passive observation.
/// </summary>
public sealed class PageAgentInjector : IPageAgentRuntime, IDisposable
{
    private const int MaximumEvaluationCharacters = 512 * 1024;
    private readonly IEventBus _eventBus;
    private readonly IAppLogger _logger;
    private readonly ICdpSessionRegistry? _sessions;
    private readonly Dictionary<string, RuntimeEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _disposed;

    public PageAgentInjector(IEventBus eventBus, IAppLogger logger)
        : this(eventBus, logger, null)
    {
    }

    public PageAgentInjector(IEventBus eventBus, IAppLogger logger, ICdpSessionRegistry? sessions)
    {
        _eventBus = eventBus;
        _logger = logger.ForCategory(nameof(PageAgentInjector));
        _sessions = sessions;
        if (_sessions is not null)
            _sessions.SessionClosed += OnSessionClosed;
    }

    /// <summary>
    /// Installs both worlds before initial navigation. A return value of <see langword="true"/>
    /// means main-world observation is installed; inspect <see cref="GetCapability"/> for the
    /// independently degradable isolated world.
    /// </summary>
    public async Task<bool> InstallAsync(ICdpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var entry = new RuntimeEntry(
            session,
            "__hackermes_" + suffix + "__",
            "__hackermes_iso_" + suffix + "__",
            "Hackermes.Isolated." + suffix);
        ReplaceEntry(entry);

        try
        {
            entry.AddSubscription(await session.SubscribeAsync(
                "Runtime.executionContextCreated",
                args => OnExecutionContextCreated(entry, args)).ConfigureAwait(false));
            entry.AddSubscription(await session.SubscribeAsync(
                "Runtime.executionContextDestroyed",
                args => OnExecutionContextDestroyed(entry, args)).ConfigureAwait(false));
            entry.AddSubscription(await session.SubscribeAsync(
                "Runtime.executionContextsCleared",
                _ => entry.ClearExecutionContexts()).ConfigureAwait(false));
            entry.AddSubscription(await session.SubscribeAsync(
                "Page.frameNavigated",
                args => OnFrameNavigated(entry, args)).ConfigureAwait(false));

            await InstallMainWorldAsync(entry).ConfigureAwait(false);
            entry.MarkMainWorldReady();

            await TryInstallIsolatedWorldAsync(entry).ConfigureAwait(false);
            var capability = entry.GetCapability();
            _logger.Info(
                $"Page Agent installed for {session.PageId} " +
                $"(main={capability.MainWorld}, isolated={capability.IsolatedWorld})");
            return true;
        }
        catch (Exception ex)
        {
            RemoveEntry(session.PageId, entry);
            _logger.Error($"Page Agent installation failed: {session.PageId}", ex);
            return false;
        }
    }

    public PageAgentRuntimeCapability GetCapability(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            return new PageAgentRuntimeCapability(
                pageId ?? string.Empty,
                PageAgentWorldState.Unavailable,
                PageAgentWorldState.Unavailable,
                "A pageId is required.");

        var entry = GetEntry(pageId);
        return entry is null
            ? new PageAgentRuntimeCapability(
                pageId,
                PageAgentWorldState.Unavailable,
                PageAgentWorldState.Unavailable,
                "The browser page is not registered with the Page Agent runtime.")
            : entry.GetCapability();
    }

    public async Task<string> EvaluateInIsolatedWorldAsync(
        string pageId,
        string expression,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            throw new ArgumentException("A pageId is required.", nameof(pageId));
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("An isolated-world expression is required.", nameof(expression));
        if (expression.Length > MaximumEvaluationCharacters)
            throw new ArgumentException(
                $"Isolated-world expressions are limited to {MaximumEvaluationCharacters} characters.",
                nameof(expression));

        var entry = GetEntry(pageId)
            ?? throw new InvalidOperationException("The browser page is no longer available.");
        if (!entry.Session.IsAlive)
            throw new InvalidOperationException("The browser page is no longer available.");
        if (!entry.IsMainWorldReady)
            throw new InvalidOperationException("The Page Agent main world is not ready.");
        if (!entry.IsIsolatedWorldInstalled)
            throw new InvalidOperationException(entry.Detail ?? "The Page Agent isolated world is unavailable.");

        // Never retry against a new context after this point. A navigation between context
        // selection and Runtime.evaluate must fail closed instead of running on a new document.
        var contextId = await EnsureIsolatedContextAsync(entry, cancellationToken).ConfigureAwait(false);
        var bindingName = JsonSerializer.Serialize(entry.IsolatedBindingName);
        var wrappedExpression =
            "(()=>{const __hackermesEmit=(message)=>{" +
            $"const binding=globalThis[{bindingName}];" +
            "if(typeof binding!=='function')throw new Error('Hackermes isolated binding is unavailable.');" +
            "binding(JSON.stringify(message));};return (" + expression + ");})()";
        var parameters = JsonSerializer.Serialize(new
        {
            expression = wrappedExpression,
            contextId,
            returnByValue = true,
            awaitPromise = true,
            includeCommandLineAPI = false,
            silent = false
        });

        try
        {
            var response = await entry.Session.SendAsync(
                "Runtime.evaluate",
                parameters,
                cancellationToken).ConfigureAwait(false);
            if (CdpJson.TryGetElement(response, "exceptionDetails") is not null)
                throw new InvalidOperationException("The isolated Page Agent expression was rejected.");
            return response;
        }
        catch
        {
            entry.ClearExecutionContext(contextId);
            throw;
        }
    }

    private async Task InstallMainWorldAsync(RuntimeEntry entry)
    {
        await entry.Session.SendAsync(
            "Runtime.addBinding",
            CdpJson.Params(("name", entry.MainBindingName))).ConfigureAwait(false);
        entry.AddSubscription(await entry.Session.SubscribeAsync(
            "Runtime.bindingCalled",
            args => OnBindingCalled(
                entry.Session.PageId,
                entry.MainBindingName,
                entry.MainReassembler,
                args)).ConfigureAwait(false));
        var script = PageAgentScript.PrepareMainWorld(entry.MainBindingName);
        await entry.Session.SendAsync(
            "Page.addScriptToEvaluateOnNewDocument",
            CdpJson.Params(("source", script))).ConfigureAwait(false);
    }

    private async Task TryInstallIsolatedWorldAsync(RuntimeEntry entry)
    {
        try
        {
            await TryResolveMainFrameAsync(entry, CancellationToken.None).ConfigureAwait(false);
            await entry.Session.SendAsync(
                "Runtime.addBinding",
                CdpJson.Params(
                    ("name", entry.IsolatedBindingName),
                    ("executionContextName", entry.IsolatedWorldName))).ConfigureAwait(false);
            entry.AddSubscription(await entry.Session.SubscribeAsync(
                "Runtime.bindingCalled",
                args => OnBindingCalled(
                    entry.Session.PageId,
                    entry.IsolatedBindingName,
                    entry.IsolatedReassembler,
                    args)).ConfigureAwait(false));

            var script = PageAgentScript.PrepareIsolatedWorld(entry.IsolatedBindingName);
            await entry.Session.SendAsync(
                "Page.addScriptToEvaluateOnNewDocument",
                CdpJson.Params(
                    ("source", script),
                    ("worldName", entry.IsolatedWorldName),
                    ("includeCommandLineAPI", false),
                    ("runImmediately", true))).ConfigureAwait(false);
            entry.MarkIsolatedWorldInstalled();
        }
        catch (Exception ex)
        {
            // Fail closed for DOM operations while preserving main-world observation.
            entry.MarkIsolatedWorldDegraded(ex.Message);
            _logger.Warn($"Isolated Page Agent installation failed: {entry.Session.PageId}; {ex.Message}");
        }
    }

    private async Task<int> EnsureIsolatedContextAsync(RuntimeEntry entry, CancellationToken cancellationToken)
    {
        if (entry.ExecutionContextId is { } ready)
            return ready;

        await entry.ContextCreationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.ExecutionContextId is { } existing)
                return existing;
            if (!entry.IsIsolatedWorldInstalled)
                throw new InvalidOperationException(entry.Detail ?? "The Page Agent isolated world is unavailable.");

            await TryResolveMainFrameAsync(entry, cancellationToken).ConfigureAwait(false);
            var frameId = entry.MainFrameId
                ?? throw new InvalidOperationException("The page main frame is not ready for isolated inspection.");
            try
            {
                var response = await entry.Session.SendAsync(
                    "Page.createIsolatedWorld",
                    CdpJson.Params(
                        ("frameId", frameId),
                        ("worldName", entry.IsolatedWorldName),
                        ("grantUniveralAccess", false)),
                    cancellationToken).ConfigureAwait(false);
                var contextId = CdpJson.TryGetInt(response, "executionContextId")
                    ?? throw new InvalidOperationException("Chromium did not return an isolated execution context.");
                entry.SetExecutionContext(frameId, contextId);
                return contextId;
            }
            catch (Exception ex)
            {
                entry.MarkContextDegraded(ex.Message);
                throw new InvalidOperationException(
                    $"The Page Agent isolated world is unavailable: {ex.Message}", ex);
            }
        }
        finally
        {
            entry.ContextCreationGate.Release();
        }
    }

    private static async Task TryResolveMainFrameAsync(RuntimeEntry entry, CancellationToken cancellationToken)
    {
        if (entry.MainFrameId is not null)
            return;
        var response = await entry.Session.SendAsync(
            "Page.getFrameTree",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var frameId = CdpJson.TryGetString(response, "frameTree", "frame", "id");
        if (!string.IsNullOrWhiteSpace(frameId))
            entry.SetMainFrame(frameId);
    }

    private void OnBindingCalled(
        string pageId,
        string expectedBinding,
        PageAgentMessageReassembler reassembler,
        CdpEventArgs args)
    {
        if (!string.Equals(
                CdpJson.TryGetString(args.ParametersJson, "name"),
                expectedBinding,
                StringComparison.Ordinal))
            return;
        var payload = CdpJson.TryGetString(args.ParametersJson, "payload");
        if (string.IsNullOrEmpty(payload) || !reassembler.TryAccept(payload, out var completePayload))
            return;
        var kind = CdpJson.TryGetString(completePayload!, "t") ?? "unknown";
        var subKind = CdpJson.TryGetString(completePayload!, "k");
        _eventBus.Publish(new PageAgentMessageEvent(pageId, kind, subKind, completePayload!));
    }

    private static void OnExecutionContextCreated(RuntimeEntry entry, CdpEventArgs args)
    {
        var name = CdpJson.TryGetString(args.ParametersJson, "context", "name");
        if (!string.Equals(name, entry.IsolatedWorldName, StringComparison.Ordinal))
            return;
        var contextId = CdpJson.TryGetInt(args.ParametersJson, "context", "id");
        var frameId = CdpJson.TryGetString(args.ParametersJson, "context", "auxData", "frameId");
        if (contextId is null || string.IsNullOrWhiteSpace(frameId))
            return;
        if (entry.MainFrameId is not null && !string.Equals(entry.MainFrameId, frameId, StringComparison.Ordinal))
            return;
        entry.SetExecutionContext(frameId, contextId.Value);
    }

    private static void OnExecutionContextDestroyed(RuntimeEntry entry, CdpEventArgs args)
    {
        if (CdpJson.TryGetInt(args.ParametersJson, "executionContextId") is { } contextId)
            entry.ClearExecutionContext(contextId);
    }

    private static void OnFrameNavigated(RuntimeEntry entry, CdpEventArgs args)
    {
        var frame = CdpJson.TryGetElement(args.ParametersJson, "frame");
        if (frame is not { ValueKind: JsonValueKind.Object } value)
            return;
        if (value.TryGetProperty("parentId", out var parent) &&
            parent.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(parent.GetString()))
            return;
        if (value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            entry.Navigate(id.GetString());
    }

    private RuntimeEntry? GetEntry(string pageId)
    {
        lock (_gate)
            return _entries.TryGetValue(pageId, out var entry) ? entry : null;
    }

    private void ReplaceEntry(RuntimeEntry entry)
    {
        RuntimeEntry? replaced = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Session.PageId, out replaced))
                _entries.Remove(entry.Session.PageId);
            _entries.Add(entry.Session.PageId, entry);
        }
        replaced?.Dispose();
    }

    private void RemoveEntry(string pageId, RuntimeEntry? expected = null)
    {
        RuntimeEntry? removed = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(pageId, out var current) &&
                (expected is null || ReferenceEquals(current, expected)))
            {
                _entries.Remove(pageId);
                removed = current;
            }
        }
        removed?.Dispose();
    }

    private void OnSessionClosed(string pageId) => RemoveEntry(pageId);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_sessions is not null)
            _sessions.SessionClosed -= OnSessionClosed;
        RuntimeEntry[] entries;
        lock (_gate)
        {
            entries = [.. _entries.Values];
            _entries.Clear();
        }
        foreach (var entry in entries)
            entry.Dispose();
    }

    private sealed class RuntimeEntry(
        ICdpSession session,
        string mainBindingName,
        string isolatedBindingName,
        string isolatedWorldName) : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IDisposable> _subscriptions = [];
        private bool _mainWorldReady;
        private bool _isolatedWorldInstalled;
        private PageAgentWorldState _isolatedState = PageAgentWorldState.Unavailable;
        private string? _mainFrameId;
        private int? _executionContextId;
        private string? _detail = "The named isolated world is waiting for a page context.";
        private int _disposed;

        public ICdpSession Session { get; } = session;
        public string MainBindingName { get; } = mainBindingName;
        public string IsolatedBindingName { get; } = isolatedBindingName;
        public string IsolatedWorldName { get; } = isolatedWorldName;
        public PageAgentMessageReassembler MainReassembler { get; } = new();
        public PageAgentMessageReassembler IsolatedReassembler { get; } = new();
        public SemaphoreSlim ContextCreationGate { get; } = new(1, 1);

        public bool IsMainWorldReady { get { lock (_gate) return _mainWorldReady; } }
        public bool IsIsolatedWorldInstalled { get { lock (_gate) return _isolatedWorldInstalled; } }
        public string? MainFrameId { get { lock (_gate) return _mainFrameId; } }
        public int? ExecutionContextId { get { lock (_gate) return _executionContextId; } }
        public string? Detail { get { lock (_gate) return _detail; } }

        public void AddSubscription(IDisposable subscription)
        {
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    subscription.Dispose();
                    return;
                }
                _subscriptions.Add(subscription);
            }
        }

        public void MarkMainWorldReady()
        {
            lock (_gate) _mainWorldReady = true;
        }

        public void MarkIsolatedWorldInstalled()
        {
            lock (_gate)
            {
                _isolatedWorldInstalled = true;
                _isolatedState = _executionContextId is null
                    ? PageAgentWorldState.Unavailable
                    : PageAgentWorldState.Ready;
                _detail = _executionContextId is null
                    ? "The named isolated world is waiting for a page context."
                    : null;
            }
        }

        public void MarkIsolatedWorldDegraded(string detail)
        {
            lock (_gate)
            {
                _isolatedWorldInstalled = false;
                _isolatedState = PageAgentWorldState.Degraded;
                _executionContextId = null;
                _detail = string.IsNullOrWhiteSpace(detail)
                    ? "The named isolated world could not be installed."
                    : detail;
            }
        }

        public void MarkContextDegraded(string detail)
        {
            lock (_gate)
            {
                _executionContextId = null;
                _isolatedState = PageAgentWorldState.Degraded;
                _detail = detail;
            }
        }

        public void SetMainFrame(string? frameId)
        {
            if (string.IsNullOrWhiteSpace(frameId))
                return;
            lock (_gate) _mainFrameId = frameId;
        }

        public void Navigate(string? frameId)
        {
            lock (_gate)
            {
                _mainFrameId = frameId;
                _executionContextId = null;
                if (_isolatedWorldInstalled)
                {
                    _isolatedState = PageAgentWorldState.Unavailable;
                    _detail = "The named isolated world is waiting for the navigated page context.";
                }
            }
        }

        public void SetExecutionContext(string frameId, int contextId)
        {
            lock (_gate)
            {
                if (_mainFrameId is not null && !string.Equals(_mainFrameId, frameId, StringComparison.Ordinal))
                    return;
                _mainFrameId ??= frameId;
                _executionContextId = contextId;
                if (_isolatedWorldInstalled)
                {
                    _isolatedState = PageAgentWorldState.Ready;
                    _detail = null;
                }
            }
        }

        public void ClearExecutionContext(int contextId)
        {
            lock (_gate)
            {
                if (_executionContextId != contextId)
                    return;
                _executionContextId = null;
                if (_isolatedWorldInstalled)
                {
                    _isolatedState = PageAgentWorldState.Unavailable;
                    _detail = "The isolated execution context was destroyed.";
                }
            }
        }

        public void ClearExecutionContexts()
        {
            lock (_gate)
            {
                _executionContextId = null;
                if (_isolatedWorldInstalled)
                {
                    _isolatedState = PageAgentWorldState.Unavailable;
                    _detail = "The page execution contexts were cleared.";
                }
            }
        }

        public PageAgentRuntimeCapability GetCapability()
        {
            lock (_gate)
            {
                return new PageAgentRuntimeCapability(
                    Session.PageId,
                    _mainWorldReady ? PageAgentWorldState.Ready : PageAgentWorldState.Unavailable,
                    _isolatedState,
                    _detail);
            }
        }

        public void Dispose()
        {
            IDisposable[] subscriptions;
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                subscriptions = [.. _subscriptions];
                _subscriptions.Clear();
                _executionContextId = null;
            }
            foreach (var subscription in subscriptions)
                subscription.Dispose();
            // A close can race an in-flight evaluation. Leave the small semaphore for
            // collection so that the evaluation's finally block can still release it.
        }
    }
}
