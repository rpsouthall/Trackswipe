using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Anchor;

/// <summary>What the icon picker returned: a built-in glyph, or a path to a custom image file
/// picked via "Browse for an image…". Never both.</summary>
internal readonly record struct IconSelection(string? Glyph, string? FilePath);

/// <summary>
/// The small panels the dock opens over itself, and the edits its own windows apply back to it.
/// Partial of <see cref="DockWindow"/>.
/// <para>
/// The item editor is <em>not</em> here: it is a window of its own (<see cref="EditWindow"/>),
/// because the picker it opens has to be able to come and go without taking the edit with it. The
/// new-group window is the same way. What remains is the one panel small enough to live over the
/// strip itself: the shortcut capture.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    // ---- The panel ---------------------------------------------------------
    //
    // A panel is a popup that is NOT clipped to the dock window, hung off the dock the same way
    // the fly-out bars are. Both halves of that matter — getting either wrong is what left these
    // drawing inside the strip, where there is nothing to see.

    /// <summary>
    /// Wraps <paramref name="content"/> in a popup that is free of the dock window's bounds.
    /// <para>
    /// The dock is a strip one cell tall and only as wide as its own icons. A
    /// <see cref="Flyout"/> is by default clipped to the window it belongs to, so a swatch grid or
    /// a capture button opened against the dock was drawn <em>inside</em> that strip, which has
    /// room for none of it — what showed was a sliver of a panel behind the icons. (A
    /// <see cref="ContentDialog"/> fares worse still: it centres itself in the same tiny window.)
    /// With <see cref="FlyoutBase.ShouldConstrainToRootBounds"/> off, the popup gets its own
    /// top-level window and renders at full size over the desktop, exactly as the fly-out bars do.
    /// </para>
    /// </summary>
    private static Flyout PanelFlyout(FrameworkElement content) => new()
    {
        Content = content,
        ShouldConstrainToRootBounds = false,
    };

    /// <summary>
    /// Opens a <see cref="PanelFlyout"/> clear of the dock: anchored on the dock <em>window</em>
    /// at the same point the fly-out bars use (<see cref="BarAnchorPoint"/>), so it extends away
    /// from whichever screen edge the dock is snapped to — straight up for the common floating and
    /// bottom-snapped cases — lines up with the icon it was opened from, and never overlaps the
    /// strip's own glass.
    /// <para>
    /// Shown in <see cref="FlyoutShowMode.Standard"/> rather than left to <c>Auto</c>: every panel
    /// has something to type into or click, so it has to take focus rather than open transient
    /// under the cursor.
    /// </para>
    /// </summary>
    private void ShowPanelFlyout(Flyout flyout, FrameworkElement anchor)
    {
        var placement = GroupFlyoutPlacement;
        flyout.Placement = placement;

        // The cursor is about to leave the strip for the panel; hold auto-hide out while it is up,
        // the way the fly-out bars do, and re-arm on close.
        PauseAutoHideForDrag();
        flyout.Closed += (_, _) => ResumeAutoHideAfterDrag();

        flyout.ShowAt(RootGrid, new FlyoutShowOptions
        {
            Placement = placement,
            Position = BarAnchorPoint(anchor, placement),
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    /// <summary>The panel body every one of them shares: a titled column its own controls go
    /// into.</summary>
    private static StackPanel PanelBody(string header)
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        panel.Children.Add(FlyoutHeader(header));
        return panel;
    }

    /// <summary>
    /// Keeps the dock on screen while one of its own windows is editing it, and releases it
    /// afterwards. Auto-hide is polled from the cursor position, and the cursor is about to be
    /// somewhere else entirely — sliding the strip away underneath an open editor helps nobody.
    /// </summary>
    internal void HoldAutoHide(bool hold)
    {
        if (hold)
            PauseAutoHideForDrag();
        else
            ResumeAutoHideAfterDrag();
    }

    // ---- Change icon -------------------------------------------------------

    /// <summary>Applies a picker result to an item: a glyph or a file path, never both, and each
    /// clears whichever the other kind of custom icon was set.</summary>
    internal void ApplyIconSelection(DockItem item, IconSelection selection)
    {
        if (selection.Glyph is not null)
            SetCustomGlyph(item, selection.Glyph);
        else if (selection.FilePath is not null)
            SetCustomIcon(item, selection.FilePath);
    }

    // ---- The editor's result ----------------------------------------------

    /// <summary>
    /// Writes an edit from <see cref="EditWindow"/> back onto one of this dock's items: its name,
    /// its glyph colour, and — for everything except a group, which points at nothing — its target.
    /// <para>
    /// Re-classifying a new target can change the item's kind (a path swapped for a URL becomes a
    /// web link), so the icon is dropped and re-resolved, and the item is re-inserted at its own
    /// index to make the strip rebuild that one cell. Blank fields are ignored rather than
    /// applied: an item with no name is a cell with no tooltip, and one with no target does
    /// nothing when it is clicked.
    /// </para>
    /// </summary>
    /// <param name="target">The new target, or null for an item that has none.</param>
    /// <param name="iconColor">The glyph colour as <c>#RRGGBB</c>, or null for the theme's.</param>
    internal void ApplyItemEdit(DockItem item, string name, string? target, string? iconColor)
    {
        name = name.Trim();
        if (name.Length > 0)
            item.DisplayName = name;

        if (iconColor != item.IconColor)
            item.IconColor = iconColor;

        bool retargeted = false;
        if (target?.Trim() is { Length: > 0 } t && t != item.Target)
        {
            item.Target = t;
            item.Kind = DockItemFactory.Classify(t);
            item.IconImage = null;
            retargeted = true;

            int i = Items.IndexOf(item);
            if (i >= 0)
            {
                Items.RemoveAt(i);
                Items.Insert(i, item);
            }
        }

        SaveConfig();
        RaiseItemsChanged();
        if (retargeted)
            _ = LoadOneIconAsync(item);
    }
}
