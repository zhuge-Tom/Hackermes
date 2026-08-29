using Hackermes.AiPanel.Tools;
using Hackermes.Base;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hackermes.App;

public sealed record CloudCredentialStaged(string CredentialToken, string Provider, DateTimeOffset ExpiresAt);

/// <summary>
/// DPAPI-backed staging vault for cloud access keys discovered during an assessment.
/// Staging mints an opaque cc- token; the keys themselves never enter the plan, the
/// ticket, the evidence or any log — only the token does, and it expires.
/// </summary>
public sealed class CloudCredentialVault
{
    public static readonly Regex TokenPattern = new("^cc-[0-9a-f]{16}$", RegexOptions.Compiled);
    private const int MaxLifetimeMinutes = 60;
    private const string IndexKey = "cloudcred.index";

    private readonly ISecretStore _secrets;

    public CloudCredentialVault(ISecretStore secrets) => _secrets = secrets;

    public CloudCredentialStaged Stage(string provider, string accessKey, string secretKey, string? sessionToken)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider is not ("alibaba" or "aws" or "tencent" or "huawei"))
            throw new ArgumentException("provider must be alibaba, aws, tencent or huawei.");
        accessKey = (accessKey ?? string.Empty).Trim();
        secretKey = (secretKey ?? string.Empty).Trim();
        sessionToken = string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken!.Trim();
        if (!Regex.IsMatch(accessKey, "^[A-Za-z0-9]{16,64}$"))
            throw new ArgumentException("accessKey must be 16-64 letters/digits.");
        if (!Regex.IsMatch(secretKey, "^[A-Za-z0-9/+=]{24,128}$"))
            throw new ArgumentException("secretKey must be 24-128 base64-ish characters.");
        if (sessionToken is { Length: > 1024 })
            throw new ArgumentException("sessionToken must be at most 1024 characters.");

        CleanupExpired();
        var token = "cc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddMinutes(MaxLifetimeMinutes);
        _secrets.Set($"cloudcred.{token}.provider", provider);
        _secrets.Set($"cloudcred.{token}.ak", accessKey);
        _secrets.Set($"cloudcred.{token}.sk", secretKey);
        if (sessionToken is not null)
            _secrets.Set($"cloudcred.{token}.st", sessionToken);
        _secrets.Set($"cloudcred.{token}.expires", expires.ToString("O", CultureInfo.InvariantCulture));
        AppendIndex(token);
        return new CloudCredentialStaged(token, provider, expires);
    }

    public bool Clear(string credentialToken)
    {
        credentialToken = (credentialToken ?? string.Empty).Trim();
        if (!TokenPattern.IsMatch(credentialToken)) return false;
        var removed = false;
        foreach (var suffix in new[] { ".provider", ".ak", ".sk", ".st", ".expires" })
            if (_secrets.Get($"cloudcred.{credentialToken}{suffix}") is not null)
            {
                _secrets.Set($"cloudcred.{credentialToken}{suffix}", null);
                removed = true;
            }
        RemoveFromIndex(credentialToken);
        return removed;
    }

    /// <summary>Removes expired staged credentials (best effort; the index tracks staged tokens).</summary>
    private void CleanupExpired()
    {
        foreach (var token in ReadIndex())
        {
            var raw = _secrets.Get($"cloudcred.{token}.expires");
            if (raw is null)
            {
                RemoveFromIndex(token);
                continue;
            }
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires) &&
                expires <= DateTimeOffset.UtcNow)
            {
                Clear(token);
            }
        }
    }

    private string[] ReadIndex()
    {
        var raw = _secrets.Get(IndexKey);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void AppendIndex(string token)
    {
        var index = ReadIndex();
        var updated = new string[index.Length + 1];
        updated[0] = token;
        index.CopyTo(updated, 1);
        _secrets.Set(IndexKey, JsonSerializer.Serialize(updated));
    }

    private void RemoveFromIndex(string token)
    {
        var index = ReadIndex();
        var updated = System.Linq.Enumerable.Where(index, value => value != token).ToArray();
        _secrets.Set(IndexKey, JsonSerializer.Serialize(updated));
    }
}

/// <summary>Registers cloud_credential_stage / cloud_credential_clear for the agent.</summary>
public sealed class CloudCredentialModule : IModule
{
    public string Name => "Cloud Credential Vault";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<CloudCredentialVault>(serviceProvider =>
            new CloudCredentialVault(serviceProvider.GetRequiredService<ISecretStore>()));
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var registry = serviceProvider.GetRequiredService<IAiToolRegistry>();
        var vault = serviceProvider.GetRequiredService<CloudCredentialVault>();

        registry.Register(new AiToolDefinition(
            "cloud_credential_stage",
            "Stage a cloud access key discovered during the authorized assessment (from heapdump/git-leak evidence) " +
            "for read-only verification with probe.cloud_aksk.verify. Keys are stored DPAPI-encrypted for at most " +
            "60 minutes; only an opaque cc- token is returned and only the token may appear in plans or evidence. " +
            "Never paste the raw key anywhere else. Read-only verification only (ls/perm) — console takeover and " +
            "resource mutation stay manual operator work.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    provider = new { type = "string", @enum = new[] { "alibaba", "aws", "tencent", "huawei" } },
                    accessKey = new { type = "string", description = "access key id, 16-64 letters/digits" },
                    secretKey = new { type = "string", description = "secret key, 24-128 base64-ish characters" },
                    sessionToken = new { type = "string", description = "optional STS session token" }
                },
                required = new[] { "provider", "accessKey", "secretKey" },
                additionalProperties = false
            }),
            AiToolRisk.Dangerous,
            (invocation, _) => ValueTask.FromResult(Try(() =>
            {
                var staged = vault.Stage(
                    Text(invocation.Arguments, "provider"),
                    Text(invocation.Arguments, "accessKey"),
                    Text(invocation.Arguments, "secretKey"),
                    string.IsNullOrWhiteSpace(Text(invocation.Arguments, "sessionToken"))
                        ? null : Text(invocation.Arguments, "sessionToken"));
                return JsonSerializer.Serialize(new
                {
                    staged.CredentialToken, staged.Provider, staged.ExpiresAt,
                    usage = "pass credentialToken into probe.cloud_aksk.verify (command=ls|perm); " +
                            "the raw keys never appear in plans, evidence or logs"
                });
            }))));

        registry.Register(new AiToolDefinition(
            "cloud_credential_clear",
            "Clear one staged cloud credential immediately (also happens automatically on expiry).",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { credentialToken = new { type = "string" } },
                required = new[] { "credentialToken" },
                additionalProperties = false
            }),
            AiToolRisk.Mutating,
            (invocation, _) => ValueTask.FromResult(
                vault.Clear(Text(invocation.Arguments, "credentialToken"))
                    ? ToolResult.Ok("Staged credential cleared.")
                    : ToolResult.Fail("Staged credential not found (maybe already expired)."))));
    }

    private static ToolResult Try(Func<object> value)
    {
        try { return ToolResult.Ok(JsonSerializer.Serialize(value())); }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;
}
