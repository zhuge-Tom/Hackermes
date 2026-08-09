using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class FileProtectedSecretStoreTests
{
    [Fact]
    public void PersistsEncryptedValuesAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hackermes-secret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            var logger = new FileAppLogger(LogLevel.Error);
            new FileProtectedSecretStore(logger, path).Set("api", "sensitive-value");

            Assert.Equal("sensitive-value", new FileProtectedSecretStore(logger, path).Get("api"));
            Assert.DoesNotContain("sensitive-value", Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
            Assert.Equal(32, File.ReadAllBytes(path + ".key").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
