using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Automation.Packet;

public enum PacketBodyChunkEncoding { Base64, SafeText }

public sealed record PacketBodyDescriptor(
    long Length,
    string Sha256,
    string? ContentType = null,
    string? Charset = null);

public sealed record PacketBodyChunk(
    long Offset,
    int Count,
    long TotalLength,
    string Data,
    PacketBodyChunkEncoding Encoding)
{
    public bool IsEnd => Offset + Count >= TotalLength;
}

/// <summary>
/// Optional range-based body access for CLI and Agent tools. Implementations should read only the
/// requested byte range from their backing store rather than materializing the complete body.
/// </summary>
public interface IPacketBodyReadService
{
    Task<PacketBodyDescriptor> DescribeBodyAsync(string id, string side, CancellationToken cancellationToken);
    Task<PacketBodyChunk> ReadBodyChunkAsync(
        string id,
        string side,
        long offset,
        int count,
        PacketBodyChunkEncoding preferredEncoding,
        CancellationToken cancellationToken);
}

public static class PacketBodyChunker
{
    public const int DefaultChunkSize = 64 * 1024;
    public const int MaximumChunkSize = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PacketBodyDescriptor Describe(
        ReadOnlySpan<byte> body,
        string? contentType = null,
        string? charset = null) =>
        new(body.Length, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), contentType, charset);

    public static PacketBodyChunk Read(
        ReadOnlySpan<byte> body,
        long offset,
        int count = DefaultChunkSize,
        PacketBodyChunkEncoding preferredEncoding = PacketBodyChunkEncoding.Base64)
    {
        ValidateRange(body.Length, offset, count);
        var actualCount = (int)Math.Min(count, body.Length - offset);
        var range = body.Slice((int)offset, actualCount);
        var encoding = preferredEncoding;
        string data;

        if (preferredEncoding == PacketBodyChunkEncoding.SafeText && TryGetSafeText(range, out var text))
        {
            data = text;
        }
        else
        {
            encoding = PacketBodyChunkEncoding.Base64;
            data = Convert.ToBase64String(range);
        }

        return new PacketBodyChunk(offset, actualCount, body.Length, data, encoding);
    }

    public static void ValidateRange(long totalLength, long offset, int count)
    {
        if (totalLength < 0) throw new ArgumentOutOfRangeException(nameof(totalLength), "Body length cannot be negative.");
        if (offset < 0 || offset > totalLength)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset must be between 0 and {totalLength}.");
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
        if (count > MaximumChunkSize)
            throw new ArgumentOutOfRangeException(nameof(count), $"Count cannot exceed {MaximumChunkSize} bytes.");
    }

    public static byte[] Decode(PacketBodyChunk chunk)
    {
        if (chunk.Count < 0 || chunk.TotalLength < 0 || chunk.Offset < 0 ||
            chunk.Offset > chunk.TotalLength || chunk.Count > chunk.TotalLength - chunk.Offset)
            throw new InvalidDataException("Packet body chunk metadata is inconsistent.");

        byte[] bytes;
        try
        {
            bytes = chunk.Encoding == PacketBodyChunkEncoding.Base64
                ? Convert.FromBase64String(chunk.Data)
                : StrictUtf8.GetBytes(chunk.Data);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Packet body chunk contains invalid base64.", exception);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("Packet body chunk contains invalid text.", exception);
        }

        if (bytes.Length != chunk.Count)
            throw new InvalidDataException($"Decoded chunk length {bytes.Length} does not match declared count {chunk.Count}.");
        return bytes;
    }

    private static bool TryGetSafeText(ReadOnlySpan<byte> bytes, out string text)
    {
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { text = ""; return false; }

        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
            {
                text = "";
                return false;
            }
        }
        return true;
    }
}
