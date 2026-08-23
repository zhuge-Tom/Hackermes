using System;
using System.Text;
using System.Text.Json;

namespace Hackermes.Inspector.Services;

/// <summary>
/// �?CDP 抓到的请�?响应要素渲染成类 Burp Suite 的原始报文文�?
/// 请求�?状态行 + 头部�?+ 空行 + 正文,便于直接阅读与比对�?/// </summary>
public static class HttpPacketFormatter
{
    /// <summary>详情面板单个报文的最大展示长�?超出截断并标注�?/summary>
    public const int MaximumDisplayCharacters = 64 * 1024;

    public static string FormatRequest(string method, string url, string? headersJson, string? body)
    {
        var builder = new StringBuilder();
        builder.Append(method.ToUpperInvariant()).Append(' ').Append(RequestPath(url)).AppendLine(" HTTP/1.1");
        builder.Append("# ").AppendLine(url);
        AppendHeaderBlock(builder, headersJson);
        AppendBody(builder, body);
        return Limit(builder.ToString());
    }

    public static string FormatResponse(int status, string statusText, string url, string? headersJson, string? body)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status <= 0 ? "-" : status.ToString())
            .Append(' ').AppendLine(string.IsNullOrWhiteSpace(statusText) ? string.Empty : statusText.Trim());
        if (!string.IsNullOrWhiteSpace(url))
            builder.Append("# ").AppendLine(url);
        AppendHeaderBlock(builder, headersJson);
        AppendBody(builder, body);
        return Limit(builder.ToString());
    }

    private static void AppendHeaderBlock(StringBuilder builder, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            builder.AppendLine("(无头部信�?");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(headersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                builder.AppendLine("(无头部信�?");
                return;
            }

            foreach (var header in document.RootElement.EnumerateObject())
            {
                var value = header.Value.ValueKind == JsonValueKind.String
                    ? header.Value.GetString()
                    : header.Value.ToString();
                builder.Append(header.Name).Append(": ").AppendLine(value ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            builder.Append("(头部解析失败)").AppendLine();
        }
    }

    private static void AppendBody(StringBuilder builder, string? body)
    {
        if (string.IsNullOrEmpty(body)) return;
        builder.AppendLine().AppendLine(body);
    }

    private static string RequestPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var path = uri.PathAndQuery;
        return string.IsNullOrEmpty(path) || path == "/" && !url.EndsWith('/') ? "/" : path;
    }

    private static string Limit(string value) =>
        value.Length <= MaximumDisplayCharacters
            ? value
            : value[..MaximumDisplayCharacters] + $"\n…[内容�?{value.Length} 字符，仅显示�?{MaximumDisplayCharacters}]";
}
