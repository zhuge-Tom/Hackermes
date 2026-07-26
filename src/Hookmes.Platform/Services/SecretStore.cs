using Hookmes.Base.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hookmes.Platform.Services;

/// <summary>
/// 敏感值(API Key 等)存取。
/// <para>
/// 参考项目把 API Key 明文写在 settings.json 里,这里改用 Windows DPAPI 按当前用户加密。
/// 加密后的密文与普通配置<strong>分开存放</strong>,避免误随配置导出或提交。
/// </para>
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string? value);

    bool Contains(string key);

    void Remove(string key);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hookmes.SecretStore.v1");

    private readonly IAppLogger _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    public DpapiSecretStore(IAppLogger logger, string? filePath = null)
    {
        _logger = logger.ForCategory(nameof(DpapiSecretStore));
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hookmes",
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

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SecretJsonContext : JsonSerializerContext
{
}
