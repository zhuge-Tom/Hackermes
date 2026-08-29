using Hackermes.AiPanel.Tools;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Agent;

/// <summary>Agent-facing, policy-gated configuration surface for persistent skills, memory and artifact retrieval.</summary>
public sealed class AgentWorkflowToolAdapter
{
    private readonly IAgentSkillStore _skills;
    private readonly IAgentMemoryStore _memory;
    private readonly IAgentArtifactStore _artifacts;

    public AgentWorkflowToolAdapter(IAgentSkillStore skills, IAgentMemoryStore memory, IAgentArtifactStore artifacts)
    {
        _skills = skills;
        _memory = memory;
        _artifacts = artifacts;
    }

    public void RegisterAll(IAiToolRegistry registry)
    {
        registry.Register(new AiToolDefinition("agent_skill_list", "List persistent Agent workflow skills.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(_skills.Snapshot())))));
        registry.Register(new AiToolDefinition("agent_skill_upsert", "Create or update a persistent workflow skill. This only narrows available tools; it cannot bypass policy.",
            Schema(new
            {
                id = new { type = "string", description = "optional stable id" }, name = new { type = "string", description = "skill name" },
                instructions = new { type = "string", description = "workflow instructions" }, enabled = new { type = "boolean" },
                toolNames = new { type = "array", items = new { type = "string" }, description = "optional allowed tool names" }
            }), AiToolRisk.Mutating,
            (invocation, _) => ValueTask.FromResult(UpsertSkill(invocation.Arguments))));
        registry.Register(new AiToolDefinition("agent_skill_remove", "Remove a persistent workflow skill by id.", Schema(new { id = new { type = "string", description = "skill id" } }), AiToolRisk.Mutating,
            (invocation, _) => ValueTask.FromResult(RemoveSkill(invocation.Arguments))));
        registry.Register(new AiToolDefinition("agent_memory_read", "Read the redacted, persistent working-memory summary and operator notes.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(_memory.Load())))));
        registry.Register(new AiToolDefinition("agent_memory_write", "Set concise persistent operator memory. Never write credentials or raw secrets.", Schema(new { notes = new { type = "string", description = "notes" } }), AiToolRisk.Mutating,
            (invocation, _) => ValueTask.FromResult(SetNotes(invocation.Arguments))));
        registry.Register(new AiToolDefinition("agent_memory_clear", "Clear persisted Agent conversation summary and operator notes.", Schema(new { }), AiToolRisk.Mutating,
            (_, _) => { _memory.Clear(); return ValueTask.FromResult(ToolResult.Ok("Persistent Agent memory cleared.")); }));
        registry.Register(new AiToolDefinition("agent_download_artifact", "Download an approved HTTPS tool artifact into Hackermes-owned storage. The artifact is not executed.",
            Schema(new
            {
                url = new { type = "string", description = "HTTPS URL" }, fileName = new { type = "string", description = "optional safe filename" },
                sha256 = new { type = "string", description = "optional expected SHA-256" }
            }), AiToolRisk.Mutating,
            DownloadAsync));
        registry.Register(new AiToolDefinition("agent_artifact_list", "List artifacts in the Hackermes artifact store (downloaded references and materials; never executed).",
            Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ArtifactList())));
        registry.Register(new AiToolDefinition("agent_artifact_read", "Read a stored text artifact as bounded, paged model context (offset/limit). Binary artifacts are refused — they belong behind ToolHost adapters.",
            Schema(new
            {
                fileName = new { type = "string" }, offset = new { type = "integer", description = "character offset, default 0" },
                maxChars = new { type = "integer", description = "characters to return, 1-16000 (default 8000)" }
            }), AiToolRisk.ReadOnly,
            (invocation, _) => ValueTask.FromResult(ArtifactRead(invocation.Arguments))));
    }

    private ToolResult UpsertSkill(JsonElement arguments)
    {
        try
        {
            var skill = new AgentSkill
            {
                Id = String(arguments, "id"), Name = String(arguments, "name"), Instructions = String(arguments, "instructions"),
                Enabled = Bool(arguments, "enabled", true), ToolNames = Strings(arguments, "toolNames").ToList()
            };
            var saved = _skills.Upsert(skill);
            return ToolResult.Ok(JsonSerializer.Serialize(saved));
        }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private ToolResult RemoveSkill(JsonElement arguments) => _skills.Remove(String(arguments, "id"))
        ? ToolResult.Ok("Skill removed.") : ToolResult.Fail("Skill was not found.");

    private ToolResult ArtifactList()
    {
        try { return ToolResult.Ok(JsonSerializer.Serialize(_artifacts.List())); }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private ToolResult ArtifactRead(JsonElement arguments)
    {
        try
        {
            var fileName = String(arguments, "fileName");
            var offset = Long(arguments, "offset");
            var maxChars = (int)Long(arguments, "maxChars", 8000);
            return ToolResult.Ok(JsonSerializer.Serialize(_artifacts.ReadText(fileName, offset, maxChars)));
        }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private static long Long(JsonElement arguments, string name, long fallback = 0) =>
        arguments.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : fallback;

    private ToolResult SetNotes(JsonElement arguments)
    {
        _memory.SetNotes(String(arguments, "notes"));
        return ToolResult.Ok("Persistent Agent memory updated.");
    }

    private async ValueTask<ToolResult> DownloadAsync(ToolInvocation invocation, CancellationToken ct)
    {
        try
        {
            var url = String(invocation.Arguments, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ToolResult.Fail("A valid absolute HTTPS URL is required.");
            var artifact = await _artifacts.DownloadAsync(uri, EmptyToNull(String(invocation.Arguments, "fileName")), EmptyToNull(String(invocation.Arguments, "sha256")), ct).ConfigureAwait(false);
            return ToolResult.Ok(JsonSerializer.Serialize(artifact));
        }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private static JsonElement Schema(object properties) => JsonSerializer.SerializeToElement(new { type = "object", properties, additionalProperties = false });
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
    private static bool Bool(JsonElement value, string name, bool fallback) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : fallback;
    private static string[] Strings(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).ToArray() : [];
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
