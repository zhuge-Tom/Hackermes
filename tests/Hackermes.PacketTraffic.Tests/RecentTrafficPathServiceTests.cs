using Hackermes.App;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using Hackermes.Platform.Serialization;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class RecentTrafficPathServiceTests
{
    [Fact]
    public void Adapter_normalizes_and_persists_archive_and_rules_paths_separately()
    {
        var settings = new SettingsFake();
        var service = new RecentTrafficPathService(settings);

        service.RememberArchivePath(" captures/session.har ");
        service.RememberRulesPath(" rules/team.json ");

        Assert.Equal(Path.GetFullPath("captures/session.har"), settings.Value.Traffic.LastArchivePath);
        Assert.Equal(Path.GetFullPath("rules/team.json"), settings.Value.Traffic.LastRulesPath);
        Assert.Equal(settings.Value.Traffic.LastArchivePath, service.LastArchivePath);
        Assert.Equal(settings.Value.Traffic.LastRulesPath, service.LastRulesPath);
    }

    [Fact]
    public void Adapter_rejects_empty_path_instead_of_persisting_non_path_data()
    {
        var settings = new SettingsFake();
        var service = new RecentTrafficPathService(settings);

        Assert.Throws<ArgumentException>(() => service.RememberArchivePath("   "));
        Assert.Null(settings.Value.Traffic.LastArchivePath);
    }

    [Fact]
    public void Source_generated_settings_contract_includes_only_recent_traffic_paths()
    {
        var value = new AppSettings
        {
            Traffic = new TrafficSettings { LastArchivePath = "archive.har", LastRulesPath = "rules.json" }
        };

        var json = JsonSerializer.Serialize(value, AppSettingsJsonContext.Default.AppSettings);

        Assert.Contains("\"traffic\"", json);
        Assert.Contains("\"lastArchivePath\"", json);
        Assert.Contains("\"lastRulesPath\"", json);
        Assert.DoesNotContain("packetContent", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rulesContent", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SettingsFake : ISettingsService
    {
        public AppSettings Value { get; } = new();
        public AppSettings Load() => Value;
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null)
        {
            mutate(Value);
            return true;
        }
        public string SettingsFilePath => "settings.json";
    }
}
