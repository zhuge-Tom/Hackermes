using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Packet;

public sealed record PacketArchiveEntry(
    string Id,
    DateTimeOffset CapturedAt,
    string Request,
    string? Response = null,
    PacketBody? RequestBody = null,
    PacketBody? ResponseBody = null);

[JsonConverter(typeof(JsonStringEnumConverter<PacketBodyEncoding>))]
public enum PacketBodyEncoding { Text, Base64 }

/// <summary>
/// A lossless archive body. Raw HTTP remains available for text-oriented clients while binary-aware
/// clients use this representation to avoid converting arbitrary bytes through UTF-8.
/// </summary>
public sealed record PacketBody(
    string Data,
    PacketBodyEncoding Encoding,
    string? ContentType = null,
    string? Charset = null)
{
    public static PacketBody FromBytes(ReadOnlySpan<byte> bytes, string? contentType = null) =>
        new(Convert.ToBase64String(bytes), PacketBodyEncoding.Base64, contentType);

    public static PacketBody FromText(string text, string? contentType = null, string? charset = "utf-8") =>
        new(text, PacketBodyEncoding.Text, contentType, charset);

    public byte[] GetBytes()
    {
        if (Encoding == PacketBodyEncoding.Base64)
        {
            try { return Convert.FromBase64String(Data); }
            catch (FormatException exception) { throw new InvalidDataException("Packet body contains invalid base64.", exception); }
        }

        try { return ResolveEncoding(Charset).GetBytes(Data); }
        catch (ArgumentException exception) { throw new InvalidDataException($"Unknown packet body charset '{Charset}'.", exception); }
    }

    public string GetSafeDisplayText() =>
        Encoding == PacketBodyEncoding.Text ? Data : $"[base64; {GetBytes().Length} bytes]{Environment.NewLine}{Data}";

    private static System.Text.Encoding ResolveEncoding(string? charset) =>
        string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset);
}

/// <summary>Optional persistence adapter. Capture backends opt in without expanding the live packet contract.</summary>
public interface IPacketArchiveService
{
    Task<IReadOnlyList<PacketArchiveEntry>> ExportArchiveAsync(string? filter, CancellationToken cancellationToken);

    /// <summary>
    /// One bounded batch of the filtered archive plus the total matched entry count, so
    /// path-free (Agent) exchanges can walk stores larger than a single content envelope.
    /// </summary>
    Task<PacketArchivePage> ExportArchivePageAsync(PacketArchiveExchangeQuery query, CancellationToken cancellationToken);
    Task<int> ImportArchiveAsync(IReadOnlyList<PacketArchiveEntry> entries, CancellationToken cancellationToken);
}

/// <summary>Batch selector for bounded archive exchange.</summary>
public sealed record PacketArchiveExchangeQuery(string? Filter, int Offset, int Limit);

/// <summary>One batch of an archive exchange; <see cref="Total"/> is the full matched count.</summary>
public sealed record PacketArchivePage(IReadOnlyList<PacketArchiveEntry> Entries, int Total);

public enum PacketArchiveFormat { HackermesJson, Har }

/// <summary>Portable, deterministic archive codec shared by CLI and Agent integrations.</summary>
public static class PacketArchiveCodec
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static PacketArchiveFormat DetectFormat(string path) =>
        string.Equals(Path.GetExtension(path), ".har", StringComparison.OrdinalIgnoreCase)
            ? PacketArchiveFormat.Har
            : PacketArchiveFormat.HackermesJson;

    public static string Serialize(IReadOnlyList<PacketArchiveEntry> entries, PacketArchiveFormat format) =>
        format == PacketArchiveFormat.Har ? SerializeHar(entries) :
        JsonSerializer.Serialize(new HackermesArchive(Version, entries), JsonOptions);

    public static IReadOnlyList<PacketArchiveEntry> Deserialize(string text, PacketArchiveFormat format) =>
        format == PacketArchiveFormat.Har ? DeserializeHar(text) : DeserializeHackermes(text);

    private static IReadOnlyList<PacketArchiveEntry> DeserializeHackermes(string text)
    {
        var archive = JsonSerializer.Deserialize<HackermesArchive>(text, JsonOptions)
            ?? throw new InvalidDataException("Archive is empty.");
        if (archive.Version != Version) throw new InvalidDataException($"Unsupported archive version {archive.Version}.");
        Validate(archive.Entries);
        return archive.Entries;
    }

    private static string SerializeHar(IReadOnlyList<PacketArchiveEntry> entries)
    {
        var harEntries = entries.Select(entry =>
        {
            var request = HttpPacketCodec.Parse(entry.Request);
            var response = entry.Response is null ? null : HttpPacketCodec.Parse(entry.Response);
            if (request.Kind != HttpPacketKind.Request) throw new InvalidDataException($"Entry '{entry.Id}' request is not a request.");
            return new HarEntry(entry.CapturedAt, entry.Id, ToHarRequest(request, entry.RequestBody),
                response is null ? EmptyHarResponse() : ToHarResponse(response, entry.ResponseBody));
        }).ToArray();
        return JsonSerializer.Serialize(new HarRoot(new HarLog("1.2", new HarCreator("Hackermes", "1"), harEntries)), JsonOptions);
    }

    private static IReadOnlyList<PacketArchiveEntry> DeserializeHar(string text)
    {
        var root = JsonSerializer.Deserialize<HarRoot>(text, JsonOptions)
            ?? throw new InvalidDataException("HAR is empty.");
        var entries = root.Log?.Entries ?? throw new InvalidDataException("HAR log.entries is missing.");
        return entries.Select((entry, index) =>
        {
            var requestBody = FromHarPostData(entry.Request.PostData);
            var responseBody = FromHarContent(entry.Response.Content);
            return new PacketArchiveEntry(
                string.IsNullOrWhiteSpace(entry.Comment) ? $"har-{index + 1}" : entry.Comment!,
                entry.StartedDateTime,
                HttpPacketCodec.Format(FromHarRequest(entry.Request), false),
                entry.Response.Status <= 0 ? null : HttpPacketCodec.Format(FromHarResponse(entry.Response), false),
                requestBody,
                entry.Response.Status <= 0 ? null : responseBody);
        }).ToArray();
    }

    private static HarRequest ToHarRequest(HttpPacket packet, PacketBody? body) => new(packet.Method!, packet.Target!, packet.ProtocolVersion,
        packet.Headers.Select(x => new HarHeader(x.Name, x.Value)).ToArray(),
        body is null
            ? string.IsNullOrEmpty(packet.Body) ? null : new HarPostData(packet.HeaderValues("Content-Type").FirstOrDefault() ?? "", packet.Body, null, null)
            : new HarPostData(body.ContentType ?? packet.HeaderValues("Content-Type").FirstOrDefault() ?? "",
                body.Data, ToHarEncoding(body.Encoding), body.Charset));

    private static HarResponse ToHarResponse(HttpPacket packet, PacketBody? body) => new(packet.StatusCode ?? 0, packet.ReasonPhrase ?? "", packet.ProtocolVersion,
        packet.Headers.Select(x => new HarHeader(x.Name, x.Value)).ToArray(),
        body is null
            ? new HarContent(Encoding.UTF8.GetByteCount(packet.Body), packet.HeaderValues("Content-Type").FirstOrDefault() ?? "", packet.Body, null, null)
            : new HarContent(body.GetBytes().LongLength, body.ContentType ?? packet.HeaderValues("Content-Type").FirstOrDefault() ?? "",
                body.Data, ToHarEncoding(body.Encoding), body.Charset));

    private static HarResponse EmptyHarResponse() => new(0, "", "HTTP/1.1", [], new HarContent(0, "", "", null, null));

    private static HttpPacket FromHarRequest(HarRequest request) => new()
    {
        Kind = HttpPacketKind.Request, Method = request.Method, Target = request.Url,
        ProtocolVersion = request.HttpVersion, Headers = request.Headers.Select(x => new HttpHeader(x.Name, x.Value)).ToArray(),
        Body = request.PostData?.Text ?? ""
    };

    private static HttpPacket FromHarResponse(HarResponse response) => new()
    {
        Kind = HttpPacketKind.Response, StatusCode = response.Status, ReasonPhrase = response.StatusText,
        ProtocolVersion = response.HttpVersion, Headers = response.Headers.Select(x => new HttpHeader(x.Name, x.Value)).ToArray(),
        Body = response.Content.Text ?? ""
    };

    private static PacketBody? FromHarPostData(HarPostData? body) => body is null ? null :
        new PacketBody(body.Text, FromHarEncoding(body.Encoding), body.MimeType, body.Charset);

    private static PacketBody FromHarContent(HarContent body) =>
        new(body.Text ?? "", FromHarEncoding(body.Encoding), body.MimeType, body.Charset);

    private static string? ToHarEncoding(PacketBodyEncoding encoding) =>
        encoding == PacketBodyEncoding.Base64 ? "base64" : null;

    private static PacketBodyEncoding FromHarEncoding(string? encoding) =>
        string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? PacketBodyEncoding.Base64
            : PacketBodyEncoding.Text;

    private static void Validate(IReadOnlyList<PacketArchiveEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id)) throw new InvalidDataException("Archive entry id is required.");
            if (HttpPacketCodec.Parse(entry.Request).Kind != HttpPacketKind.Request) throw new InvalidDataException($"Entry '{entry.Id}' has an invalid request.");
            if (entry.Response is not null && HttpPacketCodec.Parse(entry.Response).Kind != HttpPacketKind.Response)
                throw new InvalidDataException($"Entry '{entry.Id}' has an invalid response.");
            _ = entry.RequestBody?.GetBytes();
            _ = entry.ResponseBody?.GetBytes();
        }
    }

    private sealed record HackermesArchive(int Version, IReadOnlyList<PacketArchiveEntry> Entries);
    private sealed record HarRoot(HarLog? Log);
    private sealed record HarLog(string Version, HarCreator Creator, IReadOnlyList<HarEntry> Entries);
    private sealed record HarCreator(string Name, string Version);
    private sealed record HarEntry(DateTimeOffset StartedDateTime, string? Comment, HarRequest Request, HarResponse Response);
    private sealed record HarHeader(string Name, string Value);
    private sealed record HarPostData(string MimeType, string Text, string? Encoding, string? Charset);
    private sealed record HarRequest(string Method, string Url, string HttpVersion, IReadOnlyList<HarHeader> Headers, HarPostData? PostData);
    private sealed record HarResponse(int Status, string StatusText, string HttpVersion, IReadOnlyList<HarHeader> Headers, HarContent Content);
    private sealed record HarContent(long Size, string MimeType, string? Text, string? Encoding, string? Charset);
}
