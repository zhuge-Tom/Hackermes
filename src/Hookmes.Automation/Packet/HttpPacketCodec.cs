using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hookmes.Automation.Packet;

public static class HttpPacketCodec
{
    public static HttpPacket Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separator < 0)
        {
            separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        var head = separator < 0 ? raw : raw[..separator];
        var body = separator < 0 ? string.Empty : raw[(separator + separatorLength)..];
        var lines = head.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            throw new HttpPacketParseException("Missing request or status line.", 1);

        var headers = new List<HttpHeader>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) continue;
            if (char.IsWhiteSpace(lines[i][0]))
                throw new HttpPacketParseException("Obsolete folded headers are not accepted.", i + 1);
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                throw new HttpPacketParseException("Header must contain a non-empty name followed by ':'.", i + 1);
            var name = lines[i][..colon].Trim();
            if (name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                throw new HttpPacketParseException("Invalid header name.", i + 1);
            headers.Add(new HttpHeader(name, lines[i][(colon + 1)..].Trim()));
        }

        var first = lines[0].TrimEnd('\r');
        if (first.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var code) || code is < 100 or > 999)
                throw new HttpPacketParseException("Invalid HTTP status line.", 1);
            return new HttpPacket { Kind = HttpPacketKind.Response, ProtocolVersion = parts[0], StatusCode = code,
                ReasonPhrase = parts.Length == 3 ? parts[2] : string.Empty, Headers = headers, Body = body };
        }

        var request = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || !request[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            throw new HttpPacketParseException("Invalid HTTP request line.", 1);
        return new HttpPacket { Kind = HttpPacketKind.Request, Method = request[0], Target = request[1],
            ProtocolVersion = request[2], Headers = headers, Body = body };
    }

    public static string Format(HttpPacket packet, bool prettyBody = false)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var builder = new StringBuilder();
        if (packet.Kind == HttpPacketKind.Request)
            builder.Append(packet.Method).Append(' ').Append(packet.Target).Append(' ').Append(packet.ProtocolVersion);
        else
            builder.Append(packet.ProtocolVersion).Append(' ').Append(packet.StatusCode).Append(' ').Append(packet.ReasonPhrase);
        builder.Append("\r\n");
        foreach (var header in packet.Headers)
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        builder.Append("\r\n").Append(prettyBody ? PrettyBody(packet) : packet.Body);
        return builder.ToString();
    }

    public static string PrettyBody(HttpPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.Body)) return packet.Body;
        var contentType = packet.HeaderValues("Content-Type").FirstOrDefault() ?? string.Empty;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            packet.Body.TrimStart().StartsWith('{') || packet.Body.TrimStart().StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(packet.Body);
                return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException) { }
        }
        if (contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return string.Join(Environment.NewLine, packet.Body.Split('&').Select(pair =>
            {
                var parts = pair.Split('=', 2);
                return $"{Uri.UnescapeDataString(parts[0].Replace('+', ' '))} = " +
                    (parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);
            }));
        return packet.Body;
    }
}
