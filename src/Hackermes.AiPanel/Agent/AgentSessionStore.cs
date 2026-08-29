using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hackermes.AiPanel.Agent;

/// <summary>
/// Persists named AI chat sessions next to the application settings. Each session keeps its
/// own compacted summary and recent verbatim messages; tool arguments/results are excluded,
/// matching the redaction guarantees of the global memory store.
/// </summary>
public sealed class AgentSessionStore : IAgentSessionStore
{
    private const int MaxSessions = 50;
    private readonly ISettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private AgentSessionDocument? _document;

    public AgentSessionStore(ISettingsService settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger.ForCategory(nameof(AgentSessionStore));
    }

    public AgentSessionDocument Load()
    {
        lock (_gate)
        {
            if (_document is null)
            {
                try
                {
                    var path = PathFor();
                    if (File.Exists(path))
                        _document = JsonSerializer.Deserialize(File.ReadAllText(path), AgentStateJsonContext.Default.AgentSessionDocument);
                }
                catch (Exception ex) { _logger.Warn($"Unable to read Agent sessions: {ex.Message}"); }
                _document ??= new AgentSessionDocument();
                Normalize(_document);
            }
            return Clone(_document);
        }
    }

    public void Save(AgentSessionDocument document)
    {
        lock (_gate)
        {
            Normalize(document);
            _document = Clone(document);
            var path = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_document, AgentStateJsonContext.Default.AgentSessionDocument));
            File.Move(temp, path, overwrite: true);
        }
    }

    private void Normalize(AgentSessionDocument document)
    {
        document.ActiveId ??= string.Empty;
        document.Sessions ??= new List<AgentSessionEntry>();
        document.Sessions = document.Sessions.Where(session => session is not null && !string.IsNullOrWhiteSpace(session.Id))
            .Select(Normalize).OrderByDescending(session => session.UpdatedAt).Take(MaxSessions).ToList();
        if (document.Sessions.Count > 0 && document.Sessions.All(session => !string.Equals(session.Id, document.ActiveId, StringComparison.Ordinal)))
            document.ActiveId = document.Sessions[0].Id;
    }

    private static AgentSessionEntry Normalize(AgentSessionEntry input) => new()
    {
        Id = input.Id.Trim()[..Math.Min(input.Id.Trim().Length, 96)],
        Name = Limit(input.Name, 120).Length == 0 ? "新会话" : Limit(input.Name, 120),
        CreatedAt = input.CreatedAt == default ? DateTimeOffset.UtcNow : input.CreatedAt,
        UpdatedAt = input.UpdatedAt == default ? DateTimeOffset.UtcNow : input.UpdatedAt,
        Summary = Limit(input.Summary, 20_000),
        WorkspaceId = Limit(input.WorkspaceId, 96),
        RecentMessages = (input.RecentMessages ?? new List<AgentMemoryMessage>())
            .Where(message => message is not null && message.Role is "user" or "assistant")
            .Select(message => new AgentMemoryMessage { Role = message.Role, Content = Limit(message.Content, 8_000) })
            .Where(message => message.Content.Length > 0).TakeLast(64).ToList()
    };

    private static AgentSessionDocument Clone(AgentSessionDocument value) => new()
    {
        Version = value.Version,
        ActiveId = value.ActiveId,
        Sessions = value.Sessions.Select(Clone).ToList()
    };

    private static AgentSessionEntry Clone(AgentSessionEntry value) => new()
    {
        Id = value.Id, Name = value.Name, CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt, Summary = value.Summary,
        WorkspaceId = value.WorkspaceId,
        RecentMessages = value.RecentMessages.Select(message => new AgentMemoryMessage { Role = message.Role, Content = message.Content }).ToList()
    };

    private string PathFor() => Path.Combine(Path.GetDirectoryName(_settings.SettingsFilePath) ?? AppContext.BaseDirectory, "agent-sessions.json");

    private static string Limit(string? value, int max) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, max)];
}
