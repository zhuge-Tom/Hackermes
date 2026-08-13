using Hackermes.Browser.ViewModels;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;

namespace Hackermes.Browser.Services;

/// <summary>
/// Browser-owned implementation of the platform page-context contract. The map is
/// keyed with ordinal comparison so similarly named tabs can never alias each other.
/// </summary>
public sealed class BrowserPageContextService : IPageContextQueryService
{
    private readonly Dictionary<string, BrowserTabViewModel> _pages = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Track(BrowserTabViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (_gate)
        {
            _pages[page.PageId] = page;
        }
    }

    public void Untrack(string pageId)
    {
        lock (_gate)
        {
            _pages.Remove(pageId);
        }
    }

    public PageContextObservation? Read(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            return null;

        lock (_gate)
        {
            if (!_pages.TryGetValue(pageId, out var page))
                return null;

            return new PageContextObservation(
                page.PageId,
                page.CurrentUrl,
                page.Title,
                page.IsCdpReady,
                page.IsAgentReady);
        }
    }
}
