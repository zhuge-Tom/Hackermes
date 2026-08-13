using Hackermes.Base;
using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Browser.Services;
using Hackermes.Platform.Services;
using Hackermes.Traffic.Services;
using System;
using System.IO;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

[CollectionDefinition("App data environment serial", DisableParallelization = true)]
public sealed class AppDataEnvironmentSerialCollection;

[Collection("App data environment serial")]
public sealed class AppDataIsolationTests
{
    [Fact]
    public void Explicit_data_root_is_shared_by_settings_logs_traffic_and_browser_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-data-isolation-" + Guid.NewGuid().ToString("N"));
        var previousData = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        var previousProfile = Environment.GetEnvironmentVariable(BrowserProxyConfiguration.ProfileRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable, root);
            Environment.SetEnvironmentVariable(BrowserProxyConfiguration.ProfileRootEnvironmentVariable, null);

            var settings = new SettingsService(new EventBus(), new NullLogger());
            using var history = new TrafficHistoryPersistence();
            var logger = new FileAppLogger();
            logger.Log(LogLevel.Info, "isolation-test", "bounded evidence");
            var browser = BrowserProxyConfiguration.Create(BrowserProxyMode.Direct);

            Assert.Equal(Path.Combine(root, "settings.json"), settings.SettingsFilePath);
            Assert.Equal(Path.Combine(root, "traffic-history.v1.json.gz"), history.FilePath);
            Assert.Equal(Path.Combine(root, "Browser", "WebView2", "Direct"), browser.UserDataFolder);
            Assert.Contains("bounded evidence", File.ReadAllText(Path.Combine(root, "logs", "latest.log")), StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => AppDataPaths.Resolve("..", "outside"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable, previousData);
            Environment.SetEnvironmentVariable(BrowserProxyConfiguration.ProfileRootEnvironmentVariable, previousProfile);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData("relative-data-root")]
    [InlineData("G:\\")]
    public void Explicit_data_root_rejects_relative_or_drive_root(string value)
    {
        var previous = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable, value);
            Assert.Throws<InvalidOperationException>(() => _ = AppDataPaths.Root);
        }
        finally { Environment.SetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable, previous); }
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
