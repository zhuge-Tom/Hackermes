using Hackermes.App;
using Hackermes.Assessment;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

[Collection("ToolHost serial")]
public sealed class CloudCredentialTests
{
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
        public void Set(string key, string? value) { if (value is null) Values.Remove(key); else Values[key] = value; }
        public bool Contains(string key) => Values.ContainsKey(key);
        public void Remove(string key) => Values.Remove(key);
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }

    private sealed class TestSettings(string path) : ISettingsService
    {
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null) => true;
        public string SettingsFilePath => path;
    }

    [Fact]
    public void StageMintsOpaqueTokenAndStoresDpapiKeys()
    {
        var secrets = new MemorySecrets();
        var vault = new CloudCredentialVault(secrets);

        var staged = vault.Stage("alibaba", "LTAI5tFakeAccessKey123", "FakeSecretKey1234567890abcd/==", null);

        Assert.Matches("^cc-[0-9a-f]{16}$", staged.CredentialToken);
        Assert.Equal("alibaba", staged.Provider);
        Assert.True(staged.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(55));
        // The raw key material only ever lands in the DPAPI secret store, keyed by the token.
        Assert.Equal("LTAI5tFakeAccessKey123", secrets.Values[$"cloudcred.{staged.CredentialToken}.ak"]);
        Assert.DoesNotContain(secrets.Values.Keys, key => key.Contains("LTAI5tFakeAccessKey123"));
        Assert.DoesNotContain(staged.CredentialToken, secrets.Values[$"cloudcred.{staged.CredentialToken}.ak"]);
    }

    [Fact]
    public void StageRejectsMalformedKeysAndProviders()
    {
        var vault = new CloudCredentialVault(new MemorySecrets());
        Assert.Throws<ArgumentException>(() => vault.Stage("gcp", "LTAI5tFakeAccessKey123", "FakeSecretKey1234567890abcd", null));
        Assert.Throws<ArgumentException>(() => vault.Stage("alibaba", "short", "FakeSecretKey1234567890abcd", null));
        Assert.Throws<ArgumentException>(() => vault.Stage("alibaba", "LTAI5tFakeAccessKey123", "x", null));
        Assert.Throws<ArgumentException>(() => vault.Stage("alibaba", "LTAI5tFakeAccessKey123;rm", "FakeSecretKey1234567890abcd", null));
    }

    [Fact]
    public void ClearRemovesAllKeyMaterial()
    {
        var secrets = new MemorySecrets();
        var vault = new CloudCredentialVault(secrets);
        var staged = vault.Stage("aws", "AKIAFakeAccessKey1234", "FakeSecretKey1234567890abcd/==", "session-token-1");

        Assert.True(vault.Clear(staged.CredentialToken));
        Assert.DoesNotContain(secrets.Values.Keys, key => key.StartsWith($"cloudcred.{staged.CredentialToken}", StringComparison.Ordinal));
        Assert.False(vault.Clear(staged.CredentialToken));
    }

    [Fact]
    public void CloudCredentialEnvironmentMapsProviderVariants()
    {
        var alibaba = AuthorizedToolCatalog.CloudCredentialEnvironment("alibaba", "AK1", "SK1", null);
        Assert.Equal("AK1", alibaba["ALIBABA_CLOUD_ACCESS_KEY_ID"]);
        Assert.Equal("SK1", alibaba["ALIBABA_CLOUD_ACCESS_KEY_SECRET"]);
        Assert.Equal("AK1", alibaba["ALIBABACLOUD_ACCESS_KEY_ID"]);

        var aws = AuthorizedToolCatalog.CloudCredentialEnvironment("aws", "AK2", "SK2", "STS");
        Assert.Equal("STS", aws["AWS_SESSION_TOKEN"]);
        Assert.Equal("AK2", aws["AWS_ACCESS_KEY_ID"]);

        var tencent = AuthorizedToolCatalog.CloudCredentialEnvironment("tencent", "AK3", "SK3", null);
        Assert.Equal("AK3", tencent["TENCENTCLOUD_SECRET_ID"]);

        Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.CloudCredentialEnvironment("gcp", "AK", "SK", null));
    }

    [Fact]
    public void BuildInvocationCarriesOnlyTheTokenAndNeverKeyMaterial()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-cloudcred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cf = Path.Combine(root, "cf.exe");
        File.WriteAllText(cf, string.Empty);
        var oldCf = Environment.GetEnvironmentVariable("HACKERMES_CF_PATH");
        try
        {
            Environment.SetEnvironmentVariable("HACKERMES_CF_PATH", cf);
            var token = "cc-" + new string('a', 16);
            var invocation = AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ProbeCloudAkskVerify,
                    $"{{\"target\":\"127.0.0.1\",\"provider\":\"alibaba\",\"credentialToken\":\"{token}\",\"command\":\"ls\"}}"),
                ["127.0.0.1"]);

            Assert.Equal(cf, invocation.ExecutablePath);
            Assert.Equal([cf, "alibaba", "ls"], invocation.Arguments);
            Assert.Equal($"cloudcred.{token}", invocation.SecretReference);
            Assert.Null(invocation.EnvironmentVariables);
            // The normalized plan input carries the opaque token only.
            Assert.DoesNotContain("LTAI", invocation.Arguments[0] + invocation.Arguments[1] + invocation.Arguments[2]);
            Assert.Equal(
                $"{{\"target\":\"127.0.0.1\",\"scheme\":\"http\",\"port\":80,\"provider\":\"alibaba\",\"command\":\"ls\",\"credentialToken\":\"{token}\"}}",
                AuthorizedToolCatalog.NormalizeStep(
                    new AssessmentStep(AuthorizedToolCatalog.ProbeCloudAkskVerify,
                        $"{{\"target\":\"127.0.0.1\",\"provider\":\"alibaba\",\"credentialToken\":\"{token}\",\"command\":\"ls\"}}"),
                    ["127.0.0.1"]).Input);

            Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ProbeCloudAkskVerify,
                    $"{{\"target\":\"127.0.0.1\",\"provider\":\"alibaba\",\"credentialToken\":\"LTAI5tFakeAccessKey123\",\"command\":\"ls\"}}"),
                ["127.0.0.1"]));
            Assert.Throws<ArgumentException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ProbeCloudAkskVerify,
                    $"{{\"target\":\"127.0.0.1\",\"provider\":\"alibaba\",\"credentialToken\":\"{token}\",\"command\":\"console\"}}"),
                ["127.0.0.1"]));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorizedToolCatalog.BuildInvocation(
                new AssessmentStep(AuthorizedToolCatalog.ProbeCloudAkskVerify,
                    $"{{\"target\":\"example.com\",\"provider\":\"alibaba\",\"credentialToken\":\"{token}\",\"command\":\"ls\"}}"),
                ["127.0.0.1"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKERMES_CF_PATH", oldCf);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CloudVerifyParserEmitsMediumCandidateOnPositiveOutput()
    {
        var positive = ReconObservationParser.Parse(AuthorizedToolCatalog.ProbeCloudAkskVerify,
            "[+] OSS buckets: 3\n[+] ECS instances: 1\n");
        var observation = Assert.Single(positive);
        Assert.Equal("cloud-credential-indicator", observation.Code);
        Assert.Equal("Medium", observation.Severity);

        Assert.Empty(ReconObservationParser.Parse(AuthorizedToolCatalog.ProbeCloudAkskVerify,
            "[-] 凭证无效\n"));
    }
}
