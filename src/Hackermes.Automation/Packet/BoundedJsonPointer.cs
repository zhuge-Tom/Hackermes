using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hackermes.Automation.Packet;

public sealed record JsonPointerEntry(string Pointer, string Value, string Kind);

/// <summary>RFC 6901-style JSON Pointer traversal with explicit depth and entry limits.</summary>
public static class BoundedJsonPointer
{
    public const int DefaultMaximumDepth = 32;
    public const int DefaultMaximumEntries = 2_000;

    public static IReadOnlyList<JsonPointerEntry> Read(string json, int maximumDepth = DefaultMaximumDepth, int maximumEntries = DefaultMaximumEntries)
    {
        ValidateBounds(maximumDepth, maximumEntries);
        var root = JsonNode.Parse(json) ?? throw new JsonException("JSON body is empty.");
        var entries = new List<JsonPointerEntry>();
        Walk(root, string.Empty, 0, maximumDepth, maximumEntries, entries);
        return entries;
    }

    public static string Set(string json, string pointer, string value)
    {
        var root = JsonNode.Parse(json) ?? throw new JsonException("JSON body is empty.");
        var tokens = Tokens(pointer);
        if (tokens.Length == 0) throw new ArgumentException("Replacing the JSON document root is not supported.", nameof(pointer));
        JsonNode current = root;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            current = current switch
            {
                JsonObject objectNode when objectNode[tokens[index]] is { } next => next,
                JsonArray arrayNode when TryIndex(tokens[index], arrayNode.Count, out var item) && arrayNode[item] is { } next => next,
                _ => throw new KeyNotFoundException($"JSON Pointer '{pointer}' was not found.")
            };
        }
        var token = tokens[^1];
        switch (current)
        {
            case JsonObject objectNode when objectNode.TryGetPropertyValue(token, out var old): objectNode[token] = Coerce(old, value); break;
            case JsonArray arrayNode when TryIndex(token, arrayNode.Count, out var item): arrayNode[item] = Coerce(arrayNode[item], value); break;
            default: throw new KeyNotFoundException($"JSON Pointer '{pointer}' was not found.");
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void Walk(JsonNode? node, string pointer, int depth, int maximumDepth, int maximumEntries, List<JsonPointerEntry> output)
    {
        if (output.Count >= maximumEntries) throw new InvalidOperationException($"JSON entry limit of {maximumEntries} was reached.");
        if (depth > maximumDepth) throw new InvalidOperationException($"JSON depth limit of {maximumDepth} was reached.");
        switch (node)
        {
            case JsonObject objectNode:
                if (objectNode.Count == 0) output.Add(new JsonPointerEntry(pointer, "{}", "object"));
                foreach (var pair in objectNode) Walk(pair.Value, pointer + "/" + Escape(pair.Key), depth + 1, maximumDepth, maximumEntries, output);
                break;
            case JsonArray arrayNode:
                if (arrayNode.Count == 0) output.Add(new JsonPointerEntry(pointer, "[]", "array"));
                for (var index = 0; index < arrayNode.Count; index++) Walk(arrayNode[index], pointer + "/" + index, depth + 1, maximumDepth, maximumEntries, output);
                break;
            default:
                output.Add(new JsonPointerEntry(pointer, node?.ToJsonString() ?? "null", node is null ? "null" : "value"));
                break;
        }
    }

    private static string[] Tokens(string pointer)
    {
        if (pointer is null || !pointer.StartsWith('/')) throw new ArgumentException("JSON Pointer must start with '/'.", nameof(pointer));
        return pointer[1..].Split('/').Select(Unescape).ToArray();
    }
    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static string Unescape(string value) => value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    private static bool TryIndex(string value, int count, out int index) => int.TryParse(value, out index) && index >= 0 && index < count;
    private static JsonNode? Coerce(JsonNode? old, string value)
    {
        if (old is JsonValue)
        {
            try { if (JsonNode.Parse(value) is { } parsed) return parsed; }
            catch (JsonException) { }
        }
        return JsonValue.Create(value);
    }
    private static void ValidateBounds(int depth, int entries)
    {
        if (depth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(depth));
        if (entries is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(entries));
    }
}
