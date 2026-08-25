using System;
using System.IO;
using System.Net.Http;

namespace Hackermes.AiPanel.Runtime;

/// <summary>Retryability of a model-request failure (dsh LlmFailure-code lineage).</summary>
public enum AgentRequestFailureKind
{
    /// <summary>Worth retrying: transport resets, timeouts, 408/429/5xx responses.</summary>
    Transient,

    /// <summary>Pointless to retry unchanged: auth, bad request, not found, unknown failures.</summary>
    Terminal,
}

/// <summary>
/// Classifies exceptions thrown while streaming a model request. The SSE client surfaces
/// terminal HTTP statuses as <see cref="HttpRequestException"/> whose message starts with
/// "HTTP &lt;code&gt;" (see OpenAiCompatibleClient.CreateHttpErrorAsync); connection-level
/// failures carry no such prefix. Only explicitly transient shapes may be retried — an
/// unknown exception type fails the turn instead of silently burning retries.
/// </summary>
public static class AgentRequestError
{
    private static readonly int[] RetryableStatusCodes = [408, 429, 500, 502, 503, 504];

    public static AgentRequestFailureKind Classify(Exception exception)
    {
        if (exception is HttpRequestException http)
        {
            // No "HTTP <code>" prefix means the request never completed (socket/DNS/TLS).
            var prefix = "HTTP ";
            var message = http.Message;
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) return AgentRequestFailureKind.Transient;
            var digits = 0;
            while (prefix.Length + digits < message.Length && char.IsDigit(message[prefix.Length + digits])) digits++;
            if (digits is < 3 or > 3) return AgentRequestFailureKind.Terminal;
            if (!int.TryParse(message.AsSpan(prefix.Length, digits), out var code)) return AgentRequestFailureKind.Terminal;
            return Array.IndexOf(RetryableStatusCodes, code) >= 0
                ? AgentRequestFailureKind.Transient
                : AgentRequestFailureKind.Terminal;
        }

        // Broken streams and server-side cancellations (HttpClient timeout surfaces as
        // TaskCanceledException without the operator's token being cancelled).
        return exception is IOException || exception is ObjectDisposedException
            ? AgentRequestFailureKind.Transient
            : AgentRequestFailureKind.Terminal;
    }

    public static bool IsTransient(Exception exception) =>
        Classify(exception) == AgentRequestFailureKind.Transient;

    private static readonly string[] OverflowMarkers =
    [
        "context length",
        "context window",
        "maximum context",
        "too many tokens",
        "input tokens exceed",
        "tokens exceed",
        "reduce the length",
        "prompt is too long",
        "request too large",
    ];

    /// <summary>
    /// True when a terminal HTTP 400/413 failure is actually "the conversation no longer
    /// fits the model's window". Detection is marker-based over the response body excerpt
    /// that CreateHttpErrorAsync embeds in the message; OpenAI-compatible providers phrase
    /// this inconsistently, so the list is deliberately broad.
    /// </summary>
    public static bool IsContextOverflow(Exception exception)
    {
        if (exception is not HttpRequestException http) return false;
        var message = http.Message;
        if (!(message.StartsWith("HTTP 400", StringComparison.Ordinal) ||
              message.StartsWith("HTTP 413", StringComparison.Ordinal))) return false;
        foreach (var marker in OverflowMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
