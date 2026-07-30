using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hookmes.Traffic.Rules;

public enum TrafficRuleImportMode
{
    Replace,
    Merge
}

public sealed record TrafficRuleChange(
    string Operation,
    string? RuleId,
    IReadOnlyList<TrafficRule> Rules);

public interface ITrafficRuleManager
{
    event Action<TrafficRuleChange>? Changed;
    string RulesFilePath { get; }
    IReadOnlyList<TrafficRule> GetAll();
    TrafficRule? Get(string id);
    TrafficRule Add(TrafficRule rule);
    TrafficRule Update(TrafficRule rule);
    bool Remove(string id);
    TrafficRule SetEnabled(string id, bool enabled);
    void Move(string id, int targetIndex);
    string ExportJson();
    void ImportJson(string json, TrafficRuleImportMode mode = TrafficRuleImportMode.Replace);
    void Reload();
}

/// <summary>
/// Owns the editable traffic-rule collection and keeps it synchronized with the hot-path rule set.
/// All mutations are persisted before they become visible to request interception.
/// </summary>
public sealed class TrafficRuleManager : ITrafficRuleManager
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "traffic-rules.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _gate = new();
    private readonly ITrafficRuleSet _ruleSet;
    private readonly string _rulesFilePath;
    private List<TrafficRule> _rules = [];

    public TrafficRuleManager(ITrafficRuleSet ruleSet)
        : this(ruleSet, ResolveDefaultPath())
    {
    }

    public TrafficRuleManager(ITrafficRuleSet ruleSet, string rulesFilePath)
    {
        _ruleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
        if (string.IsNullOrWhiteSpace(rulesFilePath))
            throw new ArgumentException("Rules file path is required.", nameof(rulesFilePath));

        _rulesFilePath = Path.GetFullPath(rulesFilePath);
        Reload();
    }

    public event Action<TrafficRuleChange>? Changed;

    public string RulesFilePath => _rulesFilePath;

    public IReadOnlyList<TrafficRule> GetAll()
    {
        lock (_gate)
            return _rules.ToArray();
    }

    public TrafficRule? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _rules.FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal));
    }

    public TrafficRule Add(TrafficRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            if (_rules.Any(existing => string.Equals(existing.Id, rule.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"A traffic rule with id '{rule.Id}' already exists.");

            var next = new List<TrafficRule>(_rules) { rule };
            Commit(next, "add", rule.Id);
            return rule;
        }
    }

    public TrafficRule Update(TrafficRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            var index = FindIndex(rule.Id);
            var next = new List<TrafficRule>(_rules) { [index] = rule };
            Commit(next, "update", rule.Id);
            return rule;
        }
    }

    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var index = _rules.FindIndex(rule => string.Equals(rule.Id, id, StringComparison.Ordinal));
            if (index < 0)
                return false;

            var next = new List<TrafficRule>(_rules);
            next.RemoveAt(index);
            Commit(next, "remove", id);
            return true;
        }
    }

    public TrafficRule SetEnabled(string id, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var index = FindIndex(id);
            var updated = _rules[index] with { Enabled = enabled };
            var next = new List<TrafficRule>(_rules) { [index] = updated };
            Commit(next, enabled ? "enable" : "disable", id);
            return updated;
        }
    }

    public void Move(string id, int targetIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (targetIndex < 0 || targetIndex >= _rules.Count)
                throw new ArgumentOutOfRangeException(nameof(targetIndex));

            var sourceIndex = FindIndex(id);
            if (sourceIndex == targetIndex)
                return;

            var next = new List<TrafficRule>(_rules);
            var rule = next[sourceIndex];
            next.RemoveAt(sourceIndex);
            next.Insert(targetIndex, rule);
            Commit(next, "move", id);
        }
    }

    public string ExportJson()
    {
        lock (_gate)
            return Serialize(_rules);
    }

    public void ImportJson(string json, TrafficRuleImportMode mode = TrafficRuleImportMode.Replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var imported = Deserialize(json);

        lock (_gate)
        {
            List<TrafficRule> next;
            if (mode == TrafficRuleImportMode.Replace)
            {
                next = imported;
            }
            else
            {
                next = new List<TrafficRule>(_rules);
                foreach (var rule in imported)
                {
                    var index = next.FindIndex(existing => string.Equals(existing.Id, rule.Id, StringComparison.Ordinal));
                    if (index < 0)
                        next.Add(rule);
                    else
                        next[index] = rule;
                }
            }

            Commit(next, "import", null);
        }
    }

    public void Reload()
    {
        TrafficRuleChange? change = null;
        lock (_gate)
        {
            var loaded = TryRead(_rulesFilePath) ?? TryRead(_rulesFilePath + ".bak") ?? [];
            Validate(loaded);
            _rules = loaded;
            _ruleSet.Replace(loaded);
            change = new TrafficRuleChange("reload", null, loaded.ToArray());
        }

        Changed?.Invoke(change);
    }

    private void Commit(List<TrafficRule> next, string operation, string? ruleId)
    {
        Validate(next);
        WriteAtomically(next);
        _ruleSet.Replace(next);
        _rules = next;
        Changed?.Invoke(new TrafficRuleChange(operation, ruleId, next.ToArray()));
    }

    private int FindIndex(string id)
    {
        var index = _rules.FindIndex(rule => string.Equals(rule.Id, id, StringComparison.Ordinal));
        return index >= 0 ? index : throw new KeyNotFoundException($"Traffic rule '{id}' was not found.");
    }

    private static void Validate(IReadOnlyList<TrafficRule> rules)
    {
        var validator = new TrafficRuleSet();
        validator.Replace(rules);
        if (rules.Any(rule => string.IsNullOrWhiteSpace(rule.UrlPattern)))
            throw new ArgumentException("Rule URL pattern is required.", nameof(rules));
    }

    private void WriteAtomically(IReadOnlyList<TrafficRule> rules)
    {
        var directory = Path.GetDirectoryName(_rulesFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _rulesFilePath + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(rules), new UTF8Encoding(false));
        try
        {
            if (File.Exists(_rulesFilePath))
                File.Copy(_rulesFilePath, _rulesFilePath + ".bak", overwrite: true);
            File.Move(temporaryPath, _rulesFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static List<TrafficRule>? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return Deserialize(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string Serialize(IReadOnlyList<TrafficRule> rules) =>
        JsonSerializer.Serialize(new TrafficRuleDocument(CurrentSchemaVersion, rules), JsonOptions);

    private static List<TrafficRule> Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<TrafficRuleDocument>(json, JsonOptions)
            ?? throw new JsonException("Traffic rule document is empty.");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Traffic rule schema version {document.SchemaVersion} is not supported.");

        var rules = document.Rules?.ToList() ?? [];
        Validate(rules);
        return rules;
    }

    private static string ResolveDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetTempPath();
        return Path.Combine(root, "Hookmes", FileName);
    }

    private sealed record TrafficRuleDocument(int SchemaVersion, IReadOnlyList<TrafficRule>? Rules);
}
