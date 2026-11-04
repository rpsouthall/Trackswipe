using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml.Controls;

namespace Anchor;

/// <summary>
/// Moving a pinned item from one dock to another. Partial of <see cref="DockWindow"/>.
/// <para>
/// With several docks on screen the only way to re-home an item used to be to remove it here and
/// add it again there, which loses its custom icon, its arguments and its shortcut along the way.
/// This moves the <see cref="DockItem"/> itself, so everything on it comes too.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>
    /// The <c>Move to dock ▸</c> submenu: every <em>other</em> dock. Disabled outright when this
    /// is the only dock, rather than hidden — a menu whose entries come and go as you add a second
    /// dock is harder to learn than one that is simply greyed out until it applies.
    /// </summary>
    private MenuFlyoutSubItem BuildMoveToDockMenu(DockItem item)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get("Menu.MoveToDock") };

        foreach (var target in _manager.Docks)
        {
            if (ReferenceEquals(target, this))
                continue;

            var entry = new MenuFlyoutItem { Text = _manager.LabelFor(target.Profile) };
            var captured = target;
            entry.Click += (_, _) => MoveItemToDock(item, captured);
            sub.Items.Add(entry);
        }

        sub.IsEnabled = sub.Items.Count > 0;
        return sub;
    }

    /// <summary>
    /// Hands an item over to another dock: off this strip, onto the end of that one, and both
    /// docks re-laid out and saved once. The item object itself moves, so its icon, arguments,
    /// custom glyph and per-item shortcut travel with it.
    /// </summary>
    public void MoveItemToDock(DockItem item, DockWindow target)
    {
        if (ReferenceEquals(target, this) || !_profile.Items.Contains(item))
            return;

        _profile.Items.Remove(item);
        Items.Remove(item);
        QueueRelayout();

        target.AdoptItem(item);
        SaveConfig();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Takes an item another dock has just released. Separate from <see cref="AddDockItem"/>
    /// because that one saves and re-announces on its own — doing it twice for one move would
    /// have the Settings list rebuild against a config in which the item exists on neither dock
    /// (it is off the first and not yet on the second) or on both.
    /// </summary>
    internal void AdoptItem(DockItem item)
    {
        _profile.Items.Add(item);
        if (!item.Hidden)
            Items.Insert(PinnedCount, item); // ahead of the open-apps section
        item.RefreshMetrics();
        QueueRelayout();
    }
}
