using Hackermes.Automation.Model;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Timeline;

/// <summary>Versioned JSON persistence for replay scripts and complete timelines.</summary>
public sealed class ActionPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveScriptAsync(string path, IReadOnlyList<ActionDescriptor> actions, CancellationToken ct = default)
    {
        EnsureParent(path);
        await using var stream = File.Create(Path.GetFullPath(path));
        await JsonSerializer.SerializeAsync(stream, new ActionScriptDocument(1, actions), JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActionDescriptor>> LoadScriptAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var document = await JsonSerializer.DeserializeAsync<ActionScriptDocument>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Invalid action script JSON.");
        if (document.Version != 1) throw new InvalidDataException($"Unsupported action script version: {document.Version}.");
        return document.Actions ?? [];
    }

    public async Task SaveTimelineAsync(string path, IReadOnlyList<ActionTimelineEntry> entries, CancellationToken ct = default)
    {
        EnsureParent(path);
        await using var stream = File.Create(Path.GetFullPath(path));
        await JsonSerializer.SerializeAsync(stream, new ActionTimelineDocument(1, entries), JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActionTimelineEntry>> LoadTimelineAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var document = await JsonSerializer.DeserializeAsync<ActionTimelineDocument>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Invalid timeline JSON.");
        if (document.Version != 1) throw new InvalidDataException($"Unsupported timeline version: {document.Version}.");
        return document.Entries ?? [];
    }

    private static void EnsureParent(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private sealed record ActionScriptDocument(int Version, IReadOnlyList<ActionDescriptor>? Actions);
    private sealed record ActionTimelineDocument(int Version, IReadOnlyList<ActionTimelineEntry>? Entries);
}
