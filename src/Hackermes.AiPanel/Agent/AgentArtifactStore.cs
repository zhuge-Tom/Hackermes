using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Agent;

public sealed record AgentArtifact(string FileName, string Path, long Bytes, string Sha256);

public sealed record AgentArtifactInfo(string FileName, long Bytes, DateTimeOffset ModifiedAt);

public sealed record AgentArtifactTextPage(string FileName, long TotalChars, long Offset, long NextOffset, string Content);

public interface IAgentArtifactStore
{
    Task<AgentArtifact> DownloadAsync(Uri source, string? requestedFileName, string? expectedSha256, CancellationToken ct = default);

    /// <summary>Lists every artifact currently stored in the Hackermes artifact store.</summary>
    IReadOnlyList<AgentArtifactInfo> List();

    /// <summary>
    /// Reads one stored artifact as bounded text (offset/limit paging). Binary artifacts
    /// are refused — they belong behind ToolHost adapters, never in model context.
    /// </summary>
    AgentArtifactTextPage ReadText(string fileName, long offset, int maxChars);
}

/// <summary>
/// Downloads an approved HTTPS artifact into Hackermes-owned storage. This component never
/// starts the downloaded file; future ToolHost adapters must separately approve execution.
/// </summary>
public sealed class AgentArtifactStore : IAgentArtifactStore
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public AgentArtifactStore(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<AgentArtifact> DownloadAsync(Uri source, string? requestedFileName, string? expectedSha256, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri || source.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(source.Host) || !string.IsNullOrEmpty(source.UserInfo))
            throw new ArgumentException("Only absolute HTTPS URLs without embedded credentials are allowed.", nameof(source));

        var settings = _settings.Load().Ai;
        var limit = settings.MaxToolDownloadBytes;
        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(finalUri.Host) || !string.IsNullOrEmpty(finalUri.UserInfo))
            throw new InvalidOperationException("The download redirect target must also be an HTTPS URL without embedded credentials.");
        if (response.Content.Headers.ContentLength is long declared && declared > limit)
            throw new InvalidOperationException($"Artifact is {declared} bytes; configured limit is {limit} bytes.");

        var name = SanitizeFileName(requestedFileName) ?? SanitizeFileName(Path.GetFileName(source.LocalPath)) ?? "agent-artifact.bin";
        var directory = Path.Combine(Path.GetDirectoryName(_settings.SettingsFilePath) ?? AppContext.BaseDirectory, "agent-tools");
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, name);
        var temporary = path + ".partial";

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > limit) throw new InvalidOperationException($"Artifact exceeds configured limit of {limit} bytes.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(expectedSha256) && !string.Equals(NormalizeHash(expectedSha256), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Artifact SHA-256 does not match the expected value.");

            File.Move(temporary, path);
            return new AgentArtifact(Path.GetFileName(path), path, total, sha256);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static string? SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = Path.GetFileName(value.Trim());
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name[..Math.Min(name.Length, 120)];
    }

    /// <summary>
    /// Extensions that can never be returned as model context. Everything else is still
    /// sniffed for NUL bytes before the first page is served.
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".jar", ".zip", ".7z", ".rar", ".gz", ".tgz", ".tar", ".war", ".class",
      ".bin", ".pdb", ".so", ".pyd", ".hprof", ".iso", ".img", ".msi", ".apk", ".deb", ".rpm" };

    /// <summary>Artifacts directory, shared with the heapdump adapter's artifact root.</summary>
    internal string StorageDirectory =>
        Path.Combine(Path.GetDirectoryName(_settings.SettingsFilePath) ?? AppContext.BaseDirectory, "agent-tools");

    public IReadOnlyList<AgentArtifactInfo> List()
    {
        var directory = StorageDirectory;
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory)
            .Select(file => new FileInfo(file))
            .Where(file => (file.Attributes & FileAttributes.Hidden) == 0 && file.Extension != ".partial")
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new AgentArtifactInfo(file.Name, file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToArray();
    }

    public AgentArtifactTextPage ReadText(string fileName, long offset, int maxChars)
    {
        var name = SanitizeFileName(fileName) ?? throw new ArgumentException("fileName must be a safe artifact file name.");
        if (offset < 0) throw new ArgumentException("offset must not be negative.");
        maxChars = Math.Clamp(maxChars, 1, 16_000);
        var path = Path.Combine(StorageDirectory, name);
        var root = Path.GetFullPath(StorageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException("The artifact was not found in the Hackermes artifact store; download it first with agent_download_artifact.", name);
        if (BinaryExtensions.Contains(Path.GetExtension(fullPath)))
            throw new InvalidOperationException("This artifact is binary; it is never rendered into model context. Use a ToolHost adapter (e.g. exploit.heapdump.analyze) instead.");
        using var stream = File.OpenRead(fullPath);
        Span<byte> sniff = stackalloc byte[512];
        var sniffed = stream.Read(sniff);
        if (sniffed > 0 && sniff[..sniffed].Contains((byte)0))
            throw new InvalidOperationException("This artifact looks binary (NUL bytes detected); it is never rendered into model context.");
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[maxChars];
        var read = reader.Read(buffer, 0, maxChars);
        return new AgentArtifactTextPage(name, CountTextChars(stream), offset, offset + read, new string(buffer, 0, read));
    }

    /// <summary>Counts the file's characters (capped, for paging hints only — never loads it all into context).</summary>
    private static long CountTextChars(FileStream stream)
    {
        const long cap = 4_000_000;
        stream.Seek(0, SeekOrigin.Begin);
        using var counter = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        long total = 0;
        Span<char> chunk = new char[65_536];
        int readAll;
        while ((readAll = counter.Read(chunk)) > 0)
        {
            total += readAll;
            if (total > cap) return cap;
        }
        return total;
    }

    private static string UniquePath(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        return Path.Combine(directory, $"{baseName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}{extension}");
    }

    private static string NormalizeHash(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit)) throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.");
        return normalized;
    }
}
