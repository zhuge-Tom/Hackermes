using Hackermes.App;
using Hackermes.Automation.Packet;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class DpapiSecretStoreAcceptanceTests
{
    [Fact]
    public void Secret_is_reused_by_a_new_store_instance_for_the_same_windows_user()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "secrets.dat");
        var logger = new RecordingLogger();

        var first = new DpapiSecretStore(logger, path);
        first.Set("acceptance.secret", "stable-secret-value");

        var second = new DpapiSecretStore(logger, path);

        Assert.True(second.Contains("acceptance.secret"));
        Assert.Equal("stable-secret-value", second.Get("acceptance.secret"));
        Assert.True(File.Exists(path));
        Assert.NotEmpty(File.ReadAllBytes(path));
    }

    [Fact]
    public void Corrupt_store_recovers_to_empty_and_can_be_rewritten_for_another_instance()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "secrets.dat");
        var logger = new RecordingLogger();
        new DpapiSecretStore(logger, path).Set("acceptance.secret", "before-corruption");
        File.WriteAllBytes(path, [0x48, 0x4d, 0x00, 0xff, 0x01]);

        var recovering = new DpapiSecretStore(logger, path);
        Assert.Null(recovering.Get("acceptance.secret"));
        Assert.False(recovering.Contains("acceptance.secret"));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warn && entry.Category == nameof(DpapiSecretStore));

        recovering.Set("acceptance.secret", "after-recovery");

        var reopened = new DpapiSecretStore(logger, path);
        Assert.Equal("after-recovery", reopened.Get("acceptance.secret"));
    }

    [Fact]
    public void Audit_identity_and_trust_pin_survive_reopen_and_detect_key_rotation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "secrets.dat");
        var logger = new RecordingLogger();
        string pinnedKeyId;
        string signedBeforeRestart;

        using (var firstKey = new PacketAuditSigningKey(new DpapiSecretStore(logger, path)))
        {
            pinnedKeyId = firstKey.KeyId;
            signedBeforeRestart = new PacketAuditExportService(EmptyAudit.Instance, firstKey)
                .Export(new PacketAuditQuery(Limit: 1));
        }

        Assert.Matches("^[0-9a-f]{64}$", pinnedKeyId);

        using (var reopenedKey = new PacketAuditSigningKey(new DpapiSecretStore(logger, path)))
        {
            Assert.Equal(pinnedKeyId, reopenedKey.KeyId);
            var verifier = new PacketAuditExportService(EmptyAudit.Instance, reopenedKey);
            var verified = verifier.Verify(signedBeforeRestart, pinnedKeyId);
            Assert.True(verified.Valid);
            Assert.Equal(pinnedKeyId, verified.KeyId);
        }

        File.WriteAllBytes(path, [0xde, 0xad, 0xbe, 0xef]);

        using var rotatedKey = new PacketAuditSigningKey(new DpapiSecretStore(logger, path));
        Assert.NotEqual(pinnedKeyId, rotatedKey.KeyId);
        var rotatedService = new PacketAuditExportService(EmptyAudit.Instance, rotatedKey);
        var signedAfterRotation = rotatedService.Export(new PacketAuditQuery(Limit: 1));
        Assert.Equal("untrusted_key", rotatedService.Verify(signedAfterRotation, pinnedKeyId).ErrorCode);
        Assert.True(rotatedService.Verify(signedAfterRotation, rotatedKey.KeyId).Valid);
    }

    private sealed class EmptyAudit : IPacketAuditTrail
    {
        public static EmptyAudit Instance { get; } = new();
        public void Record(PacketAuditEntry entry) { }
        public IReadOnlyList<PacketAuditEntry> Query(PacketAuditQuery query) => [];
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<LogEntry> Entries { get; } = [];
        public void Log(LogLevel level, string category, string message, Exception? exception = null) =>
            Entries.Add(new LogEntry(level, category));
    }

    private sealed record LogEntry(LogLevel Level, string Category);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Hackermes.SecretStore.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
