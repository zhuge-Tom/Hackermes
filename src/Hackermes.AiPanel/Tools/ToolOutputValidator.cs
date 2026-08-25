using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Hackermes.AiPanel.Tools;

/// <summary>
/// Minimal JSON-Schema validator for declared tool outputs (dsh INVALID_TOOL_OUTPUT
/// lineage): type, enum, required, properties and array items — the subset a tool author
/// realistically declares. Validation is opt-in per tool via OutputSchema; tools that
/// return prose simply declare nothing and are never checked.
/// </summary>
public static class ToolOutputValidator
{
    public const string InvalidOutputCode = "INVALID_TOOL_OUTPUT";

    /// <summary>Returns an error description, or null when the value conforms.</summary>
    public static string? Validate(string content, JsonElement schema)
    {
        JsonDocument? document = null;
        try
        {
            if (string.IsNullOrWhiteSpace(content)) return "工具输出为空，但声明了输出模式。";
            document = JsonDocument.Parse(content);
            return ValidateElement(document.RootElement, schema, "$");
        }
        catch (JsonException ex)
        {
            return $"输出不是有效 JSON（{ex.Message}）。声明了输出模式的工具必须返回 JSON。";
        }
        finally { document?.Dispose(); }
    }

    private static string? ValidateElement(JsonElement value, JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;

        if (schema.TryGetProperty("type", out var typeElement))
        {
            var expected = typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() : null;
            var error = CheckType(value, expected, path);
            if (error is not null) return error;
        }

        if (schema.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
        {
            var raw = value.GetRawText();
            var matched = false;
            foreach (var option in enumElement.EnumerateArray())
                if (option.GetRawText() == raw) { matched = true; break; }
            if (!matched) return $"{path}: 值 {raw} 不在枚举范围内。";
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var requiredElement) &&
                requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var required in requiredElement.EnumerateArray())
                {
                    var name = required.GetString();
                    if (name is { Length: > 0 } && !value.TryGetProperty(name, out _))
                        return $"{path}: 缺少必填属性 \"{name}\"。";
                }
            }
            if (schema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (!value.TryGetProperty(property.Name, out var propertyValue)) continue;
                    var error = ValidateElement(propertyValue, property.Value, $"{path}.{property.Name}");
                    if (error is not null) return error;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array &&
            schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var error = ValidateElement(item, items, $"{path}[{index}]");
                if (error is not null) return error;
                index++;
            }
        }

        return null;
    }

    private static string? CheckType(JsonElement value, string? expected, string path)
    {
        var satisfied = expected switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind is JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };
        return satisfied ? null : $"{path}: 期望类型 {expected}，实际为 {value.ValueKind}。";
    }
}
