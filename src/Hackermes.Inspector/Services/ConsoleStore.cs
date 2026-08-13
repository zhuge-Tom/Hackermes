using Hackermes.Base.Diagnostics;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hackermes.Inspector.Services;

public sealed record ConsoleEntry(DateTime At, string Level, string Text, string? Source, string PageId = "");

/// <summary>
/// 控制台记录。三个来源合并:
/// <list type="bullet">
/// <item><c>Runtime.consoleAPICalled</c> —— 页面调用 console.* </item>
/// <item><c>Runtime.exceptionThrown</c> —— 未捕获异常</item>
/// <item><c>Log.entryAdded</c> —— 浏览器级日志(CSP 违规、资源加载失败等,页面代码看不到这些)</item>
/// </list>
/// </summary>
public sealed class ConsoleStore : IConsoleQueryService
{
    private const int MaxEntries = 500;

    private readonly IAppLogger _logger;
    private readonly Dictionary<string, List<IDisposable>> _subscriptionsByPage = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ConsoleStore(ICdpSessionRegistry registry, IAppLogger logger)
    {
        _logger = logger.ForCategory(nameof(ConsoleStore));

        foreach (var session in registry.All)
            _ = AttachAsync(session);

        registry.SessionOpened += session => _ = AttachAsync(session);
        registry.SessionClosed += OnSessionClosed;
    }

    public ObservableCollection<ConsoleEntry> Entries { get; } = [];

    public event Action? Changed;

    public IReadOnlyList<ConsoleObservation> Read(int last = 100, string? level = null, string? pageId = null) => Entries
        .Where(entry => string.IsNullOrWhiteSpace(pageId) || string.Equals(entry.PageId, pageId, StringComparison.Ordinal))
        .Where(entry => string.IsNullOrWhiteSpace(level) || string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
        .Take(Math.Clamp(last, 1, MaxEntries))
        .Select(entry => new ConsoleObservation(entry.At.ToString("O"), entry.Level, entry.Text, entry.Source))
        .ToArray();

    public void Clear() => UiThreadBridge.Post(() =>
    {
        Entries.Clear();
        Changed?.Invoke();
    });

    private async System.Threading.Tasks.Task AttachAsync(ICdpSession session)
    {
        try
        {
            await session.EnableDomainAsync("Runtime").ConfigureAwait(false);
            await session.EnableDomainAsync("Log").ConfigureAwait(false);

            var subscriptions = new List<IDisposable>
            {
                await session.SubscribeAsync("Runtime.consoleAPICalled", e => OnConsoleApi(session.PageId, e)).ConfigureAwait(false),
                await session.SubscribeAsync("Runtime.exceptionThrown", e => OnException(session.PageId, e)).ConfigureAwait(false),
                await session.SubscribeAsync("Log.entryAdded", e => OnLogEntry(session.PageId, e)).ConfigureAwait(false)
            };
            lock (_gate)
            {
                if (!session.IsAlive)
                {
                    foreach (var subscription in subscriptions) subscription.Dispose();
                    return;
                }
                if (_subscriptionsByPage.Remove(session.PageId, out var previous))
                    foreach (var subscription in previous) subscription.Dispose();
                _subscriptionsByPage[session.PageId] = subscriptions;
            }

            _logger.Info($"已接入页面控制台: {session.PageId}");
        }
        catch (Exception ex)
        {
            _logger.Error($"接入页面控制台失败: {session.PageId}", ex);
        }
    }

    private void OnConsoleApi(string pageId, CdpEventArgs e)
    {
        var level = CdpJson.TryGetString(e.ParametersJson, "type") ?? "log";
        var text = FormatArguments(e.ParametersJson);
        Add(new ConsoleEntry(DateTime.Now, NormalizeLevel(level), text, "console", pageId));
    }

    private void OnException(string pageId, CdpEventArgs e)
    {
        var text = CdpJson.TryGetString(e.ParametersJson, "exceptionDetails", "exception", "description")
                   ?? CdpJson.TryGetString(e.ParametersJson, "exceptionDetails", "text")
                   ?? "未捕获异常";

        Add(new ConsoleEntry(DateTime.Now, "error", text, "exception", pageId));
    }

    private void OnLogEntry(string pageId, CdpEventArgs e)
    {
        var level = CdpJson.TryGetString(e.ParametersJson, "entry", "level") ?? "info";
        var text = CdpJson.TryGetString(e.ParametersJson, "entry", "text") ?? string.Empty;
        var source = CdpJson.TryGetString(e.ParametersJson, "entry", "source");

        Add(new ConsoleEntry(DateTime.Now, NormalizeLevel(level), text, source ?? "browser", pageId));
    }

    private static string NormalizeLevel(string level) => level switch
    {
        "error" or "assert" => "error",
        "warning" or "warn" => "warn",
        "debug" or "verbose" => "debug",
        _ => "info"
    };

    /// <summary>
    /// 把 consoleAPICalled 的 args 拼成一行文本。
    /// 对象只取 CDP 给的 description,不做深度求值 —— 那需要额外的
    /// Runtime.getProperties 往返,列表展示不值得。
    /// </summary>
    private static string FormatArguments(string json)
    {
        var args = CdpJson.TryGetElement(json, "args");

        if (args is not { ValueKind: JsonValueKind.Array } array)
            return string.Empty;

        var parts = new List<string>();

        foreach (var arg in array.EnumerateArray())
        {
            if (arg.TryGetProperty("value", out var value))
            {
                parts.Add(value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Null => "null",
                    _ => value.ToString()
                });
                continue;
            }

            if (arg.TryGetProperty("description", out var description))
            {
                parts.Add(description.GetString() ?? string.Empty);
                continue;
            }

            if (arg.TryGetProperty("type", out var type))
                parts.Add(type.GetString() ?? "?");
        }

        return string.Join(' ', parts);
    }

    private void Add(ConsoleEntry entry) => UiThreadBridge.Post(() =>
    {
        Entries.Insert(0, entry);

        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);

        Changed?.Invoke();
    });

    private void OnSessionClosed(string pageId)
    {
        lock (_gate)
        {
            if (_subscriptionsByPage.Remove(pageId, out var subscriptions))
                foreach (var subscription in subscriptions) subscription.Dispose();
        }
        UiThreadBridge.Post(() =>
        {
            foreach (var entry in Entries.Where(entry => entry.PageId == pageId).ToArray())
                Entries.Remove(entry);
            Changed?.Invoke();
        });
    }
}
