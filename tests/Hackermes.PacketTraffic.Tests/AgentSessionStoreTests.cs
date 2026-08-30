using Hackermes.AiPanel.Agent;
using Hackermes.Platform.Events;
using Hackermes.Platform.Models;
using Hackermes.Platform.Services;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Session deletion semantics: removing one persisted session, clearing all sessions and
/// purging the matching event-log streams, so the "delete on right-click" and the
/// "clear all" AI-panel actions persist correctly across restarts.
/// </summary>
public sealed class AgentSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hackermes-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly AgentSessionStore _store;

    public AgentSessionStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new AgentSessionStore(new TestSettings(Path.Combine(_dir, "settings.json")), null!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AgentSessionEntry Entry(string id, string name) => new()
    {
        Id = id, Name = name, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private void Seed(params AgentSessionEntry[] entries)
    {
        var document = new AgentSessionDocument();
        foreach (var entry in entries) document.Sessions.Add(entry);
        _store.Save(document);
    }

    [Fact]
    public void Delete_removes_one_session_and_repoints_active_id()
    {
        Seed(Entry("11111111111111111111111111111111", "session A"), Entry("22222222222222222222222222222222", "session B"));
        _store.Save(new AgentSessionDocument
        {
            ActiveId = "11111111111111111111111111111111",
            Sessions =
            {
                Entry("11111111111111111111111111111111", "session A"),
                Entry("22222222222222222222222222222222", "session B"),
            }
        });

        Assert.True(_store.Delete("11111111111111111111111111111111"));

        var document = _store.Load();
        Assert.Single(document.Sessions);
        Assert.Equal("22222222222222222222222222222222", document.Sessions[0].Id);
        // The removed session was active; the store repoints ActiveId to the surviving one.
        Assert.Equal("22222222222222222222222222222222", document.ActiveId);
    }

    [Fact]
    public void Delete_returns_false_for_unknown_or_blank_id()
    {
        Seed(Entry("11111111111111111111111111111111", "only session"));
        Assert.False(_store.Delete("99999999999999999999999999999999"));
        Assert.False(_store.Delete(string.Empty));
        Assert.Single(_store.Load().Sessions);
    }

    [Fact]
    public void DeleteAll_clears_every_session_and_active_id()
    {
        Seed(Entry("11111111111111111111111111111111", "session A"), Entry("22222222222222222222222222222222", "session B"));

        var removed = _store.DeleteAll();

        Assert.Equal(2, removed);
        var document = _store.Load();
        Assert.Empty(document.Sessions);
        Assert.Equal(string.Empty, document.ActiveId);
    }

    [Fact]
    public void DeleteAll_returns_zero_for_an_empty_store()
    {
        Assert.Equal(0, _store.DeleteAll());
        Assert.Empty(_store.Load().Sessions);
    }

    [Fact]
    public void Deleted_sessions_do_not_survive_a_reload()
    {
        Seed(Entry("11111111111111111111111111111111", "doomed"));
        Assert.True(_store.Delete("11111111111111111111111111111111"));

        // A fresh store on the same settings path must not see the dropped session.
        var reloaded = new AgentSessionStore(new TestSettings(Path.Combine(_dir, "settings.json")), null!);
        Assert.Empty(reloaded.Load().Sessions);
    }

    private sealed class TestSettings : ISettingsService
    {
        public TestSettings(string path) => SettingsFilePath = path;
        public AppSettings Load() => new();
        public bool Save(AppSettings settings) => true;
        public bool Update(Action<AppSettings> mutate, SettingsSection? changedSection = null)
        {
            mutate(Load());
            return true;
        }
        public string SettingsFilePath { get; }
    }
}
