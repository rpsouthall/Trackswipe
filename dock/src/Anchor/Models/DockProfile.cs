namespace Anchor.Models;

/// <summary>
/// One dock strip: its items and everything about where and how it sits on screen.
/// <para>
/// Anchor can run several of these at once — one per monitor, or several on one — each with its
/// own items, edge and auto-hide behavior. Settings that belong to the <em>app</em> rather than
/// to a strip (theme, language, the global shortcut, start-with-Windows) stay on
/// <see cref="DockConfig"/>, so changing them changes them everywhere at once.
/// </para>
/// <para>
/// There is no monitor identifier here on purpose. A dock's monitor is implied by
/// <see cref="FreeX"/>/<see cref="FreeY"/> — the display those coordinates fall on — which
/// survives a reboot, a resolution change and a monitor being unplugged and plugged back in far
/// better than any display id or device name does. Moving a dock to another monitor is therefore
/// just writing coordinates on that monitor.
/// </para>
/// </summary>
public sealed class DockProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// What this dock is called in Settings and in the tray menu. Empty means "unnamed", which
    /// the UI renders as a generated label ("Dock 1") rather than a blank row.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public List<DockItem> Items { get; set; } = new();

    /// <summary>
    /// When true the dock is snapped flush to <see cref="Edge"/>. If <see cref="AutoHide"/> is
    /// also on it slides behind that edge and reveals on cursor approach; otherwise it stays
    /// pinned flush and fully visible. When false the dock floats freely at (<see cref="FreeX"/>,
    /// <see cref="FreeY"/>).
    /// </summary>
    public bool Snapped { get; set; }

    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>
    /// The dock's last top-left position in physical pixels. Kept up to date whether snapped or
    /// floating: when floating it is the free position; when snapped it records where the user
    /// dropped the dock so it re-appears at that spot (and on that monitor) rather than snapping
    /// back to the screen centre. Null only before the dock has ever been placed.
    /// </summary>
    public int? FreeX { get; set; }
    public int? FreeY { get; set; }

    /// <summary>When snapped, hide the dock behind the edge and reveal on hover.</summary>
    public bool AutoHide { get; set; } = true;

    /// <summary>
    /// How <see cref="AutoHide"/> decides to hide, as Dash to Dock's "intelligent autohide" does:
    /// the dock stays in view until a window of the app in front overlaps it, and comes back as
    /// soon as none does (hovering the edge still reveals it). Off, it hides whenever the cursor
    /// leaves. On by default.
    /// </summary>
    public bool DodgeWindows { get; set; } = true;

    /// <summary>
    /// Keep the dock centred as it grows and shrinks (apps opening and closing, items added), as
    /// the macOS Dock does: a snapped dock stays in the middle of its edge, and a floating one
    /// around its own middle. Off, a snapped dock keeps the spot along the edge it was dropped at,
    /// and a dock grows from its leading end. On by default.
    /// </summary>
    public bool KeepCentered { get; set; } = true;

    /// <summary>
    /// Keep the dock above other windows while it is <b>floating</b> (not snapped). When snapped
    /// the dock is always topmost regardless, so the auto-hide reveal works over other windows.
    /// </summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// When the dock is snapped to the <b>left or right</b> edge, arrange its icons vertically
    /// instead of horizontally. Top/bottom snapping and floating always stay horizontal. Off by
    /// default (the dock is horizontal everywhere).
    /// </summary>
    public bool VerticalWhenSideSnapped { get; set; }
}
