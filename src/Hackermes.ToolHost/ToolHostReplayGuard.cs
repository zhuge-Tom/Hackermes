using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hackermes.Base;

namespace Hackermes.ToolHost;

internal static class ToolHostReplayGuard
{
    private static readonly string PathName = AppDataPaths.Resolve("toolhost-nonces.json");

    public static void Consume(string nonce, DateTimeOffset expiresAt)
    {
        using var mutex = new System.Threading.Mutex(false, "Local\\Hackermes.ToolHost.Nonce.v1");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("ToolHost replay guard is busy.");
        try
        {
            var entries = Load();
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in entries.Where(value => value.Value <= now).Select(value => value.Key).ToArray()) entries.Remove(expired);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();
            if (!entries.TryAdd(key, expiresAt)) throw new UnauthorizedAccessException("ToolHost ticket has already been consumed.");
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            var temporary = PathName + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, PathName, overwrite: true);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static Dictionary<string, DateTimeOffset> Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(PathName)) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }
}
