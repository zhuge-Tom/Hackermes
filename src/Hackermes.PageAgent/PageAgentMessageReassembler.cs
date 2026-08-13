using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Hackermes.PageAgent;

/// <summary>
/// 对 Page Agent 的 CDP binding 分片做有界、严格按序重组。
/// <para>
/// binding 是页面可触发的不可信输入;因此本类不接受稀疏、乱序或重复分片,
/// 并对单片、总长、并发数和存活时间同时设限。
/// </para>
/// </summary>
public sealed class PageAgentMessageReassembler
{
    public const int ChunkDataChars = 16 * 1024;
    public const int MaxMessageChars = 2 * 1024 * 1024;
    public const int MaxChunks = 128;
    public const int MaxConcurrentMessages = 16;
    public const int MaxDirectMessageChars = 64 * 1024;
    public static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, PendingMessage> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PageAgentMessageReassembler(Func<DateTimeOffset>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// 接收一个 binding payload。只有当它是完整直达消息,或者本片完成了严格重组时,
    /// 才返回 <see langword="true"/>并设置 <paramref name="message"/>。
    /// </summary>
    public bool TryAccept(string payload, out string? message)
    {
        message = null;
        if (string.IsNullOrEmpty(payload))
            return false;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("__hmChunk", out var marker))
        {
            if (payload.Length > MaxDirectMessageChars)
                return false;

            message = payload;
            return true;
        }

        if (marker.ValueKind != JsonValueKind.Number || !marker.TryGetInt32(out var version) || version != 1
            || !TryReadChunk(root, out var chunk))
        {
            return false;
        }

        lock (_gate)
        {
            var now = _utcNow();
            RemoveExpired(now);

            if (!_pending.TryGetValue(chunk.Id, out var pending))
            {
                if (chunk.Index != 0 || _pending.Count >= MaxConcurrentMessages)
                    return false;

                pending = new PendingMessage(chunk.Total, now);
                _pending.Add(chunk.Id, pending);
            }
            else if (pending.Total != chunk.Total || chunk.Index != pending.NextIndex)
            {
                // ID 复用、重复片或乱序片都使该消息整体失效,不留可被继续拼接的残片。
                _pending.Remove(chunk.Id);
                return false;
            }

            if (pending.Length > MaxMessageChars - chunk.Data.Length)
            {
                _pending.Remove(chunk.Id);
                return false;
            }

            pending.Builder.Append(chunk.Data);
            pending.Length += chunk.Data.Length;
            pending.NextIndex++;

            if (pending.NextIndex != pending.Total)
                return false;

            _pending.Remove(chunk.Id);
            message = pending.Builder.ToString();
            return message.Length <= MaxMessageChars;
        }
    }

    private static bool TryReadChunk(JsonElement root, out Chunk chunk)
    {
        chunk = default;
        if (!root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("index", out var indexElement)
            || !indexElement.TryGetInt32(out var index)
            || !root.TryGetProperty("total", out var totalElement)
            || !totalElement.TryGetInt32(out var total)
            || !root.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var id = idElement.GetString() ?? string.Empty;
        var data = dataElement.GetString() ?? string.Empty;
        if (!IsValidId(id)
            || total is < 2 or > MaxChunks
            || index < 0 || index >= total
            || data.Length is < 1 or > ChunkDataChars
            || (index < total - 1 && data.Length != ChunkDataChars))
        {
            return false;
        }

        chunk = new Chunk(id, index, total, data);
        return true;
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is < 1 or > 64)
            return false;

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }

        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var item in _pending)
        {
            if (now - item.Value.StartedAt > MessageTimeout)
            {
                expired ??= [];
                expired.Add(item.Key);
            }
        }

        if (expired is null)
            return;

        foreach (var id in expired)
            _pending.Remove(id);
    }

    private readonly record struct Chunk(string Id, int Index, int Total, string Data);

    private sealed class PendingMessage(int total, DateTimeOffset startedAt)
    {
        public int Total { get; } = total;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int NextIndex { get; set; }
        public int Length { get; set; }
        public StringBuilder Builder { get; } = new(ChunkDataChars);
    }
}
