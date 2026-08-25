using Hackermes.AiPanel.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.OpenAI;

/// <summary>Dependency-free OpenAI-compatible chat/completions SSE client.</summary>
public sealed class OpenAiCompatibleClient : IOpenAiChatClient
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly int _maxRetries;
    private readonly Func<int, TimeSpan> _backoff;

    public OpenAiCompatibleClient(HttpClient http)
        : this(http, maxRetries: 2, backoff: DefaultBackoff)
    {
    }

    /// <summary>Retries transient request failures (429/5xx/connection errors) before any stream content is consumed.</summary>
    public OpenAiCompatibleClient(HttpClient http, int maxRetries, Func<int, TimeSpan>? backoff)
    {
        _http = http;
        if (maxRetries is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Retry count must be between 0 and 5.");
        _maxRetries = maxRetries;
        _backoff = backoff ?? DefaultBackoff;
    }

    private static TimeSpan DefaultBackoff(int attempt) => TimeSpan.FromSeconds(attempt == 0 ? 1 : 3);

    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/chat/completions");
    public string? ApiKey { get; set; }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        Uri modelsEndpoint,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modelsEndpoint);
        using var message = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("服务已响应，但 /models 没有返回 OpenAI 兼容的 data 列表。");

        return data.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                           item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("id").GetString() ?? string.Empty)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Performs a real, bounded chat-completions request against an unsaved configuration.
    /// This validates the URL, credential and model together without mutating the live client.
    /// </summary>
    public async Task TestConnectionAsync(Uri endpoint, string model, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("模型名称不能为空。", nameof(model));

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = model.Trim(),
                messages = new[] { new { role = "user", content = "Reply with OK." } },
                stream = false
            }, options: JsonOptions)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        throw await CreateHttpErrorAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<HttpRequestException> CreateHttpErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        detail = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 240) detail = detail[..240] + "…";
        return new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                                        (detail.Length == 0 ? string.Empty : $"：{detail}"));
    }

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

        var attempt = 0;
        HttpResponseMessage? response = null;
        try
        {
            while (true)
            {
                response?.Dispose();
                response = null;
                using var message = CreateRequest(body);
                try
                {
                    response = await _http.SendAsync(
                        message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < _maxRetries && !ct.IsCancellationRequested)
                {
                    attempt++;
                    await Task.Delay(_backoff(attempt - 1), ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode && IsRetryableStatus(response.StatusCode) &&
                    attempt < _maxRetries && !ct.IsCancellationRequested)
                {
                    attempt++;
                    var delay = ResolveDelay(response, attempt);
                    response.Dispose();
                    response = null;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw await CreateHttpErrorAsync(response, ct).ConfigureAwait(false);
                break;
            }

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
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    yield return new ChatStreamDelta(null, null, null, ReadUsage(usage));
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
                // Reasoning-model thinking stream (DeepSeek-R1 style): emitted as standalone
                // deltas so it can be rendered live but never enters model history.
                var reasoning =
                    (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
                        ? rc.GetString() : null) ??
                    (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() : null);
                if (!string.IsNullOrEmpty(reasoning))
                    yield return new ChatStreamDelta(null, null, finish, Reasoning: reasoning);
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
        finally { response?.Dispose(); }
    }

    private HttpRequestMessage CreateRequest(object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return message;
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private TimeSpan ResolveDelay(HttpResponseMessage response, int attempt)
    {
        var delay = _backoff(attempt - 1);
        if (response.Headers.RetryAfter?.Delta is { } hint && hint > delay)
            return hint > MaxRetryDelay ? MaxRetryDelay : hint;
        return delay;
    }

    private static StreamUsage ReadUsage(JsonElement usage) => new(
        usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.ValueKind == JsonValueKind.Number
            ? prompt.GetInt32() : 0,
        usage.TryGetProperty("completion_tokens", out var completion) && completion.ValueKind == JsonValueKind.Number
            ? completion.GetInt32() : 0,
        usage.TryGetProperty("total_tokens", out var total) && total.ValueKind == JsonValueKind.Number
            ? total.GetInt32() : null);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
