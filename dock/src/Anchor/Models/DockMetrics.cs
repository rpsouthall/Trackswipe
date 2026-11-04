namespace Anchor.Models;

/// <summary>How big the dock's cells are. <see cref="Medium"/> matches the Windows 11 taskbar
/// (24 px icons in 40 px cells) and is the default.</summary>
public enum DockDensity
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// The dock's geometry, derived from the chosen <see cref="Density"/>. Every place that draws a
/// cell — the strip's item template, a group's fly-out bar, the quick-launch list, the window
/// sizing math — reads its numbers from here, so a density change moves all of them together and
/// none of them can drift out of step with the others.
/// <para>
/// Static because density is an <em>app</em> setting shared by every dock, and because the item
/// template binds to per-item properties that would otherwise each need a path back up to their
/// window. <see cref="Changed"/> is how those bindings learn to re-read (see
/// <c>DockItem.RefreshMetrics</c>).
/// </para>
/// </summary>
public static class DockMetrics
{
    public static DockDensity Density { get; private set; } = DockDensity.Medium;

    /// <summary>Raised after <see cref="SetDensity"/> actually changes the geometry.</summary>
    public static event Action? Changed;

    public static void SetDensity(DockDensity density)
    {
        if (Density == density)
            return;
        Density = density;
        Changed?.Invoke();
    }

    /// <summary>The square cell a launchable item occupies (the taskbar's 40 px at Medium).</summary>
    public static double Cell => Density switch
    {
        DockDensity.Small => 32,
        DockDensity.Large => 52,
        _ => 40,
    };

    /// <summary>The bitmap icon drawn inside a cell.</summary>
    public static double Icon => Density switch
    {
        DockDensity.Small => 20,
        DockDensity.Large => 32,
        _ => 24,
    };

    /// <summary>The fallback glyph's font size. Slightly under <see cref="Icon"/> because a
    /// Segoe Fluent glyph is drawn inside its em box and reads larger than a bitmap at the same
    /// nominal size.</summary>
    public static double Glyph => Density switch
    {
        DockDensity.Small => 15,
        DockDensity.Large => 24,
        _ => 18,
    };

    /// <summary>A separator's slot along the strip's flow — a divider, not a cell.</summary>
    public static double SeparatorExtent => Density switch
    {
        DockDensity.Small => 11,
        DockDensity.Large => 16,
        _ => 13,
    };

    /// <summary>The length of a hairline drawn across the strip (a separator, or the gear divider).</summary>
    public static double DividerLength => Density switch
    {
        DockDensity.Small => 18,
        DockDensity.Large => 32,
        _ => 24,
    };

    /// <summary>The running-app indicator under an open app's icon.</summary>
    public static double IndicatorLength => Density switch
    {
        DockDensity.Small => 10,
        DockDensity.Large => 16,
        _ => 12,
    };

    /// <summary>The rounded corner on a cell's hover/press chrome.</summary>
    public static double CellCorner => Density switch
    {
        DockDensity.Small => 6,
        DockDensity.Large => 10,
        _ => 8,
    };

    // ---- Magnification -----------------------------------------------------

    /// <summary>How much bigger the icon directly under the cursor gets.</summary>
    public const double MagnifyPeak = 1.55;

    /// <summary>
    /// How far the swell reaches, in cells: exactly half a cell, so it dies at the boundary of the
    /// cell the cursor is in and <b>only the icon under the cursor ever grows</b>. The classic
    /// macOS dock spills the swell onto a couple of neighbours either side; here that read as the
    /// dock wobbling around the icon you were actually aiming at, so the effect is confined to it.
    /// </summary>
    public const double MagnifyReach = 0.5;

    /// <summary>
    /// How much of the cell, measured from its centre, stays at the full <see cref="MagnifyPeak"/>
    /// before the swell starts easing back down. Without it the icon would be full size only at
    /// the one pixel dead-centre of its cell and visibly smaller everywhere else in it — the
    /// plateau is what makes hovering an icon feel like a state rather than a knife edge.
    /// </summary>
    public const double MagnifyHold = 0.25;

    /// <summary>
    /// The size the hovered icon is drawn at, from how far the cursor sits off the centre of
    /// <em>its own</em> cell: full peak across the middle half, easing to 1 at the cell's edges so
    /// there is no jump as the cursor crosses from one cell into the gap or the next.
    /// </summary>
    /// <param name="cells">Distance from the cell's centre, in cell widths (so ±0.5 is its edge).</param>
    public static double HoverMagnificationAt(double cells) =>
        MagnificationAt(Math.Max(0, Math.Abs(cells) - MagnifyHold), reach: MagnifyReach - MagnifyHold);

    /// <summary>
    /// The magnification an icon gets when the cursor is <paramref name="cells"/> cells away from
    /// its centre. Cosine-shaped rather than linear so the swell eases in and out instead of
    /// arriving at a corner: it is exactly <see cref="MagnifyPeak"/> under the cursor, exactly 1
    /// at <see cref="MagnifyReach"/> and beyond, and its slope is zero at both ends — which is
    /// what stops the strip from visibly kinking as the cursor crosses a cell boundary.
    /// </summary>
    /// <param name="cells">Distance from the icon's centre, in cell widths. Sign is ignored.</param>
    public static double MagnificationAt(double cells, double peak = MagnifyPeak, double reach = MagnifyReach)
    {
        cells = Math.Abs(cells);
        if (reach <= 0 || cells >= reach)
            return 1;
        return 1 + (peak - 1) * (1 + Math.Cos(Math.PI * cells / reach)) / 2;
    }
}
