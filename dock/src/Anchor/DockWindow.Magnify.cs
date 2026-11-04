using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Anchor;

/// <summary>
/// What the cursor does to the strip as it passes over it: the cell under it lights up, and — when
/// "Magnify on hover" is on — its icon swells as well. Partial of <see cref="DockWindow"/>.
/// <para>
/// Both come off <b>one</b> pointer subscription on the strip, and that is the point of this file.
/// Hover used to be left to each item <c>Button</c>'s own <c>PointerOver</c> state, the way the
/// gear and the fly-out bar's cells still do it, and on a cell realized by the
/// <c>ItemsRepeater</c> that state never arrived — so a dock item had no hover cue at all, and the
/// only feedback the strip gave was the swell, which is opt-in. Driving the highlight from the
/// same tracking the swell uses makes it independent of that: whatever the repeater's cells do
/// with their own pointer events, this sees the moves on the strip and says which cell the cursor
/// is in.
/// </para>
/// <para>
/// The swell happens <b>inside each cell</b> rather than pushing icons up out of the strip. That
/// is a deliberate limit, not an oversight: the dock's glass is a real desktop acrylic backdrop,
/// which paints the entire window and cannot be masked, so a window tall enough for icons to pop
/// above the strip would be a tall slab of glass with a strip of icons along the bottom of it.
/// Growing the icon within its (fixed) cell keeps the strip exactly as tight as it is now.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>True while the magnify setting is on and nothing is suppressing the effect.</summary>
    private bool MagnifyActive => _manager.Config.Magnify && !ReducedMotion;

    /// <summary>
    /// Subscribes the strip's pointer tracking, once, for the life of the window. Unconditional —
    /// the highlight needs it whatever the magnify setting says, and the setting is read per-move
    /// (see <see cref="TrackStripPointer"/>) rather than by attaching and detaching handlers.
    /// </summary>
    private void HookStripPointer()
    {
        // handledEventsToo: the item Buttons mark pointer events handled for their own visuals,
        // and without it the tracking would die the moment the cursor actually reached an icon —
        // which is precisely when it matters.
        DockStrip.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(Strip_PointerMoved), handledEventsToo: true);
        DockStrip.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler(Strip_PointerExited), handledEventsToo: true);
    }

    /// <summary>
    /// Re-applies the magnify setting: flattens every icon when it has just been switched off.
    /// Nothing to subscribe or unsubscribe any more — <see cref="HookStripPointer"/> owns that —
    /// but <see cref="DockManager.SetMagnify"/> still calls this on every dock, and an icon left
    /// swelled under the cursor when the setting goes off would stay swelled until the cursor
    /// moved again.
    /// </summary>
    public void ApplyMagnifySetting()
    {
        if (!MagnifyActive)
            ResetMagnification();
    }

    private void Strip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            // A reorder owns the cells' size while it runs — it swells whichever group the item
            // would be filed into (see SetDropTarget), and the two would overwrite each other on
            // every frame, a flicker on the one cell whose state matters most just then. The
            // highlight goes off rather than freezing: the strip is shuffling under the cursor, so
            // a cue pinned to one cell would end up pointing at whatever slid into it.
            foreach (var item in Items)
                item.SetHovered(false);
            return;
        }

        var p = e.GetCurrentPoint(ItemsHost).Position;
        TrackStripPointer(IsVertical ? p.Y : p.X);
    }

    private void Strip_PointerExited(object sender, PointerRoutedEventArgs e) => ResetStripPointer();

    /// <summary>
    /// Finds the cell the cursor is inside and gives it both cues; everything else goes back to
    /// rest. This walks the strip's actual per-cell extents (a separator's slot is narrower than
    /// an icon's) rather than assuming a uniform pitch, so the cell it picks is the one under the
    /// cursor and not an estimate of it.
    /// <para>
    /// The highlight is unconditional. The swell is layered on top of it only while the setting is
    /// on, following <see cref="DockMetrics.HoverMagnificationAt"/>, which reaches 1 again at the
    /// cell's own edges — so crossing into the gap between two cells, or straight on into the next
    /// one, eases rather than snaps.
    /// </para>
    /// </summary>
    /// <param name="coordinate">Cursor position along the strip's flow, in DIPs from the start of
    /// the item host.</param>
    private void TrackStripPointer(double coordinate)
    {
        bool magnify = MagnifyActive;
        double edge = 0;
        int index = 0;
        DockItem? hoveredGroup = null;
        FrameworkElement? hoveredGroupAnchor = null;
        foreach (var item in Items)
        {
            double extent = item.CellExtent;
            double offset = coordinate - (edge + extent / 2);
            // Outside this cell entirely, including the gaps between cells: at rest. The curve
            // would return 1 there anyway, but saying so here is what makes "only the item under
            // the cursor reacts" a property of the code rather than of the curve's tuning.
            bool inside = Math.Abs(offset) < extent / 2;

            item.SetHovered(inside);
            item.SetMagnification(
                inside && magnify ? DockMetrics.HoverMagnificationAt(offset / extent) : 1);
            if (inside)
            {
                var element = ItemsHost.TryGetElement(index) as FrameworkElement;
                SetFastTooltip(element, true);
                // A group has nothing to launch on its own: the cursor landing on it is what opens
                // its fly-out, when the setting allows. Reported rather than acted on here — what
                // a hovered group means (open it, leave it, or hold it shut because a click just
                // closed it) is TrackGroupHover's call, not this loop's.
                if (item.IsGroup && element is not null)
                {
                    hoveredGroup = item;
                    hoveredGroupAnchor = element;
                }
            }

            edge += extent + CellSpacing;
            index++;
        }

        // Null when the cursor landed on a non-group cell or on nothing at all — which is what
        // takes it off whichever group icon opened the current bar, and so a candidate to close.
        TrackGroupHover(hoveredGroupAnchor, hoveredGroup);

        EnsureVisualAnimationRunning();
    }

    /// <summary>Drops both cues — on pointer exit, and whenever the strip is rebuilt under a
    /// cursor that is no longer telling us anything.</summary>
    private void ResetStripPointer()
    {
        foreach (var item in Items)
        {
            item.SetHovered(false);
            item.SetMagnification(1);
        }
        SetFastTooltip(null, false);
        TrackGroupHover(null, null);
        EnsureVisualAnimationRunning();
    }

    /// <summary>Returns every icon to its resting size, leaving the highlight alone — for the
    /// magnify setting being switched off mid-hover, where the cell is still hovered.</summary>
    private void ResetMagnification()
    {
        foreach (var item in Items)
            item.SetMagnification(1);
    }

    // ---- Glass personalization ---------------------------------------------

    /// <summary>
    /// Re-tints the acrylic for the current accent-tint setting. A no-op under High Contrast,
    /// where there is no backdrop to tint (the dock paints an opaque system color instead so the
    /// shell's high-contrast palette comes through).
    /// </summary>
    public void ApplyGlass()
    {
        _backdrop?.SyncWithSystemColors();
        _backdrop?.Personalize(_manager.Config.AccentTint);
        // The window rim is mixed from the same tint, so it has to be re-mixed with it or it goes
        // on advertising the glass the dock used to have.
        ApplyWindowBorder();
    }
}
