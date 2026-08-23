using Hackermes.Inspector.ViewModels;
using Hackermes.Traffic.Models;
using Hackermes.Traffic.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hackermes.App;

/// <summary>
/// Maps workbench rule drafts — including the structured "edit" and "fulfill" payloads —
/// to persistent rules, and reconstructs drafts for round-trip form editing.
/// </summary>
internal static class TrafficRuleDraftMapper
{
    public const int MaximumHeaderNameLength = 256;
    public const int MaximumHeaderValueLength = 8192;
    /// <summary>Aligned with HttpPacketParameters.MaximumValueLength.</summary>
    public const int MaximumBodyBytes = 256 * 1024;

    public static TrafficRule BuildRule(TrafficRuleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var id = draft.Id.Trim();
        if (id.Length == 0) throw new ArgumentException("Rule id is required.");
        var pattern = draft.UrlPattern.Trim();
        if (pattern.Length == 0) pattern = "*";
        var stage = NormalizeStage(draft.Stage);
        var method = MethodOrNull(draft.Method);

        var behavior = draft.Behavior.Trim().ToLowerInvariant();
        return behavior switch
        {
            "pause" => new TrafficRule(id, pattern, method, stage, Pause: true),
            "drop" => new TrafficRule(id, pattern, method, stage, Fail: true),
            "edit" => new TrafficRule(id, pattern, method, stage, RequestEdit: BuildRequestEdit(draft)),
            "fulfill" => new TrafficRule(id, pattern, method, stage, ResponseEdit: BuildResponseEdit(draft)),
            _ => throw new ArgumentException("Behavior must be pause, drop, edit or fulfill.")
        };
    }

    public static TrafficRuleDraft ToDraft(TrafficRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var edit = rule.RequestEdit;
        var fulfill = rule.ResponseEdit;
        var behavior = rule.Fail ? "drop"
            : rule.Pause ? "pause"
            : fulfill is not null ? "fulfill"
            : edit is not null ? "edit" : "pause";
        return new TrafficRuleDraft(
            rule.Id, rule.UrlPattern, rule.Method,
            rule.Stage?.ToString().ToLowerInvariant() ?? "any", behavior,
            edit?.Url, MethodOrNull(edit?.Method), ToHeaderEdits(edit?.Headers), BodyToText(edit?.Body),
            fulfill?.Status, fulfill?.StatusText, ToHeaderEdits(fulfill?.Headers), BodyToText(fulfill?.Body));
    }

    private static TrafficRequestEdit BuildRequestEdit(TrafficRuleDraft draft)
    {
        var url = NullIfEmpty(draft.RequestUrl);
        var method = MethodOrNull(draft.RequestMethod);
        var headers = HeadersOrNull(draft.RequestHeaders);
        var body = TextToBody(draft.RequestBody);
        if (url is null && method is null && headers is null && body is null)
            throw new ArgumentException(
                "The edit behavior needs at least one request change (url, method, headers or body).");
        return new TrafficRequestEdit(url, method, headers, body);
    }

    private static TrafficResponseEdit BuildResponseEdit(TrafficRuleDraft draft)
    {
        var status = draft.ResponseStatus ?? 200;
        if (status is < 100 or > 999)
            throw new ArgumentException("Response status must be between 100 and 999.");
        return new TrafficResponseEdit(status, NullIfEmpty(draft.ResponseStatusText),
            HeadersOrNull(draft.ResponseHeaders), TextToBody(draft.ResponseBody));
    }

    private static TrafficStage? NormalizeStage(string stage) => stage.Trim().ToLowerInvariant() switch
    {
        "request" => TrafficStage.Request,
        "response" => TrafficStage.Response,
        "any" or "" => null,
        _ => throw new ArgumentException("Stage must be request, response or any.")
    };

    private static string? MethodOrNull(string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return null;
        var trimmed = method.Trim();
        return trimmed == "*" ? null : trimmed;
    }

    private static IReadOnlyList<TrafficHeader>? HeadersOrNull(IReadOnlyList<TrafficRuleHeaderEdit>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name) || header.Name.Length > MaximumHeaderNameLength ||
                header.Value.Length > MaximumHeaderValueLength)
                throw new ArgumentException($"Header '{header.Name}' exceeds the allowed name/value length.");
        }
        return headers.Select(header => new TrafficHeader(header.Name, header.Value)).ToArray();
    }

    private static IReadOnlyList<TrafficRuleHeaderEdit>? ToHeaderEdits(IReadOnlyList<TrafficHeader>? headers) =>
        headers is null || headers.Count == 0 ? null :
        headers.Select(header => new TrafficRuleHeaderEdit(header.Name, header.Value)).ToArray();

    private static byte[]? TextToBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var bytes = Encoding.UTF8.GetBytes(body);
        if (bytes.Length > MaximumBodyBytes)
            throw new ArgumentException($"Rule body must not exceed {MaximumBodyBytes} bytes.");
        return bytes;
    }

    private static string? BodyToText(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try { return new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException)
        {
            // Binary bodies are out of the form's scope; they stay editable via JSON import.
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
