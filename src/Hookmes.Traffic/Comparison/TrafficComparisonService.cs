using Hookmes.Traffic.Models;
using Hookmes.Traffic.Persistence;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hookmes.Traffic.Comparison;

public interface ITrafficComparisonService
{
    event Action<TrafficComparisonChanged>? Changed;
    string StorageFilePath { get; }
    TrafficComparisonResult Compare(ComparisonSource left, ComparisonSource right);
    IReadOnlyList<TrafficComparisonSession> GetAll();
    TrafficComparisonSession? Get(string id);
    TrafficComparisonSession Create(string name, ComparisonSource left, ComparisonSource right);
    TrafficComparisonSession Rename(string id, string name);
    TrafficComparisonSession UpdateSources(string id, ComparisonSource left, ComparisonSource right);
    TrafficComparisonSession Recalculate(string id);
    bool Delete(string id);
    void Reload();
}

/// <summary>Compares captured traffic and repeater rounds without depending on presentation concerns.</summary>
public sealed class TrafficComparisonService : ITrafficComparisonService
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly ITrafficStore _trafficStore;
    private readonly IRepeaterService _repeater;
    private readonly string _storageFilePath;
    private readonly Dictionary<string, TrafficComparisonSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public TrafficComparisonService(ITrafficStore trafficStore, IRepeaterService repeater)
        : this(trafficStore, repeater, VersionedJsonFile.DefaultPath("comparisons.json"))
    {
    }

    public TrafficComparisonService(ITrafficStore trafficStore, IRepeaterService repeater, string storageFilePath)
    {
        _trafficStore = trafficStore ?? throw new ArgumentNullException(nameof(trafficStore));
        _repeater = repeater ?? throw new ArgumentNullException(nameof(repeater));
        if (string.IsNullOrWhiteSpace(storageFilePath))
            throw new ArgumentException("Storage file path is required.", nameof(storageFilePath));
        _storageFilePath = System.IO.Path.GetFullPath(storageFilePath);
        Reload();
    }

    public event Action<TrafficComparisonChanged>? Changed;

    public string StorageFilePath => _storageFilePath;

    public TrafficComparisonResult Compare(ComparisonSource left, ComparisonSource right) =>
        CompareSnapshots(Resolve(left), Resolve(right));

    public IReadOnlyList<TrafficComparisonSession> GetAll()
    {
        lock (_gate)
            return _order.Select(id => _sessions[id]).ToArray();
    }

    public TrafficComparisonSession? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _sessions.GetValueOrDefault(id);
    }

    public TrafficComparisonSession Create(string name, ComparisonSource left, ComparisonSource right)
    {
        ValidateName(name);
        var result = Compare(left, right);
        var now = DateTimeOffset.UtcNow;
        var session = new TrafficComparisonSession(Guid.NewGuid().ToString("N"), name.Trim(), left, right, result, now, now, 1);
        lock (_gate)
        {
            CommitLocked(() =>
            {
                _sessions.Add(session.Id, session);
                _order.Add(session.Id);
            });
        }
        Publish("create", session);
        return session;
    }

    public TrafficComparisonSession Rename(string id, string name)
    {
        ValidateName(name);
        TrafficComparisonSession updated;
        lock (_gate)
        {
            var current = GetRequired(id);
            updated = current with { Name = name.Trim(), UpdatedAt = DateTimeOffset.UtcNow, Revision = checked(current.Revision + 1) };
            CommitLocked(() => _sessions[id] = updated);
        }
        Publish("rename", updated);
        return updated;
    }

    public TrafficComparisonSession UpdateSources(string id, ComparisonSource left, ComparisonSource right)
    {
        var result = Compare(left, right);
        TrafficComparisonSession updated;
        lock (_gate)
        {
            var current = GetRequired(id);
            updated = current with
            {
                Left = left,
                Right = right,
                Result = result,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = checked(current.Revision + 1)
            };
            CommitLocked(() => _sessions[id] = updated);
        }
        Publish("update-sources", updated);
        return updated;
    }

    public TrafficComparisonSession Recalculate(string id)
    {
        ComparisonSource left;
        ComparisonSource right;
        lock (_gate)
        {
            var current = GetRequired(id);
            left = current.Left;
            right = current.Right;
        }
        var result = Compare(left, right);
        TrafficComparisonSession updated;
        lock (_gate)
        {
            var current = GetRequired(id);
            updated = current with { Result = result, UpdatedAt = DateTimeOffset.UtcNow, Revision = checked(current.Revision + 1) };
            CommitLocked(() => _sessions[id] = updated);
        }
        Publish("recalculate", updated);
        return updated;
    }

    public bool Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bool removed;
        lock (_gate)
        {
            removed = _sessions.ContainsKey(id);
            if (removed)
            {
                CommitLocked(() =>
                {
                    _sessions.Remove(id);
                    _order.Remove(id);
                });
            }
        }
        if (removed)
            Changed?.Invoke(new TrafficComparisonChanged("delete", id, null));
        return removed;
    }

    public void Reload()
    {
        TrafficComparisonSession[] loaded;
        lock (_gate)
        {
            var document = VersionedJsonFile.ReadWithBackup<ComparisonDocument>(_storageFilePath,
                IsValidDocument);
            loaded = document is null ? [] : Normalize(document.Sessions);
            _sessions.Clear();
            _order.Clear();
            foreach (var session in loaded)
            {
                _sessions.Add(session.Id, session);
                _order.Add(session.Id);
            }
        }
        foreach (var session in loaded)
            Publish("reload", session);
    }

    private void PersistLocked() => VersionedJsonFile.Write(_storageFilePath,
        new ComparisonDocument(SchemaVersion, _order.Select(id => _sessions[id]).ToArray()),
        IsValidDocument);

    private void CommitLocked(Action mutation)
    {
        var previousSessions = new Dictionary<string, TrafficComparisonSession>(_sessions, StringComparer.Ordinal);
        var previousOrder = _order.ToArray();
        mutation();
        try
        {
            PersistLocked();
        }
        catch
        {
            _sessions.Clear();
            foreach (var pair in previousSessions)
                _sessions.Add(pair.Key, pair.Value);
            _order.Clear();
            _order.AddRange(previousOrder);
            throw;
        }
    }

    private static TrafficComparisonSession[] Normalize(IReadOnlyList<TrafficComparisonSession>? sessions)
    {
        if (sessions is null)
            return [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return sessions.Where(session =>
                !string.IsNullOrWhiteSpace(session.Id) &&
                !string.IsNullOrWhiteSpace(session.Name) &&
                session.Left is not null && session.Right is not null && session.Result is not null &&
                ids.Add(session.Id))
            .ToArray();
    }

    private static bool IsValidDocument(ComparisonDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Sessions is null)
            return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return document.Sessions.All(session =>
            session is not null &&
            !string.IsNullOrWhiteSpace(session.Id) &&
            !string.IsNullOrWhiteSpace(session.Name) &&
            session.Left is not null && session.Right is not null && session.Result is not null &&
            session.Result.StartLine is not null && session.Result.Headers is not null && session.Result.Body is not null &&
            session.Result.Body.Left is not null && session.Result.Body.Right is not null &&
            ids.Add(session.Id));
    }

    private HttpSnapshot Resolve(ComparisonSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            ComparisonSourceKind.TrafficRequest => ResolveTraffic(source, response: false),
            ComparisonSourceKind.TrafficResponse => ResolveTraffic(source, response: true),
            ComparisonSourceKind.RepeaterRequest => ResolveRepeater(source, response: false),
            ComparisonSourceKind.RepeaterResponse => ResolveRepeater(source, response: true),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }

    private HttpSnapshot ResolveTraffic(ComparisonSource source, bool response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.PacketId);
        var packet = _trafficStore.Get(source.PacketId)
            ?? throw new KeyNotFoundException($"Traffic item '{source.PacketId}' was not found.");
        if (!response)
            return HttpSnapshot.Request(packet.Method, packet.Url, packet.RequestHeaders, packet.RequestBody);
        if (packet.ResponseStatus is null)
            throw new InvalidOperationException($"Traffic item '{source.PacketId}' has no response.");
        return HttpSnapshot.Response(packet.ResponseStatus.Value, packet.ResponseStatusText, packet.ResponseHeaders, packet.ResponseBody);
    }

    private HttpSnapshot ResolveRepeater(ComparisonSource source, bool response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.DraftId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SendResultId);
        var draft = _repeater.Get(source.DraftId)
            ?? throw new KeyNotFoundException($"Repeater draft '{source.DraftId}' was not found.");
        var send = draft.History.FirstOrDefault(item => string.Equals(item.Id, source.SendResultId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Repeater result '{source.SendResultId}' was not found.");
        if (!response)
            return HttpSnapshot.Request(send.Request.Method, send.Request.Url, send.Request.Headers, send.Request.Body);
        if (send.Status != RepeaterSendStatus.Completed || send.ResponseStatus is null)
            throw new InvalidOperationException($"Repeater result '{source.SendResultId}' has no completed response.");
        return HttpSnapshot.Response(send.ResponseStatus.Value, send.ResponseStatusText, send.ResponseHeaders, send.ResponseBody);
    }

    private static TrafficComparisonResult CompareSnapshots(HttpSnapshot left, HttpSnapshot right)
    {
        var startLine = new[]
        {
            Field("kind", left.Kind, right.Kind),
            Field("method", left.Method, right.Method),
            Field("url", left.Url, right.Url),
            Field("status", left.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture), right.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Field("statusText", left.StatusText, right.StatusText)
        };
        var headers = CompareHeaders(left.Headers, right.Headers);
        var body = CompareBodies(left, right);
        return new TrafficComparisonResult(startLine, headers, body,
            startLine.All(item => item.Kind == DifferenceKind.Unchanged) &&
            headers.All(item => item.Kind == DifferenceKind.Unchanged) && body.Equal);
    }

    private static StartLineFieldDifference Field(string field, string? left, string? right) =>
        new(field, left, right, Difference(left, right));

    private static HeaderDifference[] CompareHeaders(IReadOnlyList<TrafficHeader> left, IReadOnlyList<TrafficHeader> right)
    {
        var leftGroups = GroupHeaders(left);
        var rightGroups = GroupHeaders(right);
        var names = leftGroups.Keys.Concat(rightGroups.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        return names.Select(name =>
        {
            var leftValues = leftGroups.GetValueOrDefault(name) ?? [];
            var rightValues = rightGroups.GetValueOrDefault(name) ?? [];
            var kind = leftValues.Count == 0 ? DifferenceKind.Added
                : rightValues.Count == 0 ? DifferenceKind.Removed
                : leftValues.SequenceEqual(rightValues, StringComparer.Ordinal) ? DifferenceKind.Unchanged
                : DifferenceKind.Modified;
            return new HeaderDifference(name, leftValues, rightValues, kind);
        }).ToArray();
    }

    private static Dictionary<string, IReadOnlyList<string>> GroupHeaders(IReadOnlyList<TrafficHeader> headers) =>
        headers.GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.First().Name, group => (IReadOnlyList<string>)group.Select(header => header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);

    private static BodyDifference CompareBodies(HttpSnapshot left, HttpSnapshot right)
    {
        var leftBody = left.Body ?? [];
        var rightBody = right.Body ?? [];
        var equal = leftBody.AsSpan().SequenceEqual(rightBody);
        int? firstDifference = null;
        if (!equal)
        {
            var common = Math.Min(leftBody.Length, rightBody.Length);
            var index = 0;
            while (index < common && leftBody[index] == rightBody[index]) index++;
            firstDifference = index;
        }
        return new BodyDifference(equal, Summarize(leftBody, ContentType(left.Headers)),
            Summarize(rightBody, ContentType(right.Headers)), firstDifference);
    }

    private static BodySummary Summarize(byte[] body, string? contentType)
    {
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (body.Length == 0)
            return new BodySummary(BodyContentKind.Empty, 0, hash, contentType, string.Empty);
        var text = TryDecodeText(body, contentType);
        return text is null
            ? new BodySummary(BodyContentKind.Binary, body.Length, hash, contentType, null)
            : new BodySummary(BodyContentKind.Text, body.Length, hash, contentType, text);
    }

    private static string? TryDecodeText(byte[] body, string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        var declaredText = mediaType is not null &&
            (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));
        if (!declaredText && body.Any(value => value == 0))
            return null;
        try
        {
            var decoder = new UTF8Encoding(false, true);
            var text = decoder.GetString(body);
            if (!declaredText && text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
                return null;
            return text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? ContentType(IReadOnlyList<TrafficHeader> headers) =>
        headers.FirstOrDefault(header => string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;

    private static DifferenceKind Difference(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal) ? DifferenceKind.Unchanged
        : left is null ? DifferenceKind.Added
        : right is null ? DifferenceKind.Removed
        : DifferenceKind.Modified;

    private TrafficComparisonSession GetRequired(string id) => _sessions.TryGetValue(id, out var session)
        ? session
        : throw new KeyNotFoundException($"Comparison session '{id}' was not found.");

    private void Publish(string operation, TrafficComparisonSession session) =>
        Changed?.Invoke(new TrafficComparisonChanged(operation, session.Id, session));

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Comparison name is required.", nameof(name));
        if (name.Trim().Length > 200)
            throw new ArgumentException("Comparison name cannot exceed 200 characters.", nameof(name));
    }

    private sealed record HttpSnapshot(
        string Kind,
        string? Method,
        string? Url,
        int? Status,
        string? StatusText,
        IReadOnlyList<TrafficHeader> Headers,
        byte[]? Body)
    {
        public static HttpSnapshot Request(string method, string url, IReadOnlyList<TrafficHeader> headers, byte[]? body) =>
            new("request", method, url, null, null, headers, body);
        public static HttpSnapshot Response(int status, string? statusText, IReadOnlyList<TrafficHeader> headers, byte[]? body) =>
            new("response", null, null, status, statusText, headers, body);
    }

    private sealed record ComparisonDocument(int SchemaVersion, IReadOnlyList<TrafficComparisonSession>? Sessions);
}
