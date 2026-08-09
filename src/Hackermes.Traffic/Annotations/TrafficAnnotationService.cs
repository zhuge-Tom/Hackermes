using Hackermes.Traffic.Persistence;
using Hackermes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hackermes.Traffic.Annotations;

public interface ITrafficAnnotationService
{
    event Action<TrafficAnnotationChanged>? Changed;
    string StorageFilePath { get; }
    TrafficAnnotation? Get(string packetId);
    IReadOnlyList<TrafficAnnotation> GetAll();
    IReadOnlyList<TrafficAnnotation> Query(TrafficAnnotationQuery query);
    TrafficAnnotation Update(string packetId, TrafficAnnotationUpdate update);
    IReadOnlyList<TrafficAnnotation> UpdateMany(IReadOnlyList<string> packetIds, TrafficAnnotationUpdate update);
    bool Delete(string packetId);
    int PruneMissingPackets();
    void Reload();
}

/// <summary>Persistent analyst context shared by UI, CLI and agents without changing captured packet bytes.</summary>
public sealed class TrafficAnnotationService : ITrafficAnnotationService
{
    private const int SchemaVersion = 1;
    private const int MaxTags = 32;
    private const int MaxTagLength = 50;
    private const int MaxNoteLength = 64 * 1024;
    private readonly object _gate = new();
    private readonly ITrafficStore _trafficStore;
    private readonly string _storageFilePath;
    private readonly Dictionary<string, TrafficAnnotation> _annotations = new(StringComparer.Ordinal);

    public TrafficAnnotationService(ITrafficStore trafficStore)
        : this(trafficStore, VersionedJsonFile.DefaultPath("traffic-annotations.json"))
    {
    }

    public TrafficAnnotationService(ITrafficStore trafficStore, string storageFilePath)
    {
        _trafficStore = trafficStore ?? throw new ArgumentNullException(nameof(trafficStore));
        if (string.IsNullOrWhiteSpace(storageFilePath))
            throw new ArgumentException("Storage file path is required.", nameof(storageFilePath));
        _storageFilePath = System.IO.Path.GetFullPath(storageFilePath);
        Reload();
    }

    public event Action<TrafficAnnotationChanged>? Changed;
    public string StorageFilePath => _storageFilePath;

    public TrafficAnnotation? Get(string packetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packetId);
        lock (_gate)
            return _annotations.TryGetValue(packetId, out var annotation) ? Clone(annotation) : null;
    }

    public IReadOnlyList<TrafficAnnotation> GetAll()
    {
        lock (_gate)
            return _annotations.Values.OrderByDescending(item => item.UpdatedAt).Select(Clone).ToArray();
    }

    public IReadOnlyList<TrafficAnnotation> Query(TrafficAnnotationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            IEnumerable<TrafficAnnotation> result = _annotations.Values;
            if (!string.IsNullOrWhiteSpace(query.Tag))
                result = result.Where(item => item.Tags.Contains(query.Tag.Trim(), StringComparer.OrdinalIgnoreCase));
            if (query.Status is { } status)
                result = result.Where(item => item.Status == status);
            if (query.Starred is { } starred)
                result = result.Where(item => item.Starred == starred);
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var text = query.Text.Trim();
                result = result.Where(item =>
                    item.PacketId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    (item.Note?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    item.Tags.Any(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase)));
            }
            return result.OrderByDescending(item => item.UpdatedAt).Select(Clone).ToArray();
        }
    }

    public TrafficAnnotation Update(string packetId, TrafficAnnotationUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packetId);
        ArgumentNullException.ThrowIfNull(update);
        if (_trafficStore.Get(packetId) is null)
            throw new KeyNotFoundException($"Traffic item '{packetId}' was not found.");

        TrafficAnnotation changed;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var current = _annotations.GetValueOrDefault(packetId) ??
                new TrafficAnnotation(packetId, false, [], null, TrafficReviewStatus.Unreviewed, now, now, 0);
            var note = update.ReplaceNote ? NormalizeNote(update.Note) : current.Note;
            changed = current with
            {
                Starred = update.Starred ?? current.Starred,
                Tags = update.Tags is null ? current.Tags : NormalizeTags(update.Tags),
                Note = note,
                Status = update.Status ?? current.Status,
                UpdatedAt = now,
                Revision = checked(current.Revision + 1)
            };
            CommitLocked(next => next[packetId] = changed);
        }
        var snapshot = Clone(changed);
        Changed?.Invoke(new TrafficAnnotationChanged("update", packetId, snapshot));
        return snapshot;
    }

    public IReadOnlyList<TrafficAnnotation> UpdateMany(IReadOnlyList<string> packetIds, TrafficAnnotationUpdate update)
    {
        ArgumentNullException.ThrowIfNull(packetIds);
        ArgumentNullException.ThrowIfNull(update);
        var ids = packetIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).Take(500).ToArray();
        if (ids.Length == 0) return [];
        foreach (var id in ids)
            if (_trafficStore.Get(id) is null) throw new KeyNotFoundException($"Traffic item '{id}' was not found.");

        TrafficAnnotation[] changed;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            changed = ids.Select(id =>
            {
                var current = _annotations.GetValueOrDefault(id) ?? new TrafficAnnotation(id, false, [], null, TrafficReviewStatus.Unreviewed, now, now, 0);
                return current with
                {
                    Starred = update.Starred ?? current.Starred,
                    Tags = update.Tags is null ? current.Tags : NormalizeTags(update.Tags),
                    Note = update.ReplaceNote ? NormalizeNote(update.Note) : current.Note,
                    Status = update.Status ?? current.Status,
                    UpdatedAt = now,
                    Revision = checked(current.Revision + 1)
                };
            }).ToArray();
            CommitLocked(next => { foreach (var annotation in changed) next[annotation.PacketId] = annotation; });
        }
        foreach (var annotation in changed)
            Changed?.Invoke(new TrafficAnnotationChanged("update", annotation.PacketId, Clone(annotation)));
        return changed.Select(Clone).ToArray();
    }

    public bool Delete(string packetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packetId);
        bool removed;
        lock (_gate)
        {
            removed = _annotations.ContainsKey(packetId);
            if (removed)
                CommitLocked(next => next.Remove(packetId));
        }
        if (removed)
            Changed?.Invoke(new TrafficAnnotationChanged("delete", packetId, null));
        return removed;
    }

    public int PruneMissingPackets()
    {
        string[] removed;
        lock (_gate)
        {
            removed = _annotations.Keys.Where(packetId => _trafficStore.Get(packetId) is null).ToArray();
            if (removed.Length > 0)
                CommitLocked(next => { foreach (var packetId in removed) next.Remove(packetId); });
        }
        foreach (var packetId in removed)
            Changed?.Invoke(new TrafficAnnotationChanged("prune", packetId, null));
        return removed.Length;
    }

    public void Reload()
    {
        var document = VersionedJsonFile.ReadWithBackup<AnnotationDocument>(_storageFilePath, IsValidDocument);
        lock (_gate)
        {
            _annotations.Clear();
            foreach (var annotation in document?.Annotations ?? [])
                _annotations.Add(annotation.PacketId, annotation);
        }
    }

    private void CommitLocked(Action<Dictionary<string, TrafficAnnotation>> mutation)
    {
        var next = new Dictionary<string, TrafficAnnotation>(_annotations, StringComparer.Ordinal);
        mutation(next);
        var document = new AnnotationDocument(SchemaVersion,
            next.Values.OrderBy(item => item.CreatedAt).ToArray());
        VersionedJsonFile.Write(_storageFilePath, document, IsValidDocument);
        _annotations.Clear();
        foreach (var pair in next)
            _annotations.Add(pair.Key, pair.Value);
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = tags.Select(tag => tag?.Trim() ?? string.Empty)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > MaxTags)
            throw new ArgumentException($"An annotation cannot have more than {MaxTags} tags.", nameof(tags));
        if (normalized.Any(tag => tag.Length > MaxTagLength))
            throw new ArgumentException($"A tag cannot exceed {MaxTagLength} characters.", nameof(tags));
        return normalized;
    }

    private static string? NormalizeNote(string? note)
    {
        if (note is null)
            return null;
        if (note.Length > MaxNoteLength)
            throw new ArgumentException($"A note cannot exceed {MaxNoteLength} characters.", nameof(note));
        return note;
    }

    private static bool IsValidDocument(AnnotationDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Annotations is null)
            return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Annotations)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.PacketId) || item.Tags is null ||
                item.Revision < 1 || !ids.Add(item.PacketId) || item.Tags.Count > MaxTags ||
                item.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength) ||
                (item.Note?.Length ?? 0) > MaxNoteLength)
                return false;
        }
        return true;
    }

    private static TrafficAnnotation Clone(TrafficAnnotation annotation) =>
        annotation with { Tags = annotation.Tags.ToArray() };

    private sealed record AnnotationDocument(int SchemaVersion, IReadOnlyList<TrafficAnnotation>? Annotations);
}
