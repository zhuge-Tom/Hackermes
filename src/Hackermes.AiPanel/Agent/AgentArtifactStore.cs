using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Agent;

public sealed record AgentArtifact(string FileName, string Path, long Bytes, string Sha256);

public interface IAgentArtifactStore
{
    Task<AgentArtifact> DownloadAsync(Uri source, string? requestedFileName, string? expectedSha256, CancellationToken ct = default);
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
