using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hackermes.Browser.Services;

public sealed record BrowserHistoryEntry(string Url, string Title, DateTimeOffset VisitedAt)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Title) || string.Equals(Title, Url, StringComparison.Ordinal)
        ? Url
        : $"{Title}  ·  {Url}";
}

public interface IBrowserHistoryStore
{
    IReadOnlyList<BrowserHistoryEntry> Entries { get; }
    event Action? Changed;
    void Record(string url, string? title);
    void Clear();
}

/// <summary>Small persistent navigation history shared by all embedded browser tabs.</summary>
public sealed class BrowserHistoryStore : IBrowserHistoryStore
{
    private const int MaximumEntries = 200;
    private readonly string _path;
    private readonly object _gate = new();
    private List<BrowserHistoryEntry> _entries;

    public BrowserHistoryStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hackermes", "browser-history.json"))
    {
    }

    public BrowserHistoryStore(string path)
    {
        _path = Path.GetFullPath(path);
        _entries = Load();
    }

    public IReadOnlyList<BrowserHistoryEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public event Action? Changed;

    public void Record(string url, string? title)
    {
        var normalized = url?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) return;
        var label = string.IsNullOrWhiteSpace(title) || string.Equals(title, "新标签页", StringComparison.Ordinal)
            ? normalized
            : title.Trim();
        lock (_gate)
        {
            _entries.RemoveAll(entry => string.Equals(entry.Url, normalized, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new BrowserHistoryEntry(normalized, label, DateTimeOffset.UtcNow));
            if (_entries.Count > MaximumEntries) _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            SaveUnsafe();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            SaveUnsafe();
        }
        Changed?.Invoke();
    }

    private List<BrowserHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return (JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(File.ReadAllText(_path)) ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Url))
                .Take(MaximumEntries)
                .ToList();
        }
        catch (Exception) when (File.Exists(_path))
        {
            return [];
        }
    }

    private void SaveUnsafe()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
        File.Move(temporary, _path, true);
    }
}
