using Hookmes.Traffic.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hookmes.Traffic.Rules;

public interface ITrafficRuleSet
{
    IReadOnlyList<TrafficRule> Snapshot { get; }
    void Replace(IEnumerable<TrafficRule> rules);
    TrafficRule? Match(TrafficMessage message);
}

public sealed class TrafficRuleSet : ITrafficRuleSet
{
    private TrafficRule[] _rules = [];
    public IReadOnlyList<TrafficRule> Snapshot => System.Threading.Volatile.Read(ref _rules);

    public void Replace(IEnumerable<TrafficRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var next = rules.ToArray();
        if (next.Any(x => string.IsNullOrWhiteSpace(x.Id)))
            throw new ArgumentException("Rule id is required.", nameof(rules));
        if (next.Any(x => x.RequestEdit is not null && x.ResponseEdit is not null))
            throw new ArgumentException("A rule cannot edit both request and response stages.", nameof(rules));
        if (next.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != next.Length)
            throw new ArgumentException("Rule ids must be unique.", nameof(rules));
        System.Threading.Volatile.Write(ref _rules, next);
    }

    public TrafficRule? Match(TrafficMessage message) => Snapshot.FirstOrDefault(rule =>
        rule.Enabled &&
        (rule.Stage is null || rule.Stage == message.Stage) &&
        (rule.Method is null || string.Equals(rule.Method, message.Method, StringComparison.OrdinalIgnoreCase)) &&
        GlobMatches(rule.UrlPattern, message.Url));

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }
}
