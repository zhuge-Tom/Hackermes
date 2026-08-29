using Hackermes.AiPanel.Agent;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AgentCapabilityPolishTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hm-polish-").FullName;

    [Fact]
    public void Catalog_groups_tools_by_domain()
    {
        var tools = new[]
        {
            Dummy("page_context"), Dummy("packet_query"), Dummy("assessment_tools"), Dummy("todo_write")
        };
        var catalog = AiToolCatalog.Format(tools);
        Assert.Contains("browser: page_context", catalog, StringComparison.Ordinal);
        Assert.Contains("traffic: packet_query", catalog, StringComparison.Ordinal);
        Assert.Contains("assessment: assessment_tools", catalog, StringComparison.Ordinal);
        Assert.Contains("agent: todo_write", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_includes_catalog_when_tools_are_supplied()
    {
        var system = AgentContextCompactor.BuildSystemMessage(
            new AgentMemoryDocument(), [], new AiSettings { MaxContextCharacters = 24_000 },
            [Dummy("page_context"), Dummy("packet_analyze")]);
        Assert.Contains("Tool catalog", system, StringComparison.Ordinal);
        Assert.Contains("page_context", system, StringComparison.Ordinal);
        Assert.Contains("packet_analyze", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_skill_store_seeds_the_authorized_assessment_playbook()
    {
        var store = new AgentSkillStore(new PathSettings(Path.Combine(_root, "settings.json")), new NullLogger());
        var skill = Assert.Single(store.Snapshot());
        Assert.Equal(AgentSkillStore.DefaultAssessmentSkillId, skill.Id);
        Assert.True(skill.Enabled);
        Assert.Contains("page_security_snapshot", skill.ToolNames);
        Assert.Contains("page_eval_read", skill.ToolNames);
        Assert.Contains("page_navigate", skill.ToolNames);
        Assert.True(File.Exists(Path.Combine(_root, "agent-skills.json")));
    }

    [Fact]
    public void Existing_skill_file_is_not_reseeded()
    {
        File.WriteAllText(Path.Combine(_root, "agent-skills.json"), """{"version":1,"skills":[]}""");
        var store = new AgentSkillStore(new PathSettings(Path.Combine(_root, "settings.json")), new NullLogger());
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Stock_assessment_skill_gains_page_navigate_on_load()
    {
        File.WriteAllText(Path.Combine(_root, "agent-skills.json"),
            """{"Version":1,"Skills":[{"Id":"authorized-assessment","Name":"授权评估","Enabled":true,"Instructions":"Authorized assessment playbook. Observe first.","ToolNames":["page_context"]}]}""");
        var store = new AgentSkillStore(new PathSettings(Path.Combine(_root, "settings.json")), new NullLogger());
        var skill = Assert.Single(store.Snapshot());
        Assert.Contains("page_navigate", skill.ToolNames);
        Assert.Contains("page_context", skill.ToolNames);
        Assert.Contains("creates a tab", skill.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestRanges_prefers_low_value_dumps_over_evidence_tools()
    {
        var store = new AcpContextStore(() => "sys", 80_000);
        store.AppendAssistantToolCalls(null, [new AssistantToolCall("c1", "console_read", "{}")]);
        store.AppendToolResult("c1", new string('c', 6_000), "console_read");
        store.AppendUser("mid");
        store.AppendAssistantToolCalls(null, [new AssistantToolCall("c2", "packet_analyze", "{}")]);
        store.AppendToolResult("c2", new string('p', 6_000), "packet_analyze");
        for (var i = 0; i < 6; i++) store.AppendAssistant("recent-" + i);

        var first = Assert.Single(store.SuggestRanges(store.ActiveEntries).Take(1));
        Assert.Equal("console_read", ToolNameInRange(store, first));
    }

    [Fact]
    public void Pressure_includes_system_and_tool_schema_overhead()
    {
        var store = new AcpContextStore(() => "sys", 10_000);
        store.AppendUser("hello");
        var tools = new[] { Dummy("packet_query") };
        var overhead = store.EstimateOverhead("stable-system", tools);
        Assert.True(overhead > 0);
        Assert.Equal(store.ActiveChars + overhead, store.PressureChars("stable-system", tools));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string? ToolNameInRange(AcpContextStore store, AcpRangeSuggestion range)
    {
        var entries = store.ActiveEntries;
        var start = entries.ToList().FindIndex(entry => entry.Ref == range.StartRef);
        var end = entries.ToList().FindIndex(entry => entry.Ref == range.EndRef);
        return entries.Skip(start).Take(end - start + 1).Select(entry => entry.ToolName)
            .FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static AiToolDefinition Dummy(string name) => new(
        name, name + " desc", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }),
        AiToolRisk.ReadOnly, (_, _) => ValueTask.FromResult(ToolResult.Ok()));

    private sealed class PathSettings(string path) : ISettingsService
    {
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null) => true;
        public string SettingsFilePath => path;
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
