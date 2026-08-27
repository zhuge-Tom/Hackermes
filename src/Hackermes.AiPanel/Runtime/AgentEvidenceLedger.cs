using Hackermes.AiPanel.OpenAI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Sidecar evidence that survives context compaction: packet ids, observation codes,
/// spill locators and recent tool errors. Injected ephemerally each model step so
/// long authorized-assessment investigations stay coherent after auto-compact.
/// </summary>
public sealed class AgentEvidenceLedger
{
    private const int MaxPacketIds = 32;
    private const int MaxFindings = 16;
    private const int MaxSpills = 8;
    private const int MaxErrors = 8;
    private const int MaxRenderChars = 1_500;
    private const int MaxErrorChars = 160;

    private static readonly Regex SpillLocatorRegex = new(
        "spill:[0-9a-f]{32}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CodePropertyRegex = new(
        "\"code\"\\s*:\\s*\"([a-z][a-z0-9]*(?:-[a-z0-9]+)+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PacketIdPropertyRegex = new(
        "\"(?:id|packetId)\"\\s*:\\s*\"([^\"]{1,128})\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly List<string> _packetIds = [];
    private readonly List<Observation> _observations = [];
    private readonly List<string> _spills = [];
    private readonly List<string> _errors = [];

    public void Observe(string toolName, string content, bool success)
    {
        toolName ??= string.Empty;
        content ??= string.Empty;
        lock (_gate)
        {
            if (IsPacketTool(toolName))
            {
                foreach (var id in ExtractPacketIds(content))
                    Remember(_packetIds, id, MaxPacketIds);
            }

            foreach (var code in ExtractFindingCodes(content))
                RememberObservation(code, toolName);

            foreach (var spill in ExtractSpills(content))
                Remember(_spills, spill, MaxSpills);

            if (!success)
            {
                var line = FirstLine(content);
                if (line.Length > MaxErrorChars) line = line[..MaxErrorChars];
                if (line.Length > 0)
                {
                    _errors.Insert(0, line);
                    while (_errors.Count > MaxErrors) _errors.RemoveAt(_errors.Count - 1);
                }
            }
        }
    }

    public string Render()
    {
        lock (_gate)
        {
            if (_packetIds.Count == 0 && _observations.Count == 0 &&
                _spills.Count == 0 && _errors.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine("【证据台账】");
            if (_packetIds.Count > 0)
                builder.Append("packets: ").Append(string.Join(", ", _packetIds)).AppendLine();
            if (_observations.Count > 0)
                builder.Append("observations: ")
                    .Append(string.Join(", ", _observations.Select(static item => $"{item.Code} ({item.Tool})")))
                    .AppendLine();
            if (_spills.Count > 0)
                builder.Append("spills: ").Append(string.Join(" ", _spills)).AppendLine();
            if (_errors.Count > 0)
                builder.Append("errors: ").Append(string.Join("；", _errors)).AppendLine();

            var text = builder.ToString().TrimEnd();
            return text.Length <= MaxRenderChars ? text : text[..(MaxRenderChars - 1)] + "…";
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _packetIds.Clear();
            _observations.Clear();
            _spills.Clear();
            _errors.Clear();
        }
    }

    private void RememberObservation(string code, string tool)
    {
        var existing = _observations.FindIndex(item =>
            string.Equals(item.Code, code, StringComparison.Ordinal) &&
            string.Equals(item.Tool, tool, StringComparison.Ordinal));
        if (existing >= 0) _observations.RemoveAt(existing);
        _observations.Insert(0, new Observation(code, tool));
        while (_observations.Count > MaxFindings) _observations.RemoveAt(_observations.Count - 1);
    }

    private static void Remember(List<string> items, string value, int cap)
    {
        var existing = items.FindIndex(item => string.Equals(item, value, StringComparison.Ordinal));
        if (existing >= 0) items.RemoveAt(existing);
        items.Insert(0, value);
        while (items.Count > cap) items.RemoveAt(items.Count - 1);
    }

    private static bool IsPacketTool(string toolName) =>
        toolName.StartsWith("packet_", StringComparison.Ordinal) ||
        toolName is "packet_analyze" or "packet_query" or "packet_show" or "packet_list";

    private static List<string> ExtractPacketIds(string content)
    {
        var ids = new List<string>();
        if (TryParseJson(content, out var root))
        {
            CollectStringProperties(root, ids, static name =>
                name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("packetId", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            foreach (Match match in PacketIdPropertyRegex.Matches(content))
            {
                var value = match.Groups[1].Value;
                if (value.Length > 0) ids.Add(value);
            }
        }
        return ids;
    }

    private static List<string> ExtractFindingCodes(string content)
    {
        var codes = new List<string>();
        if (TryParseJson(content, out var root))
            CollectStringProperties(root, codes, static name => name.Equals("code", StringComparison.OrdinalIgnoreCase));
        foreach (Match match in CodePropertyRegex.Matches(content))
            codes.Add(match.Groups[1].Value);
        return codes.Where(IsObservationCode).Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractSpills(string content)
    {
        var spills = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in SpillLocatorRegex.Matches(content))
        {
            if (!seen.Add(match.Value)) continue;
            spills.Add(match.Value);
            if (spills.Count >= MaxSpills) break;
        }
        return spills;
    }

    private static bool IsObservationCode(string code)
    {
        if (code.Length is < 3 or > 64) return false;
        var hyphen = false;
        for (var index = 0; index < code.Length; index++)
        {
            var character = code[index];
            if (character == '-')
            {
                if (index == 0 || index == code.Length - 1 || code[index - 1] == '-') return false;
                hyphen = true;
                continue;
            }
            if (character is < 'a' or > 'z' && character is < '0' or > '9') return false;
        }
        return hyphen;
    }

    private static bool TryParseJson(string content, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return false;
            try
            {
                using var document = JsonDocument.Parse(content[start..(end + 1)]);
                root = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private static void CollectStringProperties(JsonElement element, List<string> dest, Func<string, bool> nameMatch)
    {
        if (dest.Count >= 64) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && nameMatch(property.Name))
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) dest.Add(value);
                    }
                    else CollectStringProperties(property.Value, dest, nameMatch);
                    if (dest.Count >= 64) return;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStringProperties(item, dest, nameMatch);
                    if (dest.Count >= 64) return;
                }
                break;
        }
    }

    private static string FirstLine(string content)
    {
        var end = content.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? content : content[..end]).Trim();
        return line;
    }

    private readonly record struct Observation(string Code, string Tool);
}

/// <summary>
/// Injects the evidence ledger as an ephemeral user-role context block (not operator speech)
/// so compaction cannot drop packet ids, finding codes, spill locators or recent errors.
/// </summary>
public sealed class EvidenceLedgerPreStepHook(AgentEvidenceLedger ledger) : IAgentPreStepHook
{
    public ValueTask<PreStepDecision> BeforeStepAsync(PreStepInput input, CancellationToken ct)
    {
        var render = ledger.Render();
        if (render.Length == 0)
            return ValueTask.FromResult(PreStepDecision.Proceed);
        return ValueTask.FromResult(PreStepDecision.AppendEphemeral(
        [
            new ChatMessage("user", "【上下文注入·证据台账】\n" + render)
        ]));
    }
}
