using Hackermes.AiPanel.OpenAI;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// LLM session titles (dsh session-title provider lineage): one tiny auxiliary call names
/// the conversation from its opening exchange. Falls back to the caller on any failure —
/// naming is cosmetic and must never block or fail a session.
/// </summary>
public static class AgentSessionTitleMaker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public static async Task<string?> SuggestAsync(
        IOpenAiChatClient client,
        string model,
        string firstUserMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessage)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DefaultTimeout);
            var messages = new[]
            {
                new ChatMessage("user",
                    "为下面的对话起一个不超过 12 个字的中文标题。只输出标题本身，不要引号、句号或任何解释。\n\n" +
                    "用户：" + firstUserMessage.Trim()[..Math.Min(firstUserMessage.Trim().Length, 600)]),
            };
            StringBuilder title = new();
            await foreach (var delta in client.StreamChatAsync(
                               new OpenAiChatRequest(model, messages, Tools: null), timeout.Token).ConfigureAwait(true))
            {
                if (delta.Content is { } text) title.Append(text);
            }
            return Normalize(title.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout: fall back silently
        }
        catch (Exception)
        {
            return null; // any provider failure: fall back silently
        }
    }

    /// <summary>Strips quotes/punctuation padding and clamps to a sane display length.</summary>
    public static string Normalize(string rawTitle)
    {
        var trimmed = rawTitle.Trim().Trim('"', '“', '”', '\'', '「', '」', '。', '.');
        if (trimmed.Length == 0) return string.Empty;
        // Cut at the first newline (models sometimes add commentary).
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        if (newline > 0) trimmed = trimmed[..newline].TrimEnd();
        return trimmed.Length <= 20 ? trimmed : trimmed[..20];
    }
}
