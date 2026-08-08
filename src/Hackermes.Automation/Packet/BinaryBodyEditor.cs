using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Packet;

public enum BinaryTextEncoding { Hex, Base64 }
public enum BinaryEditKind { Replace, Insert, Delete }

public sealed record BinaryBodyEdit(
    BinaryEditKind Kind,
    long Offset,
    long Count = 0,
    string? Data = null,
    BinaryTextEncoding DataEncoding = BinaryTextEncoding.Hex);

public interface IPacketBodyEditService
{
    Task<PacketBodyDescriptor> EditBodyAsync(
        string id, string side, BinaryBodyEdit edit, CancellationToken cancellationToken);
}

public static class BinaryBodyCodec
{
    public static byte[] Parse(string value, BinaryTextEncoding encoding) => encoding switch
    {
        BinaryTextEncoding.Hex => ParseHex(value),
        BinaryTextEncoding.Base64 => ParseBase64(value),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    public static string Format(ReadOnlySpan<byte> value, BinaryTextEncoding encoding) => encoding switch
    {
        BinaryTextEncoding.Hex => Convert.ToHexString(value).ToLowerInvariant(),
        BinaryTextEncoding.Base64 => Convert.ToBase64String(value),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static byte[] ParseHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var compact = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if ((compact.Length & 1) != 0) throw new InvalidDataException("Hex data must contain complete byte pairs.");
        try { return Convert.FromHexString(compact); }
        catch (FormatException exception) { throw new InvalidDataException("Hex data contains a non-hexadecimal character.", exception); }
    }

    private static byte[] ParseBase64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try { return Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new InvalidDataException("Data is not valid base64.", exception); }
    }
}

/// <summary>Strict immutable binary mutations shared by CLI, Agent and UI integrations.</summary>
public static class BinaryBodyEditor
{
    public const int MaximumEditDataSize = 4 * 1024 * 1024;
    public const int MaximumBodySize = 64 * 1024 * 1024;

    public static byte[] Apply(ReadOnlySpan<byte> body, BinaryBodyEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (body.Length > MaximumBodySize)
            throw new InvalidDataException($"Body exceeds the {MaximumBodySize}-byte editing limit.");
        ValidateOperation(edit);

        return edit.Kind switch
        {
            BinaryEditKind.Replace => Replace(body, edit.Offset, edit.Count, ParseData(edit)),
            BinaryEditKind.Insert => Insert(body, edit.Offset, ParseData(edit)),
            BinaryEditKind.Delete => Delete(body, edit.Offset, edit.Count),
            _ => throw new ArgumentOutOfRangeException(nameof(edit.Kind))
        };
    }

    public static PacketBody Apply(PacketBody body, BinaryBodyEdit edit)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = Apply(body.GetBytes(), edit);
        return PacketBody.FromBytes(result, body.ContentType) with { Charset = body.Charset };
    }

    public static byte[] Replace(ReadOnlySpan<byte> body, long offset, long count, ReadOnlySpan<byte> replacement)
    {
        ValidateRange(body.Length, offset, count, allowEnd: true);
        ValidateEditData(replacement.Length);
        var resultLength = checked(body.Length - (int)count + replacement.Length);
        ValidateResultLength(resultLength);

        var result = new byte[resultLength];
        body[..(int)offset].CopyTo(result);
        replacement.CopyTo(result.AsSpan((int)offset));
        body[(int)(offset + count)..].CopyTo(result.AsSpan((int)offset + replacement.Length));
        return result;
    }

    public static byte[] Insert(ReadOnlySpan<byte> body, long offset, ReadOnlySpan<byte> data) =>
        Replace(body, offset, 0, data);

    public static byte[] Delete(ReadOnlySpan<byte> body, long offset, long count) =>
        Replace(body, offset, count, ReadOnlySpan<byte>.Empty);

    /// <summary>Returns a packet with exactly one canonical Content-Length header and no conflicting Transfer-Encoding.</summary>
    public static HttpPacket UpdateContentLength(HttpPacket packet, long bodyLength)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (bodyLength < 0) throw new ArgumentOutOfRangeException(nameof(bodyLength));

        var headers = new List<HttpHeader>(packet.Headers.Count + 1);
        var inserted = false;
        foreach (var header in packet.Headers)
        {
            if (string.Equals(header.Name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                headers.Add(header);
                continue;
            }

            if (!inserted)
            {
                headers.Add(new HttpHeader("Content-Length", bodyLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                inserted = true;
            }
        }
        if (!inserted) headers.Add(new HttpHeader("Content-Length", bodyLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return packet with { Headers = headers };
    }

    private static byte[] ParseData(BinaryBodyEdit edit)
    {
        if (edit.Data is null) throw new InvalidDataException($"{edit.Kind} requires encoded data.");
        var data = BinaryBodyCodec.Parse(edit.Data, edit.DataEncoding);
        ValidateEditData(data.Length);
        return data;
    }

    private static void ValidateOperation(BinaryBodyEdit edit)
    {
        if (edit.Kind == BinaryEditKind.Insert && edit.Count != 0)
            throw new InvalidDataException("Insert operations must use a zero count.");
        if (edit.Kind == BinaryEditKind.Delete && edit.Data is not null)
            throw new InvalidDataException("Delete operations cannot contain data.");
    }

    private static void ValidateRange(int length, long offset, long count, bool allowEnd)
    {
        if (offset < 0 || offset > length || (!allowEnd && offset == length))
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset must be between 0 and {length}.");
        if (count < 0 || count > length - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "Count extends beyond the body.");
    }

    private static void ValidateEditData(int length)
    {
        if (length > MaximumEditDataSize)
            throw new InvalidDataException($"Edit data exceeds the {MaximumEditDataSize}-byte limit.");
    }

    private static void ValidateResultLength(int length)
    {
        if (length > MaximumBodySize)
            throw new InvalidDataException($"Edited body exceeds the {MaximumBodySize}-byte limit.");
    }
}
