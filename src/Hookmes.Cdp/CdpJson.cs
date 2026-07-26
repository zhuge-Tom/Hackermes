using System;
using System.Text.Json;

namespace Hookmes.Cdp;

/// <summary>
/// CDP 负载的轻量读取。
/// <para>
/// 刻意不为每个域的每个事件建强类型模型:CDP 的形状随 Chromium 版本演进,
/// 强类型化的维护成本远高于收益,而绝大多数场景只需要取其中一两个字段。
/// 需要完整负载时,调用方直接拿原始 JSON 自己解析。
/// </para>
/// </summary>
public static class CdpJson
{
    /// <summary>把对象序列化成 CDP 参数 JSON。</summary>
    public static string Params(params (string Key, object? Value)[] pairs)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var (key, value) in pairs)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNull(key);
                        break;
                    case string s:
                        writer.WriteString(key, s);
                        break;
                    case bool b:
                        writer.WriteBoolean(key, b);
                        break;
                    case int i:
                        writer.WriteNumber(key, i);
                        break;
                    case long l:
                        writer.WriteNumber(key, l);
                        break;
                    case double d:
                        writer.WriteNumber(key, d);
                        break;
                    default:
                        writer.WriteString(key, value.ToString());
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>按路径取字符串,例如 <c>TryGetString(json, "frame", "url")</c>。</summary>
    public static string? TryGetString(string json, params string[] path)
    {
        var element = TryGetElement(json, path);
        return element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
    }

    public static int? TryGetInt(string json, params string[] path)
    {
        var element = TryGetElement(json, path);

        if (element is not { } e)
            return null;

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(e.GetString(), out var s) => s,
            _ => null
        };
    }

    public static JsonElement? TryGetElement(string json, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;

            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                    return null;

                current = next;
            }

            // JsonDocument 释放后 element 会失效,这里克隆一份带出去。
            return current.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 <c>Page.frameNavigated</c> 取主框架 URL。
    /// 子框架(带 parentId)一律忽略 —— 广告 iframe 导航不应该改地址栏。
    /// </summary>
    public static string? ReadMainFrameUrl(string json)
    {
        var frame = TryGetElement(json, "frame");

        if (frame is not { ValueKind: JsonValueKind.Object } f)
            return null;

        if (f.TryGetProperty("parentId", out var parent)
            && parent.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(parent.GetString()))
        {
            return null;
        }

        return f.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;
    }
}
