using Hackermes.Base.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hackermes.Platform.Services;

/// <summary>
/// 敏感值(API Key 等)存取。
/// <para>
/// 用 Windows DPAPI 按当前用户加密,不把密钥明文写进配置文件。
/// 密文与普通配置<strong>分开存放</strong>,避免误随配置导出或提交。
/// </para>
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string? value);

    bool Contains(string key);

    void Remove(string key);
}

public static class SecretStoreFactory
{
    public static ISecretStore Create(IAppLogger logger, string? filePath = null) =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretStore(logger, filePath)
            : new FileProtectedSecretStore(logger, filePath);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hackermes.SecretStore.v1");

    private readonly IAppLogger _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    public DpapiSecretStore(IAppLogger logger, string? filePath = null)
    {
        _logger = logger.ForCategory(nameof(DpapiSecretStore));
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hackermes",
            "secrets.dat");
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return EnsureLoaded().TryGetValue(key, out var value) ? value : null;
        }
    }

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return EnsureLoaded().ContainsKey(key);
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            var map = EnsureLoaded();

            if (string.IsNullOrEmpty(value))
                map.Remove(key);
            else
                map[key] = value;

            Persist(map);
        }
    }

    public void Remove(string key) => Set(key, null);

    private Dictionary<string, string> EnsureLoaded()
    {
        if (_cache is not null)
            return _cache;

        _cache = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(_filePath))
                return _cache;

            var cipher = File.ReadAllBytes(_filePath);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plain);

            var loaded = JsonSerializer.Deserialize(json, SecretJsonContext.Default.DictionaryStringString);
            if (loaded is not null)
                _cache = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // 换机器或换用户后 DPAPI 无法解密,此时只能重新录入,不应崩溃。
            _logger.Warn($"密钥库读取失败,已重置: {ex.Message}");
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return _cache;
    }

    private void Persist(Dictionary<string, string> map)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(map, SecretJsonContext.Default.DictionaryStringString);
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);

            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error("密钥库写入失败", ex);
        }
    }
}

/// <summary>
/// Linux fallback for user-local secrets. A random AES-256 key and the encrypted
/// payload are stored in separate files restricted to the current Unix user.
/// This does not claim hardware-backed protection, but avoids plaintext API keys
/// and provides a deterministic store shared by the desktop app and ToolHost.
/// </summary>
public sealed class FileProtectedSecretStore : ISecretStore
{
    private static readonly byte[] Magic = "HMSE1"u8.ToArray();
    private readonly IAppLogger _logger;
    private readonly string _filePath;
    private readonly string _keyPath;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    public FileProtectedSecretStore(IAppLogger logger, string? filePath = null)
    {
        _logger = logger.ForCategory(nameof(FileProtectedSecretStore));
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hackermes",
            "secrets.dat");
        _keyPath = _filePath + ".key";
    }

    public string? Get(string key)
    {
        lock (_gate) return EnsureLoaded().TryGetValue(key, out var value) ? value : null;
    }

    public bool Contains(string key)
    {
        lock (_gate) return EnsureLoaded().ContainsKey(key);
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            var map = EnsureLoaded();
            if (string.IsNullOrEmpty(value)) map.Remove(key);
            else map[key] = value;
            Persist(map);
        }
    }

    public void Remove(string key) => Set(key, null);

    private Dictionary<string, string> EnsureLoaded()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_filePath)) return _cache;

        try
        {
            var payload = File.ReadAllBytes(_filePath);
            if (payload.Length < Magic.Length + 12 + 16 || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new CryptographicException("Secret store format is invalid.");
            var key = LoadOrCreateKey(createIfMissing: false);
            var nonce = payload.AsSpan(Magic.Length, 12);
            var tag = payload.AsSpan(Magic.Length + 12, 16);
            var cipher = payload.AsSpan(Magic.Length + 28);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain, Magic);
            var loaded = JsonSerializer.Deserialize(plain, SecretJsonContext.Default.DictionaryStringString);
            if (loaded is not null) _cache = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.Warn($"密钥库读取失败，已重置: {ex.Message}");
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return _cache;
    }

    private void Persist(Dictionary<string, string> map)
    {
        try
        {
            EnsurePrivateDirectory();
            var key = LoadOrCreateKey(createIfMissing: true);
            var plain = JsonSerializer.SerializeToUtf8Bytes(map, SecretJsonContext.Default.DictionaryStringString);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Magic);
            var payload = new byte[Magic.Length + nonce.Length + tag.Length + cipher.Length];
            Magic.CopyTo(payload, 0);
            nonce.CopyTo(payload, Magic.Length);
            tag.CopyTo(payload, Magic.Length + nonce.Length);
            cipher.CopyTo(payload, Magic.Length + nonce.Length + tag.Length);
            var temporary = _filePath + ".tmp";
            File.WriteAllBytes(temporary, payload);
            RestrictToCurrentUser(temporary);
            File.Move(temporary, _filePath, overwrite: true);
            RestrictToCurrentUser(_filePath);
        }
        catch (Exception ex)
        {
            _logger.Error("密钥库写入失败", ex);
        }
    }

    private byte[] LoadOrCreateKey(bool createIfMissing)
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            return existing.Length == 32 ? existing : throw new CryptographicException("Secret key length is invalid.");
        }
        if (!createIfMissing) throw new FileNotFoundException("Secret key file is missing.", _keyPath);

        EnsurePrivateDirectory();
        var generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var stream = new FileStream(_keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(generated);
            stream.Flush(flushToDisk: true);
            RestrictToCurrentUser(_keyPath);
            return generated;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            return existing.Length == 32 ? existing : throw new CryptographicException("Secret key length is invalid.");
        }
    }

    private void EnsurePrivateDirectory()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SecretJsonContext : JsonSerializerContext
{
}
