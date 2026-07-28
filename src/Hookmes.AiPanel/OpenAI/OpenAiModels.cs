using Hookmes.AiPanel.Tools;
using System.Collections.Generic;
using System.Text.Json;

namespace Hookmes.AiPanel.OpenAI;

public sealed record AssistantToolCall(string Id, string Name, string Arguments);

public sealed record ChatMessage(
    string Role,
    string? Content,
    string? Name = null,
    string? ToolCallId = null,
    IReadOnlyList<AssistantToolCall>? ToolCalls = null);

public sealed record OpenAiChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AiToolDefinition>? Tools = null,
    double? Temperature = null);

public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? Arguments);

public sealed record ChatStreamDelta(string? Content, ToolCallDelta? ToolCall, string? FinishReason);

public interface IOpenAiChatClient
{
    IAsyncEnumerable<ChatStreamDelta> StreamChatAsync(
        OpenAiChatRequest request, System.Threading.CancellationToken ct = default);
}
