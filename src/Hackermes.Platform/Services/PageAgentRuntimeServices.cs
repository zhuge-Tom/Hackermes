using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Platform.Services;

/// <summary>Describes whether one Page Agent world can currently serve browser-owned operations.</summary>
public enum PageAgentWorldState
{
    Unavailable,
    Ready,
    Degraded
}

/// <summary>
/// A page-exact capability snapshot. An isolated-world degradation never implies that
/// the main-world observation hooks are unavailable.
/// </summary>
public sealed record PageAgentRuntimeCapability(
    string PageId,
    PageAgentWorldState MainWorld,
    PageAgentWorldState IsolatedWorld,
    string? Detail = null);

/// <summary>
/// Browser-owned Page Agent runtime seam. Consumers may query an exact page and execute
/// an expression only in that page's named isolated world; implementations must never
/// fall back to a different page or to the main world.
/// </summary>
public interface IPageAgentRuntime
{
    PageAgentRuntimeCapability GetCapability(string pageId);

    Task<string> EvaluateInIsolatedWorldAsync(
        string pageId,
        string expression,
        CancellationToken cancellationToken = default);
}
