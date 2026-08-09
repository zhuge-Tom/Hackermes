using Hackermes.Traffic.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Hackermes.Traffic.Services;

public interface ITrafficHistoryPersistence : IDisposable
{
    string FilePath { get; }
    IReadOnlyList<TrafficMessage> Load();
    void ScheduleSave(IReadOnlyList<TrafficMessage> messages);
    void Flush();
}

/// <summary>Versioned, compressed traffic history with debounced atomic writes and backup fallback.</summary>
public sealed class TrafficHistoryPersistence : ITrafficHistoryPersistence
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Timer _timer;
    private TrafficMessage[]? _pending;
    private bool _disposed;
    private bool _primaryWasReadable;

    public TrafficHistoryPersistence() : this(ResolveDefaultPath()) { }

    public TrafficHistoryPersistence(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath { get; }

    public IReadOnlyList<TrafficMessage> Load()
    {
        var primary = TryRead(FilePath);
        if (primary is not null)
        {
            _primaryWasReadable = true;
            return primary;
        }

        _primaryWasReadable = false;
        return TryRead(FilePath + ".bak") ?? [];
    }

    public void ScheduleSave(IReadOnlyList<TrafficMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // TrafficMessage is treated as immutable after insertion.  A shallow
            // snapshot avoids cloning every body byte array on every request.
            _pending = messages.ToArray();
            _timer.Change(300, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        // Timer callbacks may overlap after ScheduleSave rearms the one-shot timer while a write is active.
        // Serialize the complete dequeue/write/requeue cycle so an older snapshot cannot overwrite a newer one.
        lock (_writeGate)
        {
            TrafficMessage[]? snapshot;
            lock (_gate) { snapshot = _pending; _pending = null; }
            if (snapshot is null) return;
            try { Write(snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                lock (_gate)
                {
                    if (_pending is null) _pending = snapshot;
                    if (!_disposed) _timer.Change(2000, Timeout.Infinite);
                }
            }
        }
    }

    private void Write(IReadOnlyList<TrafficMessage> messages)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, new HistoryDocument(SchemaVersion, messages));
            // The primary was validated during Load or by our previous successful
            // write. Re-deserializing a large compressed history before every save
            // previously doubled transient allocations and startup pressure.
            if (_primaryWasReadable && File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".bak", true);
            File.Move(temporary, FilePath, true);
            _primaryWasReadable = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static TrafficMessage[]? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var document = JsonSerializer.Deserialize<HistoryDocument>(gzip);
            if (document?.SchemaVersion != SchemaVersion) return null;
            if (document.Messages is null || document.Messages.Any(item =>
                    item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.PageId) ||
                    string.IsNullOrWhiteSpace(item.Method) || string.IsNullOrWhiteSpace(item.Url) ||
                    item.RequestHeaders is null || item.ResponseHeaders is null))
                return null;
            return document.Messages.Select(NormalizeAfterLoad).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static TrafficMessage NormalizeForPersistence(TrafficMessage item) => item with
    {
        RequestHeaders = item.RequestHeaders.ToArray(), RequestBody = item.RequestBody?.ToArray(),
        ResponseHeaders = item.ResponseHeaders.ToArray(), ResponseBody = item.ResponseBody?.ToArray()
    };

    private static TrafficMessage NormalizeAfterLoad(TrafficMessage item) => NormalizeForPersistence(item) with
    {
        State = item.State == TrafficState.Paused ? TrafficState.Continued : item.State
    };

    private static string ResolveDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "Hackermes", "traffic-history.v1.json.gz");
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _timer.Change(Timeout.Infinite, Timeout.Infinite); }
        Flush();
        _timer.Dispose();
    }

    private sealed record HistoryDocument(int SchemaVersion, IReadOnlyList<TrafficMessage>? Messages);
}
