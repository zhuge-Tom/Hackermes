using System;
using System.Collections.Generic;

namespace Hookmes.Automation.Packet;

public enum HttpPacketKind { Request, Response }

public sealed record HttpHeader(string Name, string Value);

/// <summary>A loss-aware, editable HTTP/1.x message. Duplicate headers retain their order.</summary>
public sealed record HttpPacket
{
    public required HttpPacketKind Kind { get; init; }
    public required string ProtocolVersion { get; init; }
    public string? Method { get; init; }
    public string? Target { get; init; }
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<HttpHeader> Headers { get; init; } = [];
    public string Body { get; init; } = string.Empty;

    public IEnumerable<string> HeaderValues(string name)
    {
        foreach (var header in Headers)
            if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return header.Value;
    }
}

public sealed class HttpPacketParseException(string message, int line = 0) : FormatException(
    line > 0 ? $"Line {line}: {message}" : message)
{
    public int Line { get; } = line;
}
