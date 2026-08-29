using System.Collections.Generic;

namespace Hackermes.Platform.Services;

/// <summary>
/// Opens embedded-browser tabs without AiPanel taking a Browser project reference.
/// Implementations must create the tab on the UI thread and select it.
/// </summary>
public interface IBrowserTabOpener
{
    IReadOnlyList<string> OpenPageIds { get; }

    /// <summary>Create a tab, select it, and navigate to <paramref name="url"/> (or the homepage). Returns the page id.</summary>
    string OpenTab(string? url = null);
}
