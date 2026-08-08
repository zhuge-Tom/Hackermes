using System;
using System.Collections.Generic;

namespace Hackermes.AiPanel.OpenAI;

public sealed record AiProviderPreset(string Id, string Name, string Endpoint, string DefaultModel);

public static class AiProviderPresets
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-5-mini"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4.1-mini"),
        new("siliconflow", "SiliconFlow", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3"),
        new("dashscope", "阿里云百炼（兼容模式）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        new("moonshot", "Moonshot/Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        new("ollama", "Ollama（本地）", "http://localhost:11434/v1", "qwen2.5:7b"),
        new("custom", "自定义 OpenAI-compatible", "http://localhost:8000/v1", "model-name")
    ];

    public static Uri ResolveChatEndpoint(string endpoint)
    {
        var value = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("API Endpoint 必须是绝对 HTTP(S) 地址。", nameof(endpoint));
        if (!value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            value += "/chat/completions";
        return new Uri(value, UriKind.Absolute);
    }
}
