using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hackermes.AiPanel.Agent;

/// <summary>A user- or Agent-authored workflow instruction. It may narrow available tools but never expands policy permissions.</summary>
public sealed class AgentSkill
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> ToolNames { get; set; } = new();

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed class AgentSkillDocument
{
    public int Version { get; set; } = 1;
    public List<AgentSkill> Skills { get; set; } = new();
}

public interface IAgentSkillStore
{
    IReadOnlyList<AgentSkill> Snapshot();
    AgentSkill Upsert(AgentSkill skill);
    bool Remove(string id);
}

public sealed class AgentSkillStore : IAgentSkillStore
{
    private const int MaxSkills = 64;
    private readonly ISettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private AgentSkillDocument? _document;

    public AgentSkillStore(ISettingsService settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger.ForCategory(nameof(AgentSkillStore));
    }

    public IReadOnlyList<AgentSkill> Snapshot()
    {
        lock (_gate) return Load().Skills.Select(Clone).ToArray();
    }

    public AgentSkill Upsert(AgentSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var clean = Normalize(skill);
        if (clean.Name.Length == 0) throw new ArgumentException("Skill name cannot be empty.");
        if (clean.Instructions.Length == 0) throw new ArgumentException("Skill instructions cannot be empty.");

        lock (_gate)
        {
            var document = Load();
            var index = document.Skills.FindIndex(value => string.Equals(value.Id, clean.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                if (document.Skills.Count >= MaxSkills) throw new InvalidOperationException($"Only {MaxSkills} skills may be stored.");
                document.Skills.Add(clean);
            }
            else document.Skills[index] = clean;
            Save(document);
            return Clone(clean);
        }
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            var document = Load();
            var removed = document.Skills.RemoveAll(value => string.Equals(value.Id, id, StringComparison.Ordinal)) > 0;
            if (removed) Save(document);
            return removed;
        }
    }

    private AgentSkillDocument Load()
    {
        if (_document is not null) return _document;
        try
        {
            var path = PathFor("agent-skills.json");
            if (File.Exists(path))
                _document = JsonSerializer.Deserialize(File.ReadAllText(path), AgentStateJsonContext.Default.AgentSkillDocument);
            else
            {
                _document = new AgentSkillDocument { Skills = [CreateDefaultAssessmentSkill()] };
                Save(_document);
                return _document;
            }
        }
        catch (Exception ex) { _logger.Warn($"Unable to read Agent skills: {ex.Message}"); }

        _document ??= new AgentSkillDocument();
        _document.Skills ??= new List<AgentSkill>();
        _document.Skills = _document.Skills.Where(skill => skill is not null).Select(Normalize).Where(skill => skill.Name.Length > 0 && skill.Instructions.Length > 0).Take(MaxSkills).ToList();
        if (MergeDefaultAssessmentSkill(_document))
            Save(_document);
        return _document;
    }

    private void Save(AgentSkillDocument document) => Write(PathFor("agent-skills.json"), JsonSerializer.Serialize(document, AgentStateJsonContext.Default.AgentSkillDocument));

    private string PathFor(string fileName)
    {
        var directory = Path.GetDirectoryName(_settings.SettingsFilePath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    private static void Write(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static AgentSkill Normalize(AgentSkill input) => new()
    {
        Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N") : input.Id.Trim()[..Math.Min(input.Id.Trim().Length, 96)],
        Name = (input.Name ?? string.Empty).Trim()[..Math.Min((input.Name ?? string.Empty).Trim().Length, 120)],
        Instructions = (input.Instructions ?? string.Empty).Trim()[..Math.Min((input.Instructions ?? string.Empty).Trim().Length, 12_000)],
        Enabled = input.Enabled,
        ToolNames = (input.ToolNames ?? new List<string>()).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.Ordinal).Take(64).ToList()
    };

    /// <summary>
    /// Stock playbook files seeded before page_navigate existed omit it from ToolNames,
    /// which hides the only tool that can create a tab. Merge missing defaults in place
    /// without reseeding empty or custom skill lists.
    /// </summary>
    internal static bool MergeDefaultAssessmentSkill(AgentSkillDocument document)
    {
        var index = document.Skills.FindIndex(skill =>
            string.Equals(skill.Id, DefaultAssessmentSkillId, StringComparison.Ordinal));
        if (index < 0) return false;

        var current = document.Skills[index];
        var fresh = CreateDefaultAssessmentSkill();
        var changed = false;
        foreach (var name in fresh.ToolNames)
        {
            if (current.ToolNames.Contains(name, StringComparer.Ordinal)) continue;
            current.ToolNames.Add(name);
            changed = true;
        }

        if (current.Instructions.StartsWith("Authorized assessment playbook", StringComparison.Ordinal)
            && !current.Instructions.Equals(fresh.Instructions, StringComparison.Ordinal))
        {
            current.Instructions = fresh.Instructions;
            changed = true;
        }

        if (changed) document.Skills[index] = Normalize(current);
        return changed;
    }

    public const string DefaultAssessmentSkillId = "authorized-assessment";

    public static AgentSkill CreateDefaultAssessmentSkill() => new()
    {
        Id = DefaultAssessmentSkillId,
        Name = "授权评估",
        Enabled = true,
        Instructions =
            "Authorized assessment playbook: systematic, evidence-first coverage across classes. " +
            "Never claim a vulnerability without tool evidence; never echo secrets; never scan a host outside the approved scope. " +
            "If the scope is '*' or '*.domain', discover in-scope hosts first, then scan those exact hosts. " +
            "No tab: page_navigate to an in-scope URL (creates a tab) or assessment_authorize_and_run with an exact target; do not wait for the operator. " +
            "Workflow (repeat per in-scope asset):\n" +
            "1) RECON: assessment_authorize_and_run with recon.dns.resolve, recon.nmap.quick, recon.nmap.service, recon.http.headers, recon.http.get, recon.httpx.probe, recon.dirsearch.quick; run recon.subdomain.enum on each in-scope wildcard root domain (uses the bundled subdomain wordlist automatically) to enumerate hosts. Call assessment_resources to see available wordlist/payload corpora before choosing inputs.\n" +
            "2) FINGERPRINT: read recon.http.headers for tech/headers/WAF; read recon.dirsearch.quick hits and recon.nmap.quick open ports.\n" +
            "3) VERIFY per class, only on exact in-scope targets:\n" +
            "   - SQL/command/LDAP injection: first run probe.param.corpus per suspicious parameter (uses a bundled payload corpus; emits Medium 'candidate — verify', NOT auto-confirmed), then confirm with probe.sqlmap.inject (auto-confirms High + PoC; ignore 'not injectable').\n" +
            "   - Unauthorized access / API / BOLA: probe.unauthorized.access (auto-confirms High + PoC URL); for app-layer, page_context → page_security_snapshot → packet_query/packet_analyze, then record codes.\n" +
            "   - XSS/SSRF/IDOR/JWT: page_context + page_eval_read for inspection JS, page_security_snapshot for unsafe sinks, packet_analyze for request/response; record with assessment_create_finding only when a code or observed behavior supports it.\n" +
            "   - Transport/config (HTTPS→HTTP downgrade, missing HSTS/CSP/frame): recon.http.headers auto-confirms Medium + PoC.\n" +
            "4) RECORD: assessment_create_finding (jobId + evidenceId, or source=page-snapshot|packet-analyze). Attach a PoC only from observed evidence (poc argument); never fabricate. Findings stay Unreviewed.\n" +
            "5) REPORT: assessment_report (json/markdown/html) and assessment_report_archive to persist; assessment_report_open to reveal the folder.\n" +
            "Prefer assessment_authorize_and_run for one bounded adapter. Use page_eval only for writes; use page_eval_read for reading. " +
            "Coverage is complete when you have run recon + verified each in-scope class on each in-scope host and recorded or explicitly ruled out the finding.",
        ToolNames =
        [
            "page_navigate", "page_context", "page_security_snapshot", "page_query", "page_screenshot", "page_eval_read", "page_wait",
            "console_read", "network_list",
            "packet_query", "packet_analyze", "packet_show", "packet_body_info", "packet_body_chunk",
            "assessment_tools", "assessment_resources", "assessment_cases", "assessment_authorize_and_run", "assessment_create_scope_from_page",
            "assessment_evidence", "assessment_create_finding", "assessment_findings",
            "assessment_jobs", "assessment_job_status", "assessment_cancel", "assessment_report",
            "assessment_report_archive", "assessment_report_open",
            "todo_write", "goal_set", "goal_clear", "read_spill", "agent_subtask"
        ]
    };

    private static AgentSkill Clone(AgentSkill value) => new()
    {
        Id = value.Id, Name = value.Name, Instructions = value.Instructions, Enabled = value.Enabled, ToolNames = [.. value.ToolNames]
    };
}

public sealed class AgentMemoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

/// <summary>Persisted, redacted conversation state. Tool arguments/results are intentionally excluded.</summary>
public sealed class AgentMemoryDocument
{
    public int Version { get; set; } = 1;
    public string Summary { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<AgentMemoryMessage> RecentMessages { get; set; } = new();
}

public interface IAgentMemoryStore
{
    AgentMemoryDocument Load();
    void SaveConversation(string summary, IReadOnlyList<AgentMemoryMessage> recentMessages);
    void SetNotes(string notes);
    void Clear();
}

public sealed class AgentMemoryStore : IAgentMemoryStore
{
    private readonly ISettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private AgentMemoryDocument? _document;

    public AgentMemoryStore(ISettingsService settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger.ForCategory(nameof(AgentMemoryStore));
    }

    public AgentMemoryDocument Load()
    {
        lock (_gate)
        {
            if (_document is null)
            {
                try
                {
                    var path = PathFor();
                    if (File.Exists(path)) _document = JsonSerializer.Deserialize(File.ReadAllText(path), AgentStateJsonContext.Default.AgentMemoryDocument);
                }
                catch (Exception ex) { _logger.Warn($"Unable to read Agent memory: {ex.Message}"); }
                _document ??= new AgentMemoryDocument();
                Normalize(_document);
            }
            return Clone(_document);
        }
    }

    public void SaveConversation(string summary, IReadOnlyList<AgentMemoryMessage> recentMessages)
    {
        lock (_gate)
        {
            var document = _document ??= new AgentMemoryDocument();
            document.Summary = Limit(summary, 20_000);
            document.RecentMessages = recentMessages.Where(message => message is not null && (message.Role is "user" or "assistant"))
                .Select(message => new AgentMemoryMessage { Role = message.Role, Content = Limit(message.Content, 8_000) })
                .Where(message => message.Content.Length > 0).TakeLast(64).ToList();
            Normalize(document);
            Save(document);
        }
    }

    public void SetNotes(string notes)
    {
        lock (_gate)
        {
            var document = _document ??= new AgentMemoryDocument();
            document.Notes = Limit(notes, 12_000);
            Save(document);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _document = new AgentMemoryDocument();
            Save(_document);
        }
    }

    private void Save(AgentMemoryDocument document)
    {
        var path = PathFor();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, AgentStateJsonContext.Default.AgentMemoryDocument));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor() => Path.Combine(Path.GetDirectoryName(_settings.SettingsFilePath) ?? AppContext.BaseDirectory, "agent-memory.json");

    private static void Normalize(AgentMemoryDocument document)
    {
        document.Summary = Limit(document.Summary, 20_000);
        document.Notes = Limit(document.Notes, 12_000);
        document.RecentMessages ??= new List<AgentMemoryMessage>();
        document.RecentMessages = document.RecentMessages.Where(message => message is not null && (message.Role is "user" or "assistant"))
            .Select(message => new AgentMemoryMessage { Role = message.Role, Content = Limit(message.Content, 8_000) })
            .Where(message => message.Content.Length > 0).TakeLast(64).ToList();
    }

    private static AgentMemoryDocument Clone(AgentMemoryDocument value) => new()
    {
        Version = value.Version, Summary = value.Summary, Notes = value.Notes,
        RecentMessages = value.RecentMessages.Select(message => new AgentMemoryMessage { Role = message.Role, Content = message.Content }).ToList()
    };

    private static string Limit(string? value, int max) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, max)];
}

/// <summary>One persisted AI chat session: its own compacted summary plus recent verbatim messages.</summary>
public sealed class AgentSessionEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string WorkspaceId { get; set; } = string.Empty;
        public List<AgentMemoryMessage> RecentMessages { get; set; } = new();

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed class AgentSessionDocument
{
    public int Version { get; set; } = 1;
    public string ActiveId { get; set; } = string.Empty;
    public List<AgentSessionEntry> Sessions { get; set; } = new();
}

public interface IAgentSessionStore
{
    AgentSessionDocument Load();
    void Save(AgentSessionDocument document);
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentSkillDocument))]
[JsonSerializable(typeof(AgentMemoryDocument))]
[JsonSerializable(typeof(AgentSessionDocument))]
internal partial class AgentStateJsonContext : JsonSerializerContext
{
}
