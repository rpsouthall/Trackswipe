using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Anchor;

/// <summary>
/// What happens while something from outside Anchor is dragged over the dock: the slot the strip
/// opens up to say where the drop will land, and what a drop on a particular <em>icon</em> does
/// instead of that. Partial of <see cref="DockWindow"/>.
/// <para>
/// Two targets, both of which read the way the shell has trained people to expect:
/// dropping a file on an <b>app</b> opens the file with that app (as dropping it on an exe in
/// Explorer does), and dropping one on a <b>group</b> files it into the group. Everything else on
/// the strip keeps the existing behavior — the drop adds the item to the dock — which is why these
/// handlers mark the event handled only when they actually take it (see
/// <c>DockWindow.Root_DragOver</c>, which would otherwise overwrite the caption on the way up).
/// </para>
/// <para>
/// A dropped <em>app</em> is the exception to the first of those: it is added to the dock as a
/// shortcut wherever it lands, icon or not. "Open Chrome with Notepad" is not something anyone
/// means, and a dock strip is mostly icons — so treating a dropped app the way a dropped document
/// is treated made adding an app by dragging it a matter of hitting the few pixels between two
/// icons.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>The icon a drag is currently hovering, swelled as the drop cue.</summary>
    private DockItem? _dropTarget;

    /// <summary>How much a cell grows to say "drop it here".</summary>
    private const double DropTargetSwell = 1.3;

    /// <summary>
    /// What the drag now in flight is carrying, once it has been read. Resolving it means going
    /// out to the shell, which is asynchronous, while a DragOver caption has to be decided on the
    /// spot — so each cell reads the payload as the drag <em>enters</em> it and every DragOver
    /// after that answers from here. Null until the first read of a drag lands (and again after a
    /// drop, so the next drag can never be answered from the last one's payload).
    /// </summary>
    private IReadOnlyList<ShellDrop.DroppedItem>? _dragPayload;

    /// <summary>Guards against piling up reads of the same payload, one per pointer move.</summary>
    private bool _dragPayloadPending;

    /// <summary>
    /// Reads what the drag is carrying, so the caption this cell shows says what its drop will
    /// actually do.
    /// <para>
    /// Deliberately <em>not</em> under a deferral, though this is exactly what deferrals are for.
    /// A deferral makes the platform apply the event's accepted operation when it completes rather
    /// than when the handler returns — and DragEnter has none to apply, so a read that finished
    /// after the DragOvers had already accepted the drag pushed that "no operation" back out and
    /// left the shell showing the "no entry" cursor over a dock that was in fact accepting the
    /// drop. With the pointer then held still there was no further DragOver to correct it, so the
    /// cue stayed wrong for as long as the user hesitated. Reading without one keeps the platform's
    /// view of the drag entirely in the hands of the DragOver handler below.
    /// </para>
    /// <para>
    /// What that costs is a guarantee: the view is only contractually readable while the event is
    /// live, so a read that lands too late fails. That is a caption that stays on its default for
    /// this drag, nothing more — the drop itself reads the payload again, under its own deferral,
    /// and never consults this.
    /// </para>
    /// </summary>
    private void Item_DragEnter(object sender, DragEventArgs e)
    {
        if (_dragPayloadPending || !ShellDrop.HasShellItems(e.DataView))
            return;

        _dragPayloadPending = true;
        _ = ReadDragPayloadAsync(e.DataView);
    }

    private async Task ReadDragPayloadAsync(Windows.ApplicationModel.DataTransfer.DataPackageView data)
    {
        try
        {
            _dragPayload = await ShellDrop.ReadAsync(data);
        }
        catch (Exception ex)
        {
            Diag.Log("Reading the drag payload failed: " + ex.Message);
        }
        finally
        {
            _dragPayloadPending = false;
        }
    }

    /// <summary>
    /// True when everything being dragged is an app — the case that is added to the dock rather
    /// than opened with whatever it was dropped on. False while the payload is still being read,
    /// which keeps the caption on the behavior that has always applied to a drag of files until
    /// the read (a few milliseconds) says otherwise.
    /// </summary>
    private bool DraggingAppsOnly() =>
        _dragPayload is { Count: > 0 } payload &&
        payload.All(d => d.Kind == DockItemKind.Application);

    private void Item_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DockItem item })
            return;

        // The drop shell is an empty slot, not an icon: there is nothing to open a file with and
        // nothing to file into, so it falls through to the strip's handler — which is what put
        // it there, and what keeps it where the cursor is.
        if (item.IsPlaceholder)
            return;

        // Only drags of shell items land on an icon. A dropped URL has nothing to hand an app on
        // a command line and nothing sensible to file into a group, so it falls through to the
        // strip's own handler, which turns it into a web link.
        if (!ShellDrop.HasShellItems(e.DataView))
            return;

        NoteDragSeen();

        string? caption = item.Kind switch
        {
            // An app dropped on an app is an "add to dock", which the strip's own handler does —
            // so say nothing here and let it bubble. Same for anything with no file behind it at
            // all (an app dragged out of the Start menu): there is no path to pass on a command
            // line, so there is nothing to open this app with.
            DockItemKind.Application when DraggingAppsOnly() ||
                !ShellDrop.HasStorageItems(e.DataView) => null,
            DockItemKind.Application => Loc.Format("Dock.DropOnApp", item.DisplayName),
            DockItemKind.Group => Loc.Format("Dock.DropInGroup", item.DisplayName),
            _ => null,
        };
        if (caption is null)
        {
            SetDropTarget(null);
            return;
        }

        SetDropTarget(item);
        // This icon is taking the drop, so the shell is no longer where it will land. Its slot
        // stays open all the same — see DockItem.SetPlaceholderMuted for why closing it here
        // would fight the cursor.
        _dropShell?.SetPlaceholderMuted(true);
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
        {
            ui.Caption = caption;
            ui.IsCaptionVisible = true;
            ui.IsGlyphVisible = true;
        }
        // Stop here: the strip's handler would otherwise replace the caption with "Add to dock",
        // which is not what this drop is going to do.
        e.Handled = true;
    }

    /// <summary>
    /// Drops the cue when the drag leaves this cell — but only if this cell is still the one
    /// showing it. Moving from one icon straight onto the next raises the new cell's DragOver
    /// before the old cell's DragLeave, so an unconditional clear here would wipe the cue off the
    /// cell the cursor had just arrived at.
    /// </summary>
    private void Item_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DockItem item } && ReferenceEquals(_dropTarget, item))
            SetDropTarget(null);
    }

    private async void Item_Drop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);

        if (sender is not FrameworkElement { Tag: DockItem item } || item.IsPlaceholder ||
            item.Kind is not (DockItemKind.Application or DockItemKind.Group) ||
            !ShellDrop.HasShellItems(e.DataView))
            return;

        // Handled before the first await: the strip's Drop handler runs as this one bubbles, and
        // by the time an await has resumed it has already added the files to the dock.
        e.Handled = true;

        var deferral = e.GetDeferral();
        try
        {
            var dropped = await ShellDrop.ReadAsync(e.DataView);
            if (dropped.Count == 0)
                return;

            if (item.Kind == DockItemKind.Group)
            {
                FileIntoGroup(item, dropped);
                return;
            }

            // Only a real file can be handed to an app on a command line, so a drop with none —
            // an app dragged out of the Start menu — has nothing to open. Neither does a drop of
            // apps: those are added to the dock, wherever on the strip they landed.
            var paths = dropped.Where(d => d.IsFileSystemPath).Select(d => d.Target).ToList();
            if (paths.Count == 0 || dropped.All(d => d.Kind == DockItemKind.Application))
                AddDroppedItems(dropped, TakeDropSlot());
            else
                Launcher.LaunchWith(item, paths);
        }
        catch (Exception ex)
        {
            Diag.Log("Drop onto an icon failed: " + ex.Message);
        }
        finally
        {
            _dragPayload = null;
            // Nothing landed on the strip on the two paths above that don't add anything, so the
            // slot the drag opened has to close either way.
            ClearDropShell();
            deferral.Complete();
        }
    }

    /// <summary>Adds dropped items straight into a group, skipping the strip entirely.</summary>
    private void FileIntoGroup(DockItem group, IReadOnlyList<ShellDrop.DroppedItem> dropped)
    {
        foreach (var item in dropped)
        {
            var child = item.ToDockItem();
            group.Children.Add(child);
            _ = LoadOneIconAsync(child);
        }
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    // ---- The drop shell ---------------------------------------------------
    //
    // Dragging something in from outside parts the strip around an empty cell where the drop will
    // land, the same way the strip parts around an icon being reordered — and the drop then goes
    // in *there*, rather than onto the end of the strip as it used to. The shell lives only in
    // the visible collection, never in the profile, so nothing about it can be persisted.

    /// <summary>The empty cell held open in <see cref="Items"/> while a drag is over the dock.</summary>
    private DockItem? _dropShell;

    /// <summary>Closes the shell if the drag stops arriving without ever saying it left.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dropShellWatchdog;

    /// <summary>When any handler last saw the drag over this dock (<c>Environment.TickCount64</c>).</summary>
    private long _dragSeenAt;

    /// <summary>How long a silent drag is given before the shell assumes it is gone.</summary>
    private const long DragLostAfterMs = 500;

    /// <summary>Marks the drag as still in flight over this dock, for the watchdog below.</summary>
    private void NoteDragSeen() => _dragSeenAt = Environment.TickCount64;

    /// <summary>
    /// Opens the shell, or slides it to the slot the cursor is now over. Driven from the strip's
    /// own DragOver, which runs for exactly the drags the dock would <em>add</em> — a drag an icon
    /// is taking marks the event handled and never reaches it, which is what leaves the slot
    /// parked (and muted) while the cursor sits on an app.
    /// </summary>
    private void TrackDropShell(DragEventArgs e)
    {
        NoteDragSeen();

        // A shell on its own is the whole strip: there is no other cell to measure it against,
        // and on an empty dock the item host it would be measured against has not been laid out
        // yet either (it is collapsed until the strip has something in it).
        if (_dropShell is not null && Items.Count == 1)
        {
            _dropShell.SetPlaceholderMuted(false);
            return;
        }

        int slot;
        try
        {
            var p = e.GetPosition(ItemsHost);
            slot = SlotAt(IsVertical ? p.Y : p.X, _dropShell);
        }
        catch (Exception ex)
        {
            Diag.Log("Placing the drop shell failed: " + ex.Message);
            return;
        }

        if (_dropShell is null)
        {
            OpenDropShell(slot);
            return;
        }

        _dropShell.SetPlaceholderMuted(false);
        int from = Items.IndexOf(_dropShell);
        slot = Math.Clamp(slot, 0, PinnedCount - 1); // the shell counts as pinned; the open apps stay after it
        if (from >= 0 && from != slot)
            Items.Move(from, slot);
    }

    private void OpenDropShell(int slot)
    {
        _dropShell = DockItem.CreatePlaceholder();
        _dropShell.SetFlowVertical(IsVertical);
        Items.Insert(Math.Clamp(slot, 0, PinnedCount), _dropShell);

        // The strip just grew by a cell, so the window has to grow with it or the shell is drawn
        // outside the glass. Auto-hide is held off for the length of the drag for the same reason
        // a reorder holds it off: a dock that slid away mid-gesture would take the drop with it.
        PauseAutoHideForDrag();
        QueueRelayout();
        EnsureDropShellWatchdog();
    }

    /// <summary>Closes the shell and gives the strip its old geometry back. Idempotent.</summary>
    private void ClearDropShell()
    {
        _dropShellWatchdog?.Stop();
        if (_dropShell is null)
            return;

        Items.Remove(_dropShell);
        _dropShell = null;
        CatchUpOpenApps();
        QueueRelayout();
        ResumeAutoHideAfterDrag();
    }

    /// <summary>
    /// The slot a drop should land in — the one the shell has been holding open — closing the
    /// shell as it is read, so what is added goes straight into the gap the user was looking at.
    /// Falls back to the end of the strip when no shell is open, which is where every drop landed
    /// before there was one.
    /// </summary>
    private int TakeDropSlot()
    {
        int slot = _dropShell is null ? Items.Count : Items.IndexOf(_dropShell);
        ClearDropShell();
        return slot < 0 ? Items.Count : slot;
    }

    /// <summary>
    /// Closes the shell when the drag leaves the dock. A cell's own DragLeave bubbles up here
    /// every time the cursor crosses from one icon to the next, so it is the cursor — not the
    /// event — that is asked whether the drag has really gone.
    /// </summary>
    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        if (CursorOverDock())
            return;
        SetDropTarget(null);
        ClearDropShell();
    }

    /// <summary>True while the button carrying a drag is still held down.</summary>
    private static bool DragButtonHeld() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

    /// <summary>True while the cursor is within the dock window's current bounds (physical px).</summary>
    private bool CursorOverDock()
    {
        if (!NativeMethods.GetCursorPos(out var p))
            return false;
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        return p.X >= pos.X && p.X < pos.X + size.Width &&
               p.Y >= pos.Y && p.Y < pos.Y + size.Height;
    }

    private void EnsureDropShellWatchdog()
    {
        if (_closed)
            return;
        _dropShellWatchdog ??= CreateDropShellWatchdog();
        if (!_dropShellWatchdog.IsRunning)
            _dropShellWatchdog.Start();
    }

    /// <summary>
    /// The shell's way out when the drag stops arriving. Leaving the dock, cancelling with Esc and
    /// dropping on another window all raise DragLeave — but a missed one would leave the strip
    /// parted around a slot for a drag that ended long ago, and a window sized for it.
    /// <para>
    /// Silence on its own is <em>not</em> that, which is the whole subtlety here: a drag simply
    /// being held still raises no DragOver either, and someone lining a file up between two icons
    /// holds it still on purpose — closing the gap under them would be the bug. So the shell goes
    /// only once the drag cannot still be over the dock: the cursor has left it, or the button
    /// carrying the drag is no longer down.
    /// </para>
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDropShellWatchdog()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(200);
        t.Tick += (_, _) =>
        {
            if (_closed)
            {
                t.Stop();
                return;
            }
            if (Environment.TickCount64 - _dragSeenAt < DragLostAfterMs)
                return;
            if (CursorOverDock() && DragButtonHeld())
                return; // held still over the strip, which is not the same as gone
            Diag.Log("The drag went quiet and is no longer over the dock; closing the drop shell");
            SetDropTarget(null);
            ClearDropShell();
        };

        // A drag left in flight when the dock is removed (the strip's own context menu can do this
        // mid-drag) would otherwise keep this ticking on a window whose AppWindow is gone, and
        // CursorOverDock's read of _appWindow.Position/.Size throws once that happens — with
        // nothing above a DispatcherQueueTimer tick to catch it, that is an unhandled exception on
        // the UI thread, i.e. a crash. The shared OnClosed (DockWindow.xaml.cs) stops it if it
        // exists by then, and the _closed check above covers a tick that was already queued. It is
        // not stopped by a lambda on this window's own Closed event: that would keep the closed
        // dock alive (see WindowEventLifetimeTests).
        return t;
    }

    /// <summary>
    /// Swells the icon a drag is over, and un-swells whatever it was over before. Reuses the
    /// magnification channel rather than adding a second visual state: it is already the property
    /// the item template watches for "this cell is bigger than the others", and two mechanisms
    /// fighting over one icon's size is a bug waiting to be written.
    /// </summary>
    private void SetDropTarget(DockItem? item)
    {
        if (ReferenceEquals(_dropTarget, item))
            return;
        _dropTarget?.SetMagnification(1);
        _dropTarget = item;
        _dropTarget?.SetMagnification(DropTargetSwell);
        // A target change needs the animation timer running to ever be seen — SetMagnification
        // only records where to lerp to, and nothing else on this path is guaranteed to already
        // have it ticking (the item-reorder path suppresses hover, the only other thing that
        // reliably kicks it, for the whole gesture; see Strip_PointerMoved).
        EnsureVisualAnimationRunning();
    }
}
