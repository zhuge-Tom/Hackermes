using System;
using System.Collections.Generic;
using System.Linq;

namespace Hookmes.Platform.Registries;

public sealed class DockLayoutRegistry : IDockLayoutRegistry
{
    private readonly List<DockTabRegistration> _registrations = new();
    private readonly HashSet<string> _tabIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void RegisterTab(DockTabRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        lock (_gate)
        {
            if (!_tabIds.Add(registration.TabId))
                throw new InvalidOperationException(
                    $"Tab 标识重复: '{registration.TabId}'。每个 Tab 在全局必须唯一。");

            _registrations.Add(registration);
        }
    }

    public IReadOnlyList<DockTabRegistration> GetRegistrations()
    {
        lock (_gate)
        {
            return _registrations.ToArray();
        }
    }

    public IReadOnlyList<DockTabRegistration> GetRegistrationsForRegion(DockPosition region)
    {
        lock (_gate)
        {
            return _registrations
                .Where(r => r.Region == region)
                .OrderBy(r => r.Order)
                .ToArray();
        }
    }
}

public sealed class MenuRegistry : IMenuRegistry
{
    private readonly List<MenuItemEntry> _items = new();
    private readonly object _gate = new();

    public void Register(MenuItemEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _items.Add(entry);
        }
    }

    public IReadOnlyList<MenuItemEntry> GetItems()
    {
        lock (_gate)
        {
            return _items
                .OrderBy(i => i.MenuGroupOrder)
                .ThenBy(i => i.Order)
                .ToArray();
        }
    }

    public IReadOnlyList<MenuItemEntry> GetItemsForMenu(string menuPath)
    {
        lock (_gate)
        {
            return _items
                .Where(i => string.Equals(i.MenuPath, menuPath, StringComparison.Ordinal))
                .OrderBy(i => i.MenuGroupOrder)
                .ThenBy(i => i.Order)
                .ToArray();
        }
    }
}

public sealed class SettingsRegistry : ISettingsRegistry
{
    private readonly List<SettingsPageEntry> _pages = new();
    private readonly object _gate = new();

    public void Register(SettingsPageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _pages.Add(entry);
        }
    }

    public IReadOnlyList<SettingsPageEntry> GetPages()
    {
        lock (_gate)
        {
            return _pages.OrderBy(p => p.Order).ToArray();
        }
    }
}
