using Hookmes.Traffic.Models;
using Hookmes.Traffic.Rules;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class TrafficRuleManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hookmes-rules-tests", Guid.NewGuid().ToString("N"));
    private string RulesPath => Path.Combine(_directory, "traffic-rules.json");

    [Fact]
    public void Mutations_are_persisted_and_synchronized_with_hot_rule_set()
    {
        var hotRules = new TrafficRuleSet();
        var manager = new TrafficRuleManager(hotRules, RulesPath);
        var operations = new System.Collections.Generic.List<string>();
        manager.Changed += change => operations.Add(change.Operation);

        manager.Add(new TrafficRule("api", "*/api/*", Method: "GET"));
        manager.Add(new TrafficRule("assets", "*/assets/*"));
        manager.Move("assets", 0);
        manager.SetEnabled("api", false);
        manager.Update(manager.Get("assets")! with { Pause = true });

        Assert.True(File.Exists(RulesPath));
        Assert.Equal(["assets", "api"], manager.GetAll().Select(x => x.Id));
        Assert.True(manager.Get("assets")!.Pause);
        Assert.False(hotRules.Snapshot.Single(x => x.Id == "api").Enabled);
        Assert.Equal(["add", "add", "move", "disable", "update"], operations);

        var reloaded = new TrafficRuleManager(new TrafficRuleSet(), RulesPath);
        Assert.Equal(manager.GetAll(), reloaded.GetAll());
    }

    [Fact]
    public void Import_replace_and_merge_have_deterministic_id_semantics()
    {
        var manager = new TrafficRuleManager(new TrafficRuleSet(), RulesPath);
        manager.Add(new TrafficRule("existing", "old"));

        var imported = new TrafficRuleManager(new TrafficRuleSet(), Path.Combine(_directory, "import.json"));
        imported.Add(new TrafficRule("existing", "updated", Pause: true));
        imported.Add(new TrafficRule("new", "new"));

        manager.ImportJson(imported.ExportJson(), TrafficRuleImportMode.Merge);
        Assert.Equal(["existing", "new"], manager.GetAll().Select(x => x.Id));
        Assert.Equal("updated", manager.Get("existing")!.UrlPattern);

        imported.Remove("existing");
        manager.ImportJson(imported.ExportJson(), TrafficRuleImportMode.Replace);
        Assert.Equal(["new"], manager.GetAll().Select(x => x.Id));
    }

    [Fact]
    public void Reload_falls_back_to_backup_when_primary_is_corrupt()
    {
        var manager = new TrafficRuleManager(new TrafficRuleSet(), RulesPath);
        manager.Add(new TrafficRule("first"));
        manager.Add(new TrafficRule("second")); // creates backup containing first
        File.WriteAllText(RulesPath, "{broken");

        manager.Reload();

        Assert.Equal(["first"], manager.GetAll().Select(x => x.Id));
    }

    [Fact]
    public void Failed_validation_does_not_replace_live_or_persisted_rules()
    {
        var hotRules = new TrafficRuleSet();
        var manager = new TrafficRuleManager(hotRules, RulesPath);
        manager.Add(new TrafficRule("valid"));
        var before = File.ReadAllText(RulesPath);

        Assert.Throws<ArgumentException>(() => manager.Add(new TrafficRule("invalid", "")));

        Assert.Equal(["valid"], hotRules.Snapshot.Select(x => x.Id));
        Assert.Equal(before, File.ReadAllText(RulesPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
