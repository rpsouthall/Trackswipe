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

    /// <summary>Re-lays the section after a setting that shapes it (the divider) changed.</summary>
    internal void ResyncOpenApps() => SetOpenApps(_openApps);

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
        RemoveLeftItems();
        var wanted = new List<DockItem>(_openApps.Count + 1);
        if (_openApps.Count > 0)
        {
            // A divider, if wanted, only between something and something: with nothing pinned, the
            // open apps are the whole strip. Without one, pinned and open apps read as one row, as
            // on a Mac with "recent apps" off.
            if (_manager.Config.OpenAppsDivider && Items.Any(item => !item.IsTransient && !item.IsPlaceholder))
            {
                _openAppsDivider ??= DockItem.CreateOpenAppsDivider();
                wanted.Add(_openAppsDivider);
            }
            wanted.AddRange(_openApps);
        }

        // An app that has just closed stays where it was while it shrinks away (see
        // AdvancePresence), so it is kept in the tail, after whatever it followed before; one that
        // has finished shrinking is dropped. One that reopens mid-shrink grows back in place.
        int start = PinnedCount;
        var before = Items.Skip(start).ToList();
        var tail = new List<DockItem>(wanted);
        for (int i = 0; i < before.Count; i++)
        {
            var item = before[i];
            if (tail.Contains(item))
            {
                if (item.IsLeaving)
                    item.AnimatePresenceTo(1);
                continue;
            }
            if (item.HasLeft)
                continue;
            item.AnimatePresenceTo(0);
            int after = -1;
            for (int j = i - 1; j >= 0 && after < 0; j--)
                after = tail.IndexOf(before[j]);
            tail.Insert(after + 1, item);
        }

        // Laid out at all yet? The apps already open when Anchor starts are just there; only ones
        // that open later grow in.
        bool animate = _shownRect.Width > 0;
        bool changed = false;
        for (int k = 0; k < tail.Count; k++)
        {
            int at = start + k;
            var item = tail[k];
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
                if (animate)
                    item.BeginAppearing();
                Items.Insert(at, item);
                if (item.IconImage is null && !item.IsSeparator)
                    _ = LoadOneIconAsync(item);
            }
            changed = true;
        }
        while (Items.Count > start + tail.Count)
        {
            Items.RemoveAt(Items.Count - 1);
            changed = true;
        }

        if (changed)
            QueueRelayout();
        EnsurePresenceAnimation();
    }

    // ---- Growing in and shrinking out ----------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _presenceTimer;

    /// <summary>Runs the grow/shrink animation while any cell on the strip is still on its way in or out.</summary>
    private void EnsurePresenceAnimation()
    {
        if (_closed || !Items.Any(item => item.IsPresenceAnimating))
        {
            RemoveLeftItems();
            return;
        }
        if (_presenceTimer is null)
        {
            _presenceTimer = DispatcherQueue.CreateTimer();
            // About 120 Hz, like the hide slide: each tick is one resize of a small window.
            _presenceTimer.Interval = TimeSpan.FromMilliseconds(8);
            _presenceTimer.Tick += (_, _) => PresenceTick();
        }
        if (!_presenceTimer.IsRunning)
            _presenceTimer.Start();
    }

    private void PresenceTick()
    {
        Probe.Count("presenceFrame");
        if (_closed)
        {
            _presenceTimer?.Stop();
            return;
        }
        long now = Environment.TickCount64;
        bool moving = false;
        foreach (var item in Items)
            moving |= item.AdvancePresence(now);
        RemoveLeftItems();
        // The window follows the cells frame by frame, so the dock itself widens and narrows (and,
        // kept centred, stays centred) rather than jumping to its new size at the start or end.
        UpdateSizeAndPosition();
        if (!moving)
            _presenceTimer?.Stop();
    }

    /// <summary>Takes cells that have finished shrinking off the strip.</summary>
    private void RemoveLeftItems()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].IsTransient && Items[i].HasLeft)
                Items.RemoveAt(i);
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
        // The taskbar's menu: jump list, the app, pinning, closing.
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = MenuStyle };
        AddTaskbarSection(menu, item, () => _manager.KeepInDock(this, item, PinnedCount), Loc.Get("Menu.KeepInDock"));
        if (Launcher.SupportsFileLocation(item))
        {
            var options = new MenuFlyoutSubItem { Text = Loc.Get("Menu.Options") };
            options.Items.Add(Mi(Loc.Get("Menu.OpenFileLocation"), () => Launcher.OpenFileLocation(item)));
            menu.Items.Add(options);
        }
        AddCloseSection(menu, item);

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
