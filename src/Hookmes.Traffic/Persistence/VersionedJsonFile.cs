using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hookmes.Traffic.Persistence;

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
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, Options), new UTF8Encoding(false));
            // Never replace a known-good backup with a corrupt or incompatible primary.
            var existing = TryRead<T>(path);
            if (existing is not null && (acceptExistingForBackup is null || acceptExistingForBackup(existing)))
                File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static string DefaultPath(string fileName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetTempPath();
        return Path.Combine(root, "Hookmes", fileName);
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
}
