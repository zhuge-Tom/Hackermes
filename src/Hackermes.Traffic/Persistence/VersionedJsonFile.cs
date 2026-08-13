using System;
using Hackermes.Base;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Hackermes.Traffic.Persistence;

internal static class VersionedJsonFile
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static T? ReadWithBackup<T>(string path)
    {
        return TryRead<T>(path) ?? TryRead<T>(path + ".bak");
    }

    public static T? ReadWithBackup<T>(string path, Func<T, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        var primary = TryRead<T>(path);
        if (primary is not null && accept(primary))
            return primary;
        var backup = TryRead<T>(path + ".bak");
        return backup is not null && accept(backup) ? backup : default;
    }

    public static void Write<T>(string path, T document, Func<T, bool>? acceptExistingForBackup = null)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, Options), new UTF8Encoding(false));
            // Never replace a known-good backup with a corrupt or incompatible primary.
            var existing = TryRead<T>(path);
            if (existing is not null && (acceptExistingForBackup is null || acceptExistingForBackup(existing)))
                File.Copy(path, path + ".bak", overwrite: true);
            MoveWithRetry(temporaryPath, path);
        }
        catch (UnauthorizedAccessException exception)
        {
            // Callers expose persistence failures as IOException. Normalize the Windows
            // access-denied variant so all platforms have the same public contract.
            throw new IOException($"Cannot write persistent file '{path}'.", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string DefaultPath(string fileName)
    {
        return AppDataPaths.Resolve(fileName);
    }

    private static T? TryRead<T>(string path)
    {
        if (!File.Exists(path))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Options);
        }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
        catch (NotSupportedException) { return default; }
        catch (ArgumentException) { return default; }
    }

    private static void MoveWithRetry(string source, string destination)
    {
        // Endpoint security scanners can briefly reject a just-written temp file.
        // Retry only the final atomic replacement; persistent access denial is still
        // normalized by Write into the documented IOException contract.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
