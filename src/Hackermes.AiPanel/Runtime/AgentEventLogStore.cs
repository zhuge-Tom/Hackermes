using Hackermes.Base.Diagnostics;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Append-only JSONL persistence for <see cref="AgentSessionEvent"/> streams — the
/// "log is truth" second stage (dsh session persistence lineage). One file per chat
/// session under the application data directory; streaming deltas are excluded (message
/// level replay fidelity), everything else survives restart so sessions can resume with
/// full tool protocol, compaction blocks and approval audits intact.
///
/// Payload serialization is a hand-written switch: no reflection, stable field names,
/// and unknown future kinds are skipped on read instead of corrupting the stream.
/// </summary>
public sealed class AgentEventLogStore
{
    private readonly Func<string> _settingsDirectoryFactory;
    private readonly IAppLogger? _logger;

    /// <summary>False once a write failed: persistence is paused for this store instance so
    /// one full disk cannot spam errors or silently drop events unnoticed.</summary>
    public bool Healthy { get; private set; } = true;

    /// <summary>Raised once, on the first write failure, with the error message.</summary>
    public event Action<string>? WriteFailed;

    public AgentEventLogStore(Func<string> settingsDirectoryFactory, IAppLogger? logger = null)
    {
        _settingsDirectoryFactory = settingsDirectoryFactory;
        _logger = logger?.ForCategory(nameof(AgentEventLogStore));
    }

    private string DirectoryFor(string sessionId)
    {
        // Session ids are 32-char lowercase hex GUIDs; anything else is rejected so the
        // path can never escape the events directory.
        if (!IsValidSessionId(sessionId)) throw new ArgumentException("Invalid agent session id.", nameof(sessionId));
        return Path.Combine(_settingsDirectoryFactory(), "agent-events");
    }

    private string PathFor(string sessionId) => Path.Combine(DirectoryFor(sessionId), sessionId + ".jsonl");

    public bool Exists(string sessionId)
    {
        return IsValidSessionId(sessionId) && File.Exists(PathFor(sessionId));
    }

    public void Append(string sessionId, AgentSessionEvent @event)
    {
        if (!Healthy) return; // paused after a failure; the UI was already notified
        // Fail fast on invalid ids (programming errors); only IO problems are contained.
        var path = PathFor(sessionId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, Serialize(@event) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Healthy = false;
            _logger?.Warn($"Agent event log write failed, persistence paused: {ex.Message}");
            var handlers = WriteFailed;
            if (handlers is null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<string>)handler)(ex.Message); }
                catch { /* listener failures never propagate */ }
            }
        }
    }

    /// <summary>Loads and re-indexes one session's events; unreadable lines are skipped.</summary>
    public IReadOnlyList<AgentSessionEvent> Load(string sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return [];
        var loaded = new List<AgentSessionEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var @event = TryDeserialize(line, loaded.Count);
            if (@event is not null) loaded.Add(@event);
        }
        return loaded;
    }

    public void Delete(string sessionId)
    {
        try
        {
            if (IsValidSessionId(sessionId))
            {
                var path = PathFor(sessionId);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to delete agent event log {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies one session's full event stream to a fresh session id (dsh fork lineage):
    /// the fork resumes with complete history, compaction blocks and audits intact, while
    /// the source session stays untouched. Returns false when there was nothing to copy.
    /// </summary>
    public bool Fork(string sourceSessionId, string forkedSessionId)
    {
        if (!IsValidSessionId(sourceSessionId) || !IsValidSessionId(forkedSessionId)) return false;
        if (!Exists(sourceSessionId)) return false;
        try
        {
            var target = PathFor(forkedSessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(PathFor(sourceSessionId), target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to fork agent event log {sourceSessionId} → {forkedSessionId}: {ex.Message}");
            return false;
        }
    }

    private static bool IsValidSessionId(string sessionId) =>
        sessionId.Length == 32 && sessionId.All(Uri.IsHexDigit);

    #region Serialization

    internal static string Serialize(AgentSessionEvent @event)
    {
        var builder = new StringBuilder(256);
        builder.Append("{\"kind\":\"").Append(@event.Kind.ToString()).Append('"');
        builder.Append(",\"turn\":").Append(@event.Turn.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"step\":").Append(@event.Step.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"time\":").Append(@event.Time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"data\":").Append(SerializeData(@event.Data));
        builder.Append('}');
        return builder.ToString();
    }

    private static AgentSessionEvent? TryDeserialize(string line, int seq)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement)) return null;
            var kind = Enum.TryParse<AgentEventKind>(kindElement.GetString(), out var parsedKind)
                ? parsedKind : (AgentEventKind?)null;
            if (kind is null) return null;
            var turn = root.TryGetProperty("turn", out var turnElement) ? turnElement.GetInt32() : 0;
            var step = root.TryGetProperty("step", out var stepElement) ? stepElement.GetInt32() : 0;
            var time = root.TryGetProperty("time", out var timeElement)
                ? DateTimeOffset.FromUnixTimeMilliseconds(timeElement.GetInt64())
                : DateTimeOffset.UtcNow;
            var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
                ? DeserializeData(kind.Value, dataElement)
                : null;
            if (data is null) return null;
            return new AgentSessionEvent(seq, time, kind.Value, turn, step, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeData(AgentEventData data) => data switch
    {
        TurnStarted value => Props($"\"turn\":{value.Turn}"),
        StepStarted value => Props($"\"turn\":{value.Turn},\"step\":{value.Step}"),
        StepFinished value => Props($"\"turn\":{value.Turn},\"step\":{value.Step}"),
        UserMessageReceived value => Props(
            $"\"text\":{Json(value.Text)},\"steered\":{(value.Steered ? "true" : "false")}," +
            $"\"priority\":{(value.Priority ? "true" : "false")},\"injected\":{(value.Injected ? "true" : "false")}"),
        AssistantReply value => Props(
            $"\"content\":{Json(value.Content)},\"hasToolCalls\":{(value.HasToolCalls ? "true" : "false")}," +
            $"\"isFinalReport\":{(value.IsFinalReport ? "true" : "false")}"),
        ToolCallRequested value => Props(
            $"\"callId\":{Json(value.CallId)},\"name\":{Json(value.Name)},\"args\":{Json(value.ArgumentsJson)}"),
        ToolCallCompleted value => Props(
            $"\"callId\":{Json(value.CallId)},\"name\":{Json(value.Name)}," +
            $"\"success\":{(value.Success ? "true" : "false")},\"content\":{Json(value.Content)}"),
        UsageRecorded value => Props(
            $"\"p\":{value.Usage.PromptTokens},\"c\":{value.Usage.CompletionTokens}," +
            $"\"t\":{value.Usage.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "null"}"),
        RequestRetried value => Props(
            $"\"attempt\":{value.Attempt},\"max\":{value.MaxAttempts}," +
            $"\"error\":{Json(value.Error)},\"delayMs\":{(long)value.Delay.TotalMilliseconds}"),
        ContextCompacted value => Props(
            $"\"reclaimed\":{value.ReclaimedChars},\"range\":{Json(value.Range)}," +
            $"\"automatic\":{(value.Automatic ? "true" : "false")}," +
            $"\"warning\":{(value.Warning is null ? "null" : Json(value.Warning))}," +
            $"\"summary\":{(value.Summary is null ? "null" : Json(value.Summary))}"),
        ApprovalAudited value => Props(
            $"\"time\":{value.Record.Time.ToUnixTimeMilliseconds()}," +
            $"\"tool\":{Json(value.Record.Tool)}," +
                            $"\"session\":{(value.Record.SessionId is null ? "null" : Json(value.Record.SessionId))}," +
            $"\"page\":{(value.Record.PageId is null ? "null" : Json(value.Record.PageId))}," +
            $"\"decision\":{Json(value.Record.Decision)}," +
            $"\"reason\":{Json(value.Record.Reason)}"),
        RequestHeaderLogged value => Props(
            $"\"fingerprint\":{Json(value.Fingerprint)},\"headerReason\":{Json(value.Reason)}," +
            $"\"model\":{Json(value.Model)},\"tools\":{value.ToolCount}"),
        TurnEnded value => Props(
            $"\"reason\":\"{value.Reason}\"," +
            $"\"detail\":{(value.Detail is null ? "null" : Json(value.Detail))}"),
        _ => "{}",
    };

    private static AgentEventData? DeserializeData(AgentEventKind kind, JsonElement root)
    {
        string Text(string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty : string.Empty;
        string? TextOrNull(string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() : null;
        int Number(string name, int fallback = 0) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
                ? element.GetInt32() : fallback;
        long LongNumber(string name, long fallback = 0) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
                ? element.GetInt64() : fallback;
        bool Flag(string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;

        return kind switch
        {
            AgentEventKind.TurnStart => new TurnStarted(Number("turn")),
            AgentEventKind.StepStart => new StepStarted(Number("turn"), Number("step")),
            AgentEventKind.StepEnd => new StepFinished(Number("turn"), Number("step")),
            AgentEventKind.UserMessage => new UserMessageReceived(
                Text("text"), Flag("steered"), Flag("priority"), Flag("injected")),
            AgentEventKind.AssistantMessage => new AssistantReply(
                Text("content"), Flag("hasToolCalls"), Flag("isFinalReport")),
            AgentEventKind.ToolCall => new ToolCallRequested(Text("callId"), Text("name"), Text("args")),
            AgentEventKind.ToolResult => new ToolCallCompleted(
                Text("callId"), Text("name"), Flag("success"), Text("content")),
            AgentEventKind.Usage => new UsageRecorded(new StreamUsage(
                Number("p"), Number("c"),
                root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null)),
            AgentEventKind.RequestRetry => new RequestRetried(
                Number("attempt"), Number("max"), Text("error"),
                TimeSpan.FromMilliseconds(LongNumber("delayMs"))),
            AgentEventKind.ContextCompacted => new ContextCompacted(
                LongNumber("reclaimed"), Text("range"), Flag("automatic"),
                TextOrNull("warning"), TextOrNull("summary")),
            AgentEventKind.ApprovalAudited => new ApprovalAudited(new AiToolAuditRecord(
                DateTimeOffset.FromUnixTimeMilliseconds(LongNumber("time")),
                Text("tool"), TextOrNull("session"), TextOrNull("page"),
                Text("decision"), Text("reason"))),
            AgentEventKind.RequestHeader => new RequestHeaderLogged(
                Text("fingerprint"), Text("headerReason"), Text("model"), Number("tools")),
            AgentEventKind.TurnEnd => new TurnEnded(
                Enum.TryParse<TurnEndReasonWire>(Text("reason"), ignoreCase: true, out var wire)
                    ? Map(wire) : AgentTurnEndReason.Completed,
                TextOrNull("detail")),
            _ => null,
        };
    }

    private enum TurnEndReasonWire { Completed, Aborted, Blocked, Error, LengthCapped, MaxRounds }

    private static AgentTurnEndReason Map(TurnEndReasonWire wire) => wire switch
    {
        TurnEndReasonWire.Aborted => AgentTurnEndReason.Aborted,
        TurnEndReasonWire.Blocked => AgentTurnEndReason.Blocked,
        TurnEndReasonWire.Error => AgentTurnEndReason.Error,
        TurnEndReasonWire.LengthCapped => AgentTurnEndReason.LengthCapped,
        TurnEndReasonWire.MaxRounds => AgentTurnEndReason.MaxRounds,
        _ => AgentTurnEndReason.Completed,
    };

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Props(string fields) => "{" + fields + "}";

    #endregion
}
