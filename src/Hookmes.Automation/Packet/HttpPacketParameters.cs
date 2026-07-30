using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hookmes.Automation.Packet;

public enum HttpParameterLocation { Query, Form, Json }

public sealed record HttpPacketParameter(
    HttpParameterLocation Location,
    string Name,
    string Value,
    int Occurrence);

/// <summary>Content-aware parameter inspection and targeted mutation without rebuilding unrelated fields.</summary>
public static class HttpPacketParameters
{
    public static IReadOnlyList<HttpPacketParameter> Read(HttpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var result = new List<HttpPacketParameter>();
        if (packet.Kind == HttpPacketKind.Request && packet.Target is not null)
            ReadPairs(GetQuery(packet.Target), HttpParameterLocation.Query, result);

        var mediaType = packet.HeaderValues("Content-Type").FirstOrDefault()?.Split(';')[0].Trim();
        if (mediaType?.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
            ReadPairs(packet.Body, HttpParameterLocation.Form, result);
        else if (mediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true ||
                 mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
            ReadJson(packet.Body, result);
        return result;
    }

    public static HttpPacket Set(HttpPacket packet, HttpParameterLocation location,
        string name, int occurrence, string value)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Parameter name is required.", nameof(name));
        if (occurrence < 0) throw new ArgumentOutOfRangeException(nameof(occurrence));

        return location switch
        {
            HttpParameterLocation.Query => SetQuery(packet, name, occurrence, value),
            HttpParameterLocation.Form => SetForm(packet, name, occurrence, value),
            HttpParameterLocation.Json => SetJson(packet, name, occurrence, value),
            _ => throw new ArgumentOutOfRangeException(nameof(location))
        };
    }

    private static HttpPacket SetQuery(HttpPacket packet, string name, int occurrence, string value)
    {
        if (packet.Kind != HttpPacketKind.Request || packet.Target is null)
            throw new InvalidDataException("Query parameters require an HTTP request target.");
        var target = packet.Target;
        var fragmentAt = target.IndexOf('#');
        var fragment = fragmentAt < 0 ? "" : target[fragmentAt..];
        var withoutFragment = fragmentAt < 0 ? target : target[..fragmentAt];
        var queryAt = withoutFragment.IndexOf('?');
        if (queryAt < 0) throw Missing(HttpParameterLocation.Query, name, occurrence);
        var changed = SetPair(withoutFragment[(queryAt + 1)..], name, occurrence, value, form: false);
        return packet with { Target = withoutFragment[..(queryAt + 1)] + changed + fragment };
    }

    private static HttpPacket SetForm(HttpPacket packet, string name, int occurrence, string value)
    {
        RequireMediaType(packet, "application/x-www-form-urlencoded", HttpParameterLocation.Form);
        return packet with { Body = SetPair(packet.Body, name, occurrence, value, form: true) };
    }

    private static HttpPacket SetJson(HttpPacket packet, string name, int occurrence, string value)
    {
        if (occurrence != 0) throw Missing(HttpParameterLocation.Json, name, occurrence);
        var mediaType = packet.HeaderValues("Content-Type").FirstOrDefault()?.Split(';')[0].Trim();
        if (mediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) != true &&
            mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("JSON parameter editing requires an application/json or +json Content-Type.");
        JsonObject root;
        try { root = JsonNode.Parse(packet.Body) as JsonObject ?? throw new InvalidDataException("JSON body must be an object."); }
        catch (JsonException exception) { throw new InvalidDataException("JSON body is malformed.", exception); }
        if (!root.TryGetPropertyValue(name, out var old)) throw Missing(HttpParameterLocation.Json, name, occurrence);
        root[name] = CoerceJsonValue(old, value);
        return packet with { Body = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) };
    }

    private static JsonNode? CoerceJsonValue(JsonNode? old, string value)
    {
        if (old is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out _) && bool.TryParse(value, out var boolean)) return JsonValue.Create(boolean);
            if (scalar.TryGetValue<long>(out _) && long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            if (scalar.TryGetValue<double>(out _) && double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
        }
        return JsonValue.Create(value);
    }

    private static void ReadJson(string body, List<HttpPacketParameter> result)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root) return;
            foreach (var property in root)
                result.Add(new HttpPacketParameter(HttpParameterLocation.Json, property.Key,
                    property.Value?.ToJsonString() ?? "null", 0));
        }
        catch (JsonException) { }
    }

    private static void ReadPairs(string source, HttpParameterLocation location, List<HttpPacketParameter> result)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in source.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Decode(separator < 0 ? pair : pair[..separator]);
            var value = Decode(separator < 0 ? "" : pair[(separator + 1)..]);
            var occurrence = occurrences.GetValueOrDefault(name);
            occurrences[name] = occurrence + 1;
            result.Add(new HttpPacketParameter(location, name, value, occurrence));
        }
    }

    private static string SetPair(string source, string name, int occurrence, string value, bool form)
    {
        var pairs = source.Split('&').ToArray();
        var found = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var separator = pairs[i].IndexOf('=');
            var encodedName = separator < 0 ? pairs[i] : pairs[i][..separator];
            if (!Decode(encodedName).Equals(name, StringComparison.Ordinal)) continue;
            if (found++ != occurrence) continue;
            pairs[i] = encodedName + "=" + Encode(value, form);
            return string.Join('&', pairs);
        }
        throw Missing(form ? HttpParameterLocation.Form : HttpParameterLocation.Query, name, occurrence);
    }

    private static string GetQuery(string target)
    {
        var queryAt = target.IndexOf('?');
        if (queryAt < 0) return "";
        var fragmentAt = target.IndexOf('#', queryAt + 1);
        return fragmentAt < 0 ? target[(queryAt + 1)..] : target[(queryAt + 1)..fragmentAt];
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static string Encode(string value, bool form)
    {
        var encoded = Uri.EscapeDataString(value);
        return form ? encoded.Replace("%20", "+", StringComparison.Ordinal) : encoded;
    }

    private static void RequireMediaType(HttpPacket packet, string expected, HttpParameterLocation location)
    {
        var actual = packet.HeaderValues("Content-Type").FirstOrDefault()?.Split(';')[0].Trim();
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{location} parameter editing requires Content-Type {expected}.");
    }

    private static KeyNotFoundException Missing(HttpParameterLocation location, string name, int occurrence) =>
        new($"{location} parameter '{name}' occurrence {occurrence} was not found.");
}
