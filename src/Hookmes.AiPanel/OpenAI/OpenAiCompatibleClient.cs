using Hookmes.AiPanel.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace Hookmes.AiPanel.OpenAI;

/// <summary>Dependency-free OpenAI-compatible chat/completions SSE client.</summary>
public sealed class OpenAiCompatibleClient : IOpenAiChatClient
{
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(HttpClient http) => _http = http;

    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/chat/completions");
    public string? ApiKey { get; set; }

    public async IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
        OpenAiChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new
            {
                role = m.Role, content = m.Content, name = m.Name, tool_call_id = m.ToolCallId,
                tool_calls = m.ToolCalls?.Select(call => new
                {
                    id = call.Id, type = "function",
                    function = new { name = call.Name, arguments = call.Arguments }
                })
            }),
            tools = request.Tools?.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.InputSchema }
            }),
            temperature = request.Temperature,
            stream = true,
            stream_options = new { include_usage = true }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var response = await _http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() : null;
            if (!choice.TryGetProperty("delta", out var delta))
            {
                if (finish is not null) yield return new ChatStreamDelta(null, null, finish);
                continue;
            }

            var content = delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            if (delta.TryGetProperty("tool_calls", out var calls))
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    var id = call.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                    string? name = null, arguments = null;
                    if (call.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var n)) name = n.GetString();
                        if (function.TryGetProperty("arguments", out var a)) arguments = a.GetString();
                    }
                    yield return new ChatStreamDelta(content, new ToolCallDelta(index, id, name, arguments), finish);
                    content = null;
                }
            }
            else if (content is not null || finish is not null)
            {
                yield return new ChatStreamDelta(content, null, finish);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
