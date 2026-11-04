using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Anchor;

// The macOS Dock's section of open apps: apps that aren't pinned appear after a divider at the end
// of the strip while they have a window open, and leave when they close.
//
// They live at the tail of the visible collection (Items) and nowhere else — never in the profile,
// so never saved — which lets the strip's sizing, hover, magnification and tooltips treat them like
// any other icon. Everything that edits the pinned part keeps to the slots before PinnedCount, so
// the section always stays the tail. The DockManager decides what is in it (see
// DockManager.ApplyRunningState); this side only mirrors that onto the strip.
public sealed partial class DockWindow
{
    /// <summary>The section as the manager last described it, divider not included.</summary>
    private IReadOnlyList<DockItem> _openApps = Array.Empty<DockItem>();

    /// <summary>The divider between the pinned icons and the open apps, made once and reused.</summary>
    private DockItem? _openAppsDivider;

    /// <summary>A section update arrived mid-gesture and is waiting for it to end.</summary>
    private bool _openAppsPending;

    /// <summary>While an open app is dragged: the pinned slot it would be kept at if let go now, or -1.</summary>
    private int _pinSlot = -1;

    /// <summary>While an open app is dragged: where it sat in the section, to go back to.</summary>
    private int _openAppHome = -1;

    /// <summary>The visible slots before the open-apps section: pinned icons, and a drop shell if one is open.</summary>
    private int PinnedCount
    {
        get
        {
            int n = 0;
            foreach (var item in Items)
                if (!item.IsTransient)
                    n++;
            return n;
        }
    }

    /// <summary>Called by the manager whenever the set of open, unpinned apps changes; empty on all but the first dock.</summary>
    internal void SetOpenApps(IReadOnlyList<DockItem> apps)
    {
        _openApps = apps;
        // Mid-gesture the strip's order belongs to the gesture (an icon is held, or a drop shell is
        // open); the section catches up as it ends rather than shuffling cells under the cursor.
        if (_reorderItem is not null || _dropShell is not null)
        {
            _openAppsPending = true;
            return;
        }
        SyncOpenApps();
    }

    private void CatchUpOpenApps()
    {
        if (!_openAppsPending || _reorderItem is not null || _dropShell is not null)
            return;
        _openAppsPending = false;
        SyncOpenApps();
    }

    /// <summary>
    /// Makes the strip's tail match <see cref="_openApps"/> with as few moves as it can, so an
    /// app that stays open keeps its cell (and its hover and magnification state) while others
    /// come and go around it.
    /// </summary>
    private void SyncOpenApps()
    {
        _openAppsPending = false;
        var wanted = new List<DockItem>(_openApps.Count + 1);
        if (_openApps.Count > 0)
        {
            // A divider only between something and something: with nothing pinned, the open apps
            // are the whole strip.
            if (Items.Any(item => !item.IsTransient && !item.IsPlaceholder))
            {
                _openAppsDivider ??= DockItem.CreateOpenAppsDivider();
                wanted.Add(_openAppsDivider);
            }
            wanted.AddRange(_openApps);
        }

        int start = PinnedCount;
        bool changed = false;
        for (int k = 0; k < wanted.Count; k++)
        {
            int at = start + k;
            var item = wanted[k];
            if (at < Items.Count && ReferenceEquals(Items[at], item))
                continue;

            int existing = Items.IndexOf(item);
            if (existing > at)
            {
                Items.Move(existing, at);
            }
            else
            {
                if (existing >= 0)
                    Items.RemoveAt(existing);
                item.RefreshMetrics();
                Items.Insert(at, item);
                if (item.IconImage is null && !item.IsSeparator)
                    _ = LoadOneIconAsync(item);
            }
            changed = true;
        }
        while (Items.Count > start + wanted.Count)
        {
            Items.RemoveAt(Items.Count - 1);
            changed = true;
        }

        if (changed)
            QueueRelayout();
    }

    /// <summary>The open apps aren't in the profile, so density and label changes reach them here.</summary>
    private void RefreshOpenAppMetrics()
    {
        foreach (var item in Items)
            if (item.IsTransient)
                item.RefreshMetrics();
    }

    /// <summary>An open app's menu: bring it forward, keep it in the dock, find its file, or quit it.</summary>
    private void ShowOpenAppMenu(FrameworkElement target, DockItem item, ContextRequestedEventArgs e)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Mi(Loc.Get("Menu.Open"), () => LaunchOrFocus(item)));
        menu.Items.Add(Mi(Loc.Get("Menu.KeepInDock"), () => _manager.KeepInDock(this, item, PinnedCount)));
        if (Launcher.SupportsFileLocation(item))
            menu.Items.Add(Mi(Loc.Get("Menu.OpenFileLocation"), () => Launcher.OpenFileLocation(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Mi(Loc.Get("Menu.QuitApp"), () => _manager.Running.Quit(item)));

        // Held open over the menu as the item menu is, so the dock doesn't slide away mid-click.
        PauseAutoHideForDrag();
        menu.Closed += (_, _) => ResumeAutoHideAfterDrag();

        if (e.TryGetPosition(target, out var pos))
            menu.ShowAt(target, pos);
        else
            menu.ShowAt(target);
        e.Handled = true;

        static MenuFlyoutItem Mi(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }
    }
}
