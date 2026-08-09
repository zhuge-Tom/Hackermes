using System;
using System.Collections.Generic;

namespace Hackermes.AiPanel.OpenAI;

public sealed record AiProviderPreset(string Id, string Name, string Endpoint, string ChatPath, string DefaultModel)
{
    public override string ToString() => Name;
}

public static class AiProviderPresets
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", "/chat/completions", "gpt-5-mini"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "/chat/completions", "deepseek-chat"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "/chat/completions", "openai/gpt-4.1-mini"),
        new("custom", "自定义（OpenAI 兼容）", "", "/chat/completions", "")
    ];

    public static Uri ResolveBaseEndpoint(string endpoint)
    {
        var value = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("API Endpoint 必须是绝对 HTTP(S) 地址。", nameof(endpoint));
        return uri;
    }

    public static Uri ResolveChatEndpoint(string endpoint, string? chatPath = null)
    {
        var value = endpoint.Trim().TrimEnd('/');
        _ = ResolveBaseEndpoint(value);

        // Compatibility with settings written before the route became a separate field.
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(value, UriKind.Absolute);

        var route = string.IsNullOrWhiteSpace(chatPath) ? "/chat/completions" : chatPath.Trim();
        if (Uri.TryCreate(route, UriKind.Absolute, out _))
            throw new ArgumentException("Chat 接口必须是相对于 Base URL 的路径。", nameof(chatPath));
        route = "/" + route.TrimStart('/');
        if (route.Contains('#') || route.Contains('?'))
            throw new ArgumentException("Chat 接口不能包含查询参数或片段。", nameof(chatPath));
        value += route;
        return new Uri(value, UriKind.Absolute);
    }

    public static Uri ResolveModelsEndpoint(string endpoint)
    {
        var value = ResolveBaseEndpoint(endpoint).AbsoluteUri.TrimEnd('/');
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            value = value[..^"/chat/completions".Length].TrimEnd('/');
        return new Uri(value + "/models", UriKind.Absolute);
    }
}
