using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using System;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficRuleSetTests
{
    [Theory]
    [InlineData("https://*.example.test/api/?", "https://www.example.test/api/1")]
    [InlineData("*EXAMPLE.TEST/*", "https://example.test/path")]
    public void Match_supports_case_insensitive_star_and_question_globs(string pattern, string url)
    {
        var rules = new TrafficRuleSet();
        rules.Replace([new TrafficRule("match", pattern)]);

        Assert.Equal("match", rules.Match(Message(url: url))?.Id);
    }

    [Fact]
    public void Match_honors_method_stage_enabled_and_rule_order()
    {
        var rules = new TrafficRuleSet();
        rules.Replace([
            new TrafficRule("disabled", "*", Enabled: false),
            new TrafficRule("wrong-method", "*", Method: "POST"),
            new TrafficRule("wrong-stage", "*", Stage: TrafficStage.Response),
            new TrafficRule("first-match", "*", Method: "get", Stage: TrafficStage.Request),
            new TrafficRule("later-match", "*")
        ]);

        Assert.Equal("first-match", rules.Match(Message())?.Id);
        Assert.Equal("wrong-stage", rules.Match(Message(method: "DELETE", stage: TrafficStage.Response))?.Id);

        rules.Replace([new TrafficRule("disabled-only", "*", Enabled: false)]);
        Assert.Null(rules.Match(Message()));
    }

    [Fact]
    public void Replace_takes_a_snapshot_independent_from_source_collection()
    {
        var source = new[] { new TrafficRule("one") };
        var rules = new TrafficRuleSet();

        rules.Replace(source);
        source[0] = new TrafficRule("changed");

        Assert.Equal("one", rules.Snapshot[0].Id);
    }

    [Fact]
    public void Replace_rejects_blank_duplicate_and_dual_stage_edits()
    {
        var rules = new TrafficRuleSet();

        Assert.Throws<ArgumentException>(() => rules.Replace([new TrafficRule(" ")]));
        Assert.Throws<ArgumentException>(() => rules.Replace([new TrafficRule("same"), new TrafficRule("same")]));
        Assert.Throws<ArgumentException>(() => rules.Replace([
            new TrafficRule("dual", RequestEdit: new TrafficRequestEdit(), ResponseEdit: new TrafficResponseEdit())
        ]));
    }

    private static TrafficMessage Message(
        string method = "GET",
        string url = "https://example.test/path",
        TrafficStage stage = TrafficStage.Request) => new(
            "id", "page", stage, TrafficState.Paused, method, url, [], null,
            null, null, [], null, "Document", DateTimeOffset.UtcNow);
}
