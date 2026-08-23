using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hackermes.Automation.Packet;

public sealed record MultipartPart(
    int Occurrence,
    string Name,
    string? ContentType,
    string? Filename,
    long ValueOffset,
    int ValueLength,
    string DisplayValue,
    bool IsBinary)
{
    public bool IsText => !IsBinary;
}

/// <summary>
/// Bounded reader/writer for multipart/form-data bodies. Parts are located by their
/// Content-Disposition name and edited by byte-range splicing, so untouched parts —
/// including binary ones — are preserved verbatim. Bounds mirror the other structured
/// editors: few parts, short names, bounded values, bounded boundary tokens.
/// </summary>
public static class BoundedMultipartBody
{
    public const int MaximumParts = 64;
    public const int MaximumBoundaryLength = 128;
    public const int MaximumNameLength = HttpPacketParameters.MaximumNameLength;

    public static IReadOnlyList<MultipartPart> ReadParts(byte[] body, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(body);
        var boundary = ExtractBoundary(contentType);
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<MultipartPart>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        var cursor = Find(body, delimiter, 0);
        while (cursor >= 0 && parts.Count < MaximumParts)
        {
            var headEnd = cursor + delimiter.Length;
            if (IsCloseDelimiter(body, headEnd)) break;
            headEnd = SkipCrlf(body, headEnd);
            if (headEnd < 0) break;

            var next = Find(body, delimiter, headEnd);
            if (next < 0) break;
            var valueEnd = next - 2; // the CRLF preceding the delimiter belongs to the framing
            if (valueEnd < headEnd) break;

            var part = ParsePart(body, headEnd, valueEnd, occurrences);
            if (part is not null) parts.Add(part);
            cursor = next; // the delimiter ending this part opens the next one
        }
        return parts;
    }

    /// <summary>Replaces one part's value bytes; every other byte of the body is preserved.</summary>
    public static byte[] SetPartValue(byte[] body, string? contentType, string name, int occurrence, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaximumNameLength)
            throw new ArgumentException($"Part name must not exceed {MaximumNameLength} characters.", nameof(name));
        if (occurrence < 0) throw new ArgumentOutOfRangeException(nameof(occurrence));
        if (value.Length > HttpPacketParameters.MaximumValueLength)
            throw new ArgumentException($"Part value must not exceed {HttpPacketParameters.MaximumValueLength} bytes.", nameof(value));

        var found = 0;
        foreach (var part in ReadParts(body, contentType))
        {
            if (!part.Name.Equals(name, StringComparison.Ordinal)) continue;
            if (found++ != occurrence) continue;
            var merged = new byte[body.Length - part.ValueLength + value.Length];
            Array.Copy(body, 0, merged, 0, part.ValueOffset);
            Array.Copy(value, 0, merged, part.ValueOffset, value.Length);
            Array.Copy(body, part.ValueOffset + part.ValueLength, merged, part.ValueOffset + value.Length,
                body.Length - part.ValueOffset - part.ValueLength);
            return merged;
        }
        throw new KeyNotFoundException(
            $"multipart part '{name}' occurrence {occurrence} was not found.");
    }

    public static string ExtractBoundary(string? contentType)
    {
        if (contentType is null || !contentType.TrimStart().StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Multipart editing requires a multipart/* Content-Type.");
        foreach (var segment in contentType.Split(';'))
        {
            var trimmed = segment.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
            var boundary = trimmed[9..].Trim().Trim('"');
            if (boundary.Length == 0 || boundary.Length > MaximumBoundaryLength ||
                boundary.IndexOfAny(['\r', '\n']) >= 0)
                throw new InvalidDataException("Content-Type carries an invalid multipart boundary.");
            return boundary;
        }
        throw new InvalidDataException("Multipart Content-Type does not declare a boundary.");
    }

    private static MultipartPart? ParsePart(byte[] body, int start, int valueEnd, Dictionary<string, int> occurrences)
    {
        var headerTerminator = Find(body, CrlfCrlf, start);
        if (headerTerminator < 0 || headerTerminator > valueEnd) return null;

        var headerText = Encoding.UTF8.GetString(body, start, headerTerminator - start);
        string? name = null, partContentType = null, filename = null;
        foreach (var line in headerText.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            var colon = clean.IndexOf(':');
            if (colon <= 0) continue;
            var headerName = clean[..colon].Trim();
            var headerValue = clean[(colon + 1)..].Trim();
            if (headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                (name, filename) = ParseDisposition(headerValue);
            else if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                partContentType = headerValue;
        }
        if (name is null || name.Length == 0 || name.Length > MaximumNameLength) return null;

        var valueOffset = headerTerminator + CrlfCrlf.Length;
        var valueLength = valueEnd - valueOffset;
        var occurrence = occurrences.GetValueOrDefault(name);
        occurrences[name] = occurrence + 1;
        var (display, isBinary) = Display(body, valueOffset, valueLength);
        return new MultipartPart(occurrence, name, partContentType, filename,
            valueOffset, valueLength, display, isBinary);
    }

    private static (string? Name, string? Filename) ParseDisposition(string value)
    {
        string? name = null, filename = null;
        foreach (var segment in value.Split(';'))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0) continue;
            var key = segment[..equals].Trim();
            var parameter = segment[(equals + 1)..].Trim().Trim('"');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = parameter;
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase)) filename = parameter;
        }
        return (name, filename);
    }

    private static (string Display, bool IsBinary) Display(byte[] body, int offset, int length)
    {
        try
        {
            var text = Encoding.UTF8.GetString(body, offset, length);
            foreach (var character in text)
                if (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                    return ($"<binary {length} bytes>", true);
            return (text, false);
        }
        catch (DecoderFallbackException)
        {
            return ($"<binary {length} bytes>", true);
        }
    }

    private static bool IsCloseDelimiter(byte[] body, int position) =>
        position + 1 < body.Length && body[position] == '-' && body[position + 1] == '-';

    private static int SkipCrlf(byte[] body, int position)
    {
        if (position + 1 < body.Length && body[position] == '\r' && body[position + 1] == '\n') return position + 2;
        return -1;
    }

    private static int Find(byte[] body, ReadOnlySpan<byte> needle, int start)
    {
        if (start >= body.Length) return -1;
        var found = body.AsSpan(start).IndexOf(needle);
        return found < 0 ? -1 : found + start;
    }

    private static readonly byte[] CrlfCrlf = [(byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n'];
}
