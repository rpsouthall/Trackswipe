using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Anchor;

/// <summary>
/// Groups: one dock icon that holds several items and opens them in a fly-out, so a long dock
/// stays short. Partial of <see cref="DockWindow"/>.
/// <para>
/// A group is a <see cref="DockItemKind.Group"/> item that carries <see cref="DockItem.Children"/>
/// instead of a target. Groups deliberately do not nest, and separators can't go inside one: both
/// would give the fly-out a structure it has no way to render, and neither earns its complexity.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>Every group currently on the dock, in dock order (drives the "Move to" menu).</summary>
    public IReadOnlyList<DockItem> Groups =>
        _profile.Items.Where(i => i.IsGroup).ToList();

    /// <summary>True for an item that can be filed into a group: not a group, not a separator.</summary>
    private static bool CanBeGrouped(DockItem item) => !item.IsGroup && !item.IsSeparator && !item.IsTransient;

    // ---- Creating and dissolving ------------------------------------------

    /// <summary>Adds an empty group to the end of the dock and returns it.</summary>
    public DockItem CreateGroup(string name)
    {
        var group = new DockItem
        {
            Kind = DockItemKind.Group,
            DisplayName = string.IsNullOrWhiteSpace(name) ? Loc.Get("Kind.Group") : name.Trim(),
        };
        AddDockItem(group);
        return group;
    }

    /// <summary>
    /// Files an item into a group. The item leaves the top-level list entirely — it lives inside
    /// the group from here on, and comes back via <see cref="RemoveFromGroup"/> or
    /// <see cref="Ungroup"/>.
    /// </summary>
    public void MoveItemToGroup(DockItem item, DockItem group)
    {
        if (!CanBeGrouped(item) || !group.IsGroup || ReferenceEquals(item, group))
            return;

        _profile.Items.Remove(item);
        Items.Remove(item);
        group.Children.Add(item);

        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Lifts a child back out onto the dock, immediately after the group it came from, so it
    /// lands where the user is looking rather than at the far end of the strip.
    /// </summary>
    public void RemoveFromGroup(DockItem child)
    {
        var group = _profile.Items.FirstOrDefault(i => i.IsGroup && i.Children.Contains(child));
        if (group is null)
            return;

        group.Children.Remove(child);
        int at = _profile.Items.IndexOf(group);
        _profile.Items.Insert(at < 0 ? _profile.Items.Count : at + 1, child);

        RebuildVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Dissolves a group, leaving its children on the dock in its place and in their own order.
    /// The non-destructive counterpart to removing a group, which takes the children with it.
    /// </summary>
    public void Ungroup(DockItem group)
    {
        if (!group.IsGroup)
            return;

        int at = _profile.Items.IndexOf(group);
        if (at < 0)
            return;

        _profile.Items.RemoveAt(at);
        _profile.Items.InsertRange(at, group.Children);
        group.Children.Clear();

        RebuildVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>Removes a single entry from inside a group, discarding it.</summary>
    public void RemoveGroupChild(DockItem child)
    {
        var group = _profile.Items.FirstOrDefault(i => i.IsGroup && i.Children.Contains(child));
        if (group is null)
            return;
        group.Children.Remove(child);
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Swaps a child with its neighbor one slot earlier (-1) or later (+1) within its own group.
    /// Used by the Settings "Apps &amp; links" page, which — unlike the group's own fly-out (see
    /// <see cref="ReorderGroupChildren"/>) — has no drag gesture of its own to reorder by.
    /// </summary>
    public void MoveGroupChild(DockItem child, int direction)
    {
        var group = _profile.Items.FirstOrDefault(i => i.IsGroup && i.Children.Contains(child));
        if (group is null)
            return;
        var list = group.Children;
        int i = list.IndexOf(child);
        int j = i + direction;
        if (j < 0 || j >= list.Count)
            return;
        (list[i], list[j]) = (list[j], list[i]);
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Moves a child to an arbitrary slot within its own group, for the Settings "Apps &amp; links"
    /// page's drag-to-reorder rows. Unlike <see cref="MoveGroupChild"/>, which only swaps adjacent
    /// neighbors, this drops the child directly where the drag ended.
    /// </summary>
    public void ReorderGroupChild(DockItem child, int toIndex)
    {
        var group = _profile.Items.FirstOrDefault(i => i.IsGroup && i.Children.Contains(child));
        if (group is null)
            return;
        var list = group.Children;
        int from = list.IndexOf(child);
        if (from < 0)
            return;
        toIndex = Math.Clamp(toIndex, 0, list.Count - 1);
        if (from == toIndex)
            return;
        list.RemoveAt(from);
        list.Insert(toIndex, child);
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Collapses or expands a group's children in the Settings "Apps &amp; links" list. Doesn't
    /// touch <see cref="Items"/> or the dock's own strip — a group's children never appear there,
    /// only in that list — so no <c>RebuildVisible</c> is needed, just persistence.
    /// </summary>
    public void SetGroupCollapsed(DockItem group, bool collapsed)
    {
        if (!group.IsGroup || group.IsCollapsed == collapsed)
            return;
        group.IsCollapsed = collapsed;
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Applies a new order to a group's <em>visible</em> children — what dragging a cell to a new
    /// slot inside the group's own fly-out bar produces (see <see cref="DockBarOptions.OnReorder"/>
    /// and <c>WatchCellDrag</c> in <c>DockWindow.FlyoutBar.cs</c>). Hidden children keep their
    /// existing absolute slots and the visible ones are refilled around them in the new order —
    /// the same rule <see cref="DockWindow.SyncMasterFromVisible"/> applies when a reorder on the
    /// main strip (which likewise only ever shows visible items) is written back to the master list.
    /// </summary>
    public void ReorderGroupChildren(DockItem group, IReadOnlyList<DockItem> visibleOrder)
    {
        var q = new Queue<DockItem>(visibleOrder);
        for (int i = 0; i < group.Children.Count && q.Count > 0; i++)
            if (!group.Children[i].Hidden)
                group.Children[i] = q.Dequeue();

        PersistAndRelayout();
        RaiseItemsChanged();
    }

    // ---- The fly-out -------------------------------------------------------

    /// <summary>The group whose fly-out is currently open, and the fly-out itself — tracked so a
    /// hover that lands on a second group can swap straight to it, and so re-hovering (or
    /// re-clicking) the same group's icon while its bar is already up is a no-op rather than a
    /// flicker of the same content.</summary>
    private DockItem? _openGroup;
    private Flyout? _openGroupFlyout;

    /// <summary>The dock icon the open bar hangs from. Kept so <see cref="CursorIsOverGroupTrigger"/>
    /// can ask where the cursor physically is, rather than trusting what the strip last said.</summary>
    private FrameworkElement? _openGroupAnchor;

    /// <summary>True while the cursor sits over the dock icon that opened <see cref="_openGroupFlyout"/>,
    /// or over the bar itself — see <see cref="TrackGroupHover"/> and
    /// <see cref="SetGroupBarHovered"/>. Both feed <see cref="GroupHoverWatchTick"/>, which is what
    /// closes the bar again once the cursor leaves both.</summary>
    private bool _pointerOverGroupTrigger;
    private bool _pointerOverGroupBar;

    /// <summary>
    /// The group whose bar a click just closed, held until the cursor leaves that group's icon.
    /// Without it "a second click hides the bar" cannot survive hover mode: the click closes the
    /// bar, the cursor is still sitting on the icon, and the very next pointer move over the strip
    /// would open the same bar straight back up — the click would read as doing nothing at all.
    /// Cleared by <see cref="TrackGroupHover"/> as soon as the cursor is somewhere else, so
    /// coming back to the icon hovers it open again as usual.
    /// </summary>
    private DockItem? _clickClosedGroup;

    /// <summary>The last group bar to close, and when — see <see cref="ClosedOnThisClick"/>.</summary>
    private DockItem? _lastClosedGroup;
    private DateTime _lastGroupCloseAt = DateTime.MinValue;

    /// <summary>Runs <see cref="GroupHoverWatchTick"/> while a hover-opened bar is up. See
    /// <see cref="EnsureGroupHoverWatch"/> for why closing is polled rather than evented.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _groupCloseTimer;

    /// <summary>When the cursor first went away from both the icon and the bar, or null while it
    /// is on one of them. The debounce that lets the cursor cross the gap between the two — which
    /// is off both — without the bar shutting mid-transition.</summary>
    private DateTime? _groupAwaySince;

    /// <summary>How often the watch looks, and how long the cursor must be away from both the
    /// icon and the bar before the bar closes.</summary>
    private static readonly TimeSpan GroupHoverPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GroupCloseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Opens a group's fly-out directly over its dock icon: its (visible) children as a
    /// <see cref="ShowDockBarFlyout">dock bar</see> — the same cells, chrome and spacing as the
    /// strip itself, running the same way the dock does, so what pops out is a second dock
    /// floating above the main one rather than a menu beside it.
    /// <para>
    /// It always opens away from whichever screen edge the dock is snapped to (so it can never
    /// open off-screen into that edge), or upward when the dock is floating — see
    /// <see cref="GroupFlyoutPlacement"/>.
    /// </para>
    /// <para>
    /// Called from <see cref="TrackGroupHover"/> as the cursor passes over the icon (only while
    /// <see cref="DockConfig.GroupOpenOnHover"/> is on) and from <see cref="ToggleGroupFlyout"/>'s
    /// "open" half, so the same bar comes up whichever way the user got there. Always a plain
    /// show — a hover (or a re-click routed here) that lands on the group already showing is a
    /// no-op; a hover onto a <em>different</em> group closes the first bar and opens the second,
    /// the way a menu bar swaps top-level menus under the cursor without a click for each one.
    /// Closing an already-open bar is <see cref="ToggleGroupFlyout"/>'s job, not this one's.
    /// </para>
    /// </summary>
    private void ShowGroupFlyout(FrameworkElement anchor, DockItem group)
    {
        if (ReferenceEquals(_openGroup, group))
            return;

        _openGroupFlyout?.Hide();

        var children = group.Children.Where(c => !c.Hidden).ToList();
        var flyout = ShowDockBarFlyout(anchor, new DockBarOptions(
            children,
            Loc.Get("Group.Empty"),
            OnContext: ShowGroupChildMenu,
            // Dragging a cell clear of the bar is the reverse of dropping an icon onto the group:
            // it puts the item back on the strip, next to the group it came out of.
            OnDragOut: RemoveFromGroup,
            // Dragging a cell within the bar reorders it among its siblings, the same as dragging
            // an icon on the main strip does among the top-level items.
            OnReorder: newOrder => ReorderGroupChildren(group, newOrder),
            // Hover has to keep working on the strip underneath the bar — both to close this bar
            // again and to swap to another group — and the bar's own light-dismiss layer would
            // otherwise take the pointer away from the dock entirely.
            KeepDockInteractive: true));

        _openGroup = group;
        _openGroupFlyout = flyout;
        _openGroupAnchor = anchor;
        _groupAwaySince = null;
        EnsureGroupHoverWatch();

        // The strip side of "hover off closes the bar" comes from TrackStripPointer; this is the
        // bar's own half, since once the cursor is over the bar it is no longer over the dock icon
        // at all. Hooked on whatever ShowDockBarFlyout put in Content — the empty-state TextBlock
        // or the ScrollViewer — both are FrameworkElements either way.
        //
        // AddHandler with handledEventsToo, NOT the += accessor: the bar's cells are Buttons, and
        // (see HookStripPointer) a Button marks its own pointer events handled for its own visual
        // states. Landing the cursor squarely on a cell — the common case, since cells are most of
        // the bar's area — would otherwise have its PointerEntered swallowed before it ever
        // bubbled up to barRoot, so "am I over the bar" would go true only on the lucky moves that
        // happened to cross a gap between cells instead. That was the bar closing under the
        // cursor at unpredictable moments rather than reliably on hover-off.
        if (flyout.Content is FrameworkElement barRoot)
        {
            barRoot.AddHandler(UIElement.PointerEnteredEvent,
                new PointerEventHandler((_, _) => SetGroupBarHovered(true)), handledEventsToo: true);
            barRoot.AddHandler(UIElement.PointerExitedEvent,
                new PointerEventHandler((_, _) => SetGroupBarHovered(false)), handledEventsToo: true);
        }

        // Everything here is scoped to "this flyout is still the open one": hovering from one
        // group onto the next hides the first bar while the second is already the current one, and
        // clearing the shared hover state on that stale close would wipe out the new bar's.
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_openGroupFlyout, flyout))
                return;
            _openGroupFlyout = null;
            _openGroup = null;
            _openGroupAnchor = null;
            _lastClosedGroup = group;
            _lastGroupCloseAt = DateTime.UtcNow;
            _pointerOverGroupTrigger = false;
            _pointerOverGroupBar = false;
            _groupCloseTimer?.Stop();
        };
    }

    /// <summary>
    /// The group icon's click behavior: opens the fly-out if it (or a different group's) isn't
    /// already up, or closes it if this group's own bar is the one currently showing — "first
    /// click shows it, second click hides it". Used by <see cref="Item_Click"/>, so it fires
    /// whichever way a click can (mouse, touch, or keyboard Space/Enter on the focused icon).
    /// <para>
    /// This is the one way to close a group's bar when <see cref="DockConfig.GroupOpenOnHover"/>
    /// is off — with hover-driven opening and closing both switched off, a click is the only
    /// thing left that can open <em>or</em> close it (besides the fly-out's own light-dismiss on
    /// an outside click or Escape).
    /// </para>
    /// </summary>
    private void ToggleGroupFlyout(FrameworkElement anchor, DockItem group)
    {
        if (ReferenceEquals(_openGroup, group) || ClosedOnThisClick(group))
        {
            // Suppress the hover re-open before hiding: the cursor is still on the icon, and the
            // strip's next pointer move would otherwise put the bar straight back up.
            _clickClosedGroup = group;
            _openGroupFlyout?.Hide();
            return;
        }
        ShowGroupFlyout(anchor, group);
    }

    /// <summary>
    /// True when this group's bar was closed by the very click now being handled, rather than by
    /// anything earlier. A press outside a light-dismiss fly-out dismisses it <em>and</em> — since
    /// the dock is the bar's pass-through element — goes on to the icon underneath, so by the time
    /// <see cref="ToggleGroupFlyout"/> runs the bar it was meant to toggle shut is already gone
    /// and <see cref="_openGroup"/> is null. Treating a close this recent as part of the same
    /// click is what stops that from reading as "open it again": the second click hides the bar
    /// and leaves it hidden, whichever of the two paths actually did the hiding.
    /// </summary>
    private bool ClosedOnThisClick(DockItem group) =>
        ReferenceEquals(_lastClosedGroup, group) &&
        DateTime.UtcNow - _lastGroupCloseAt < TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// What the strip's pointer tracking reports on every move: the group icon under the cursor,
    /// or null for a non-group cell or no cell at all. The one place hover-to-open is decided.
    /// <para>
    /// Opening is conditional on <see cref="DockConfig.GroupOpenOnHover"/>; the hover <em>flag</em>
    /// is not, so that switching hover mode back on finds the flag saying what the cursor is
    /// really doing rather than whatever it last said before the setting changed.
    /// </para>
    /// </summary>
    private void TrackGroupHover(FrameworkElement? anchor, DockItem? group)
    {
        // Off the icon whose bar a click closed: hover owns it again.
        if (_clickClosedGroup is not null && !ReferenceEquals(_clickClosedGroup, group))
            _clickClosedGroup = null;

        // Before the open below, so landing on the icon cancels a close already scheduled rather
        // than letting it fire against the bar this move is about to (re-)open.
        SetGroupTriggerHovered(group is not null);

        if (group is null || anchor is null)
            return;
        if (!_manager.Config.GroupOpenOnHover || ReferenceEquals(_clickClosedGroup, group))
            return;
        ShowGroupFlyout(anchor, group);
    }

    /// <summary>Marks whether the cursor is over a group's dock icon. Called from
    /// <see cref="TrackGroupHover"/> on every strip pointer move.</summary>
    private void SetGroupTriggerHovered(bool hovered) => _pointerOverGroupTrigger = hovered;

    /// <summary>Marks whether the cursor is over the open fly-out bar itself.</summary>
    private void SetGroupBarHovered(bool hovered) => _pointerOverGroupBar = hovered;

    /// <summary>
    /// Starts the watch that closes a hover-opened bar once the cursor has left both it and the
    /// icon it came out of.
    /// <para>
    /// It polls the cursor rather than simply closing when the strip reports a pointer exit,
    /// because that report cannot be trusted here: opening the fly-out itself makes the strip fire
    /// <c>PointerExited</c> — the popup takes the pointer for a moment as it comes up — with the
    /// cursor still sitting squarely on the icon. Closing on that is what produced the flicker
    /// this path exists to avoid: the bar opened, "left" immediately, closed a beat later, and the
    /// next pointer move over the icon opened it again, over and over for as long as the cursor
    /// rested there. Where the cursor actually is settles the question outright, and gives the
    /// same answer whether or not any pointer event ever arrives — see
    /// <see cref="CursorIsOverGroupTrigger"/>.
    /// </para>
    /// </summary>
    private void EnsureGroupHoverWatch()
    {
        if (!_manager.Config.GroupOpenOnHover)
            return;

        if (_groupCloseTimer is null)
        {
            _groupCloseTimer = DispatcherQueue.CreateTimer();
            _groupCloseTimer.Interval = GroupHoverPoll;
            _groupCloseTimer.IsRepeating = true;
            _groupCloseTimer.Tick += (_, _) => GroupHoverWatchTick();
        }
        if (!_groupCloseTimer.IsRunning)
            _groupCloseTimer.Start();
    }

    /// <summary>
    /// Re-applies "open groups on hover" to a bar that is already up: starts watching it when the
    /// setting has just come on, and lets <see cref="GroupHoverWatchTick"/> stand itself down when
    /// it has just gone off. Called by <see cref="DockManager.SetGroupOpenOnHover"/>.
    /// </summary>
    public void ApplyGroupOpenOnHoverSetting()
    {
        if (_openGroupFlyout is not null)
            EnsureGroupHoverWatch();
    }

    /// <summary>
    /// One look at where the cursor is: keeps the bar up while it is on the icon or on the bar,
    /// and hides it once it has been away from both for <see cref="GroupCloseDelay"/>.
    /// </summary>
    private void GroupHoverWatchTick()
    {
        // Nothing to watch: the bar is gone, or hover mode was switched off under it — in which
        // case the bar is click-driven from here on and must stay up until a click closes it.
        if (_openGroupFlyout is null || !_manager.Config.GroupOpenOnHover)
        {
            _groupCloseTimer?.Stop();
            return;
        }

        bool overTrigger = CursorIsOverGroupTrigger();
        // The strip's own flag is corrected rather than consulted: it goes stale precisely when
        // the fly-out's phantom exit sets it false with the cursor still on the icon.
        _pointerOverGroupTrigger = overTrigger;

        if (overTrigger || _pointerOverGroupBar)
        {
            _groupAwaySince = null;
            return;
        }

        _groupAwaySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _groupAwaySince < GroupCloseDelay)
            return;

        _groupCloseTimer?.Stop();
        _openGroupFlyout?.Hide();
    }

    /// <summary>
    /// True when the cursor is physically inside the dock icon the open bar hangs from. Measured
    /// against the screen rather than asked of the pointer events, for the reason
    /// <see cref="EnsureGroupHoverWatch"/> gives: the icon's window-relative bounds scaled by the
    /// dock's DPI and offset by the window's position, the same conversion the reorder drag does.
    /// </summary>
    private bool CursorIsOverGroupTrigger()
    {
        if (_openGroupAnchor is null || !NativeMethods.GetCursorPos(out var cursor))
            return false;

        try
        {
            double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            var bounds = _openGroupAnchor.TransformToVisual(RootGrid).TransformBounds(
                new Windows.Foundation.Rect(
                    0, 0, _openGroupAnchor.ActualWidth, _openGroupAnchor.ActualHeight));

            double left = _appWindow.Position.X + bounds.X * scale;
            double top = _appWindow.Position.Y + bounds.Y * scale;
            return cursor.X >= left && cursor.X <= left + bounds.Width * scale
                && cursor.Y >= top && cursor.Y <= top + bounds.Height * scale;
        }
        catch
        {
            // The anchor can be un-parented mid-tick by a relayout; "not over it" simply lets the
            // bar close, which is the safe way to be wrong here.
            return false;
        }
    }

    /// <summary>
    /// Which way the fly-out bar should extend: away from the dock's snapped edge (so it never
    /// tries to open into the screen edge the dock is pinned against), or straight up when the
    /// dock floats — matching the "extends above the dock" look for the common floating and
    /// bottom-snapped cases.
    /// </summary>
    private FlyoutPlacementMode GroupFlyoutPlacement =>
        !_profile.Snapped ? FlyoutPlacementMode.Top : _profile.Edge switch
        {
            DockEdge.Bottom => FlyoutPlacementMode.Top,
            DockEdge.Top => FlyoutPlacementMode.Bottom,
            DockEdge.Left => FlyoutPlacementMode.Right,
            DockEdge.Right => FlyoutPlacementMode.Left,
            _ => FlyoutPlacementMode.Top,
        };

    private void ShowGroupChildMenu(
        FrameworkElement target, DockItem child, Flyout owner, ContextRequestedEventArgs e)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Mi(Loc.Get("Menu.Open"), () => { owner.Hide(); LaunchOrFocus(child); }));
        // The same one editor the strip's own menu opens — name, target and icon together — in a
        // window of its own, so closing the bar first takes nothing with it.
        menu.Items.Add(Mi(Loc.Get("Menu.Edit"), () =>
        {
            owner.Hide();
            _manager.OpenItemEditor(this, child);
        }));
        if (child.HasCustomIcon)
            menu.Items.Add(Mi(Loc.Get("Menu.ResetIcon"), () => SetCustomIcon(child, null)));
        if (Launcher.SupportsFileLocation(child))
            menu.Items.Add(Mi(Loc.Get("Menu.OpenFileLocation"), () => { owner.Hide(); Launcher.OpenFileLocation(child); }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Mi(Loc.Get("Menu.RemoveFromGroup"), () => { owner.Hide(); RemoveFromGroup(child); }));
        menu.Items.Add(Mi(Loc.Get("Menu.Remove"), () => { owner.Hide(); RemoveGroupChild(child); }));

        if (e.TryGetPosition(target, out var pos))
            menu.ShowAt(target, pos);
        else
            menu.ShowAt(target);

        static MenuFlyoutItem Mi(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }
    }

    // ---- "Move to group" menu ---------------------------------------------

    /// <summary>
    /// The <c>Move to group ▸</c> submenu for a top-level item: every existing group, plus a
    /// "New group…" entry that creates one and files the item into it in a single step.
    /// </summary>
    private MenuFlyoutSubItem BuildMoveToGroupMenu(FrameworkElement target, DockItem item)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get("Menu.MoveToGroup") };

        foreach (var group in Groups)
        {
            var entry = new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(group.DisplayName)
                    ? Loc.Get("Kind.Group")
                    : group.DisplayName,
            };
            var captured = group;
            entry.Click += (_, _) => MoveItemToGroup(item, captured);
            sub.Items.Add(entry);
        }

        if (sub.Items.Count > 0)
            sub.Items.Add(new MenuFlyoutSeparator());

        // Opens the same window the dock's own "New group" menu entry does, in a real window
        // rather than a fly-out (see NewGroupWindow).
        var create = new MenuFlyoutItem { Text = Loc.Get("Menu.NewGroup") };
        create.Click += (_, _) => _manager.OpenNewGroupWindow(this, item);
        sub.Items.Add(create);

        return sub;
    }
}
