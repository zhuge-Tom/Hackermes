using Hackermes.App;
using Hackermes.Inspector.ViewModels;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class TrafficRuleComplexFormTests
{
    private const string HeaderLines = "X-Env: staging\r\nHost: override.test";

    // ---- mapper: draft -> rule -------------------------------------------------

    [Fact]
    public void Edit_behavior_maps_every_request_field()
    {
        var rule = TrafficRuleDraftMapper.BuildRule(new TrafficRuleDraft(
            "rewrite", "*://api.test/*", "POST", "request", "edit",
            RequestUrl: "https://mirror.test/api", RequestMethod: "PUT",
            RequestHeaders: [new("X-Env", "staging")], RequestBody: "new body"));

        Assert.False(rule.Pause);
        Assert.False(rule.Fail);
        Assert.NotNull(rule.RequestEdit);
        Assert.Null(rule.ResponseEdit);
        Assert.Equal("https://mirror.test/api", rule.RequestEdit!.Url);
        Assert.Equal("PUT", rule.RequestEdit.Method);
        Assert.Equal([new TrafficHeader("X-Env", "staging")], rule.RequestEdit.Headers);
        Assert.Equal("new body", Encoding.UTF8.GetString(rule.RequestEdit.Body!));
    }

    [Fact]
    public void Edit_behavior_requires_at_least_one_change()
    {
        Assert.Throws<ArgumentException>(() => TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("empty-edit", "*", null, "request", "edit")));
    }

    [Fact]
    public void Fulfill_behavior_defaults_status_and_maps_response_fields()
    {
        var rule = TrafficRuleDraftMapper.BuildRule(new TrafficRuleDraft(
            "mock", "*://api.test/health", null, "response", "fulfill",
            ResponseStatusText: "OK", ResponseHeaders: [new("Content-Type", "application/json")],
            ResponseBody: "{\"ok\":true}"));

        Assert.NotNull(rule.ResponseEdit);
        Assert.Null(rule.RequestEdit);
        Assert.Equal(200, rule.ResponseEdit!.Status);
        Assert.Equal("OK", rule.ResponseEdit.StatusText);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(rule.ResponseEdit.Body!));
    }

    [Fact]
    public void Fulfill_rejects_out_of_range_status()
    {
        Assert.Throws<ArgumentException>(() => TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("bad-status", "*", null, "response", "fulfill", ResponseStatus: 99)));
        Assert.Throws<ArgumentException>(() => TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("bad-status", "*", null, "response", "fulfill", ResponseStatus: 1000)));
    }

    [Fact]
    public void Pause_and_drop_behaviors_keep_their_previous_shape()
    {
        var pause = TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("p", "*", "*", "request", "pause"));
        Assert.True(pause.Pause);
        Assert.False(pause.Fail);

        var drop = TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("d", "*", null, "any", "drop"));
        Assert.True(drop.Fail);
        Assert.Null(drop.Stage);

        Assert.Throws<ArgumentException>(() => TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("x", "*", null, "request", "redirect")));
        Assert.Throws<ArgumentException>(() => TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("x", "*", null, "websocket", "pause")));
    }

    [Fact]
    public void Oversized_body_is_rejected_before_persistence()
    {
        Assert.Throws<ArgumentException>(() => TrafficRuleDraftMapper.BuildRule(
            new TrafficRuleDraft("big", "*", null, "request", "edit",
                RequestBody: new string('b', TrafficRuleDraftMapper.MaximumBodyBytes + 1))));
    }

    // ---- mapper: rule -> draft round trip --------------------------------------

    [Fact]
    public void Fulfill_draft_round_trips_through_rule()
    {
        var original = new TrafficRuleDraft(
            "round-trip", "*://api.test/*", "GET", "response", "fulfill",
            ResponseStatus: 418, ResponseStatusText: "Teapot",
            ResponseHeaders: [new("Content-Type", "text/x-roundtrip")], ResponseBody: "body");

        var rebuilt = TrafficRuleDraftMapper.ToDraft(TrafficRuleDraftMapper.BuildRule(original));

        Assert.Equal(original.Id, rebuilt.Id);
        Assert.Equal(original.UrlPattern, rebuilt.UrlPattern);
        Assert.Equal(original.Method, rebuilt.Method);
        Assert.Equal(original.Stage, rebuilt.Stage);
        Assert.Equal(original.Behavior, rebuilt.Behavior);
        Assert.Equal(original.ResponseStatus, rebuilt.ResponseStatus);
        Assert.Equal(original.ResponseBody, rebuilt.ResponseBody);
        Assert.Equal(original.ResponseHeaders, rebuilt.ResponseHeaders);
    }

    [Fact]
    public void Edit_draft_round_trips_through_rule()
    {
        var original = new TrafficRuleDraft(
            "round-edit", "*", "POST", "request", "edit",
            RequestUrl: "https://mirror.test/", RequestMethod: "PUT",
            RequestHeaders: [new("X-Env", "staging")], RequestBody: "payload");

        var rebuilt = TrafficRuleDraftMapper.ToDraft(TrafficRuleDraftMapper.BuildRule(original));

        Assert.Equal("edit", rebuilt.Behavior);
        Assert.Equal(original.RequestUrl, rebuilt.RequestUrl);
        Assert.Equal(original.RequestMethod, rebuilt.RequestMethod);
        Assert.Equal(original.RequestHeaders, rebuilt.RequestHeaders);
        Assert.Equal(original.RequestBody, rebuilt.RequestBody);
    }

    [Fact]
    public void Binary_bodies_are_hidden_from_the_form_but_never_corrupted()
    {
        var binary = new byte[] { 0x00, 0xFF, 0xFE };
        var rule = new TrafficRule("binary", "*", null, TrafficStage.Request,
            RequestEdit: new TrafficRequestEdit(Body: binary));

        var draft = TrafficRuleDraftMapper.ToDraft(rule);

        Assert.Null(draft.RequestBody);
        Assert.Same(binary, rule.RequestEdit!.Body);
    }

    [Fact]
    public void Complex_rules_pass_rule_set_validation_and_manager_persistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hackermes-rule-form-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rulesFile = Path.Combine(directory, "rules.json");
            new TrafficRuleManager(new TrafficRuleSet(), rulesFile).Add(TrafficRuleDraftMapper.BuildRule(
                new TrafficRuleDraft("complex", "*", null, "response", "fulfill",
                    ResponseStatus: 599, ResponseBody: "nope")));

            var reloaded = new TrafficRuleManager(new TrafficRuleSet(), rulesFile);
            var draft = TrafficRuleDraftMapper.ToDraft(reloaded.Get("complex")!);

            Assert.Equal("fulfill", draft.Behavior);
            Assert.Equal(599, draft.ResponseStatus);
            Assert.Equal("nope", draft.ResponseBody);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    // ---- view-model form behaviour ----------------------------------------------

    [Fact]
    public async Task Add_with_edit_behavior_parses_header_lines_into_the_draft()
    {
        var service = new CapturingService();
        var model = new TrafficRulesViewModel(service)
        {
            Id = "rewrite", UrlPattern = "*://api.test/*", Stage = "request", Behavior = "edit",
            RequestUrl = "https://mirror.test/", RequestHeaderText = HeaderLines
        };

        await model.AddCommand.ExecuteAsync(null);

        Assert.NotNull(service.Added);
        Assert.Equal("https://mirror.test/", service.Added.RequestUrl);
        Assert.Equal([new TrafficRuleHeaderEdit("X-Env", "staging"), new TrafficRuleHeaderEdit("Host", "override.test")],
            service.Added.RequestHeaders);
        Assert.Null(service.Added.ResponseBody);
        Assert.Contains("'rewrite' added.", model.Status);
    }

    [Fact]
    public async Task Malformed_form_input_surfaces_an_error_without_touching_the_service()
    {
        var service = new CapturingService();
        var model = new TrafficRulesViewModel(service)
        {
            Id = "broken", Behavior = "edit", RequestHeaderText = "no-colon-here"
        };

        await model.AddCommand.ExecuteAsync(null);

        Assert.Null(service.Added);
        Assert.Contains("Header line 1", model.Status);
        Assert.Contains("Name: value", model.Status);
    }

    [Fact]
    public async Task Load_then_save_round_trips_a_complex_rule()
    {
        var service = new CapturingService();
        service.Stored["mock"] = TrafficRuleDraftMapper.BuildRule(new TrafficRuleDraft(
            "mock", "*://api.test/*", null, "response", "fulfill",
            ResponseStatus: 418, ResponseHeaders: [new("X-Mock", "yes")], ResponseBody: "teapot"));
        service.RulesList = [new TrafficRuleItem("mock", "*://api.test/*", "*", "response", "fulfill", true)];
        var model = new TrafficRulesViewModel(service);

        model.Selected = model.Rules.Single();
        await model.LoadSelectedCommand.ExecuteAsync(null);

        Assert.Equal("mock", model.LoadedRuleId);
        Assert.Equal("fulfill", model.Behavior);
        Assert.Equal("418", model.ResponseStatus);
        Assert.Contains("X-Mock: yes", model.ResponseHeaderText);
        Assert.True(model.HasLoadedRule);

        model.ResponseStatus = "503";
        model.ResponseHeaderText = "X-Mock: changed";
        await model.SaveChangesCommand.ExecuteAsync(null);

        Assert.NotNull(service.Updated);
        Assert.Equal("mock", service.Updated.Id);
        Assert.Equal(503, service.Updated.ResponseStatus);
        Assert.Equal([new TrafficRuleHeaderEdit("X-Mock", "changed")], service.Updated.ResponseHeaders);
        Assert.Contains("'mock' updated.", model.Status);
    }

    [Fact]
    public async Task Save_changes_requires_a_loaded_rule()
    {
        var service = new CapturingService();
        var model = new TrafficRulesViewModel(service);

        Assert.False(model.HasLoadedRule);
        Assert.False(model.SaveChangesCommand.CanExecute(null));

        model.Id = "never-loaded";
        model.Behavior = "fulfill";
        await model.SaveChangesCommand.ExecuteAsync(null);

        Assert.Null(service.Updated);
        Assert.NotEmpty(model.Status); // the failure is reported, not thrown
    }

    private sealed class CapturingService : ITrafficRuleWorkbenchService
    {
        public Dictionary<string, TrafficRule> Stored { get; } = new(StringComparer.Ordinal);
        public TrafficRuleDraft? Added { get; private set; }
        public TrafficRuleDraft? Updated { get; private set; }
        public IReadOnlyList<TrafficRuleItem> RulesList { get; set; } = [];
        public IReadOnlyList<TrafficRuleItem> Rules => RulesList;
#pragma warning disable CS0067
        public event Action? RulesChanged;
#pragma warning restore CS0067

        public Task AddRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken)
        {
            var rule = TrafficRuleDraftMapper.BuildRule(draft);
            Stored.Add(rule.Id, rule);
            Added = draft;
            return Task.CompletedTask;
        }

        public Task UpdateRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken)
        {
            var rule = TrafficRuleDraftMapper.BuildRule(draft);
            if (!Stored.ContainsKey(rule.Id))
                throw new KeyNotFoundException($"Traffic rule '{rule.Id}' was not found.");
            Stored[rule.Id] = rule;
            Updated = draft;
            return Task.CompletedTask;
        }

        public Task<TrafficRuleDraft?> GetRuleAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.TryGetValue(id, out var rule) ? TrafficRuleDraftMapper.ToDraft(rule) : null);

        public Task SetRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveRuleAsync(string id, CancellationToken cancellationToken) { Stored.Remove(id); return Task.CompletedTask; }
        public Task MoveRuleAsync(string id, int targetIndex, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExportRulesFileAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> ImportRulesFileAsync(string path, bool merge, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
