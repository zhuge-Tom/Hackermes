using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hackermes.AiPanel.Tools;

/// <summary>Groups the flat tool registry into domains so the model can pick names without inventing them.</summary>
public static class AiToolCatalog
{
    public const string Browser = "browser";
    public const string Traffic = "traffic";
    public const string Assessment = "assessment";
    public const string Agent = "agent";
    public const string Mcp = "mcp";

    public static string Classify(string name)
    {
        if (string.IsNullOrEmpty(name)) return Agent;
        if (name.StartsWith("mcp_", StringComparison.Ordinal)) return Mcp;
        if (name.StartsWith("assessment_", StringComparison.Ordinal)) return Assessment;
        if (name.StartsWith("packet_", StringComparison.Ordinal) ||
            name.StartsWith("traffic_", StringComparison.Ordinal) ||
            name.StartsWith("repeater_", StringComparison.Ordinal) ||
            name.StartsWith("comparison_", StringComparison.Ordinal))
            return Traffic;
        if (name.StartsWith("page_", StringComparison.Ordinal) ||
            name.StartsWith("script_", StringComparison.Ordinal) ||
            name is "console_read" or "network_list")
            return Browser;
        return Agent;
    }

    public static string Format(IReadOnlyList<AiToolDefinition> tools)
    {
        if (tools.Count == 0) return string.Empty;
        var groups = tools
            .GroupBy(tool => Classify(tool.Name))
            .OrderBy(group => Rank(group.Key));
        var builder = new StringBuilder("Tool catalog (use these exact names; do not invent):\n");
        foreach (var group in groups)
        {
            var names = group.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            builder.Append(group.Key).Append(": ").Append(string.Join(", ", names)).Append('\n');
        }
        var text = builder.ToString();
        return text.Length <= 2_400 ? text : text[..2_399] + "…";
    }

    private static int Rank(string domain) => domain switch
    {
        Browser => 0,
        Traffic => 1,
        Assessment => 2,
        Agent => 3,
        _ => 4
    };
}
