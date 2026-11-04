using Anchor.Models;
using Xunit;

namespace Anchor.Tests.Models;

/// <summary>
/// The dock's geometry: the density scale, and the magnification curve every icon's size is read
/// off. <see cref="DockMetrics"/> is app-wide static state, so each test that changes the density
/// restores it — a leaked Large would silently change what every other sizing assertion in the
/// suite is comparing against.
/// </summary>
public class DockMetricsTests
{
    private static void WithDensity(DockDensity density, Action assert)
    {
        var original = DockMetrics.Density;
        try
        {
            DockMetrics.SetDensity(density);
            assert();
        }
        finally
        {
            DockMetrics.SetDensity(original);
        }
    }

    // ---- Density -----------------------------------------------------------

    [Fact]
    public void Medium_is_the_default_and_matches_the_Windows_11_taskbar()
    {
        // 24px icons in 40px cells. Changing this changes how Anchor looks for everyone who never
        // opens Settings, so it is worth pinning down.
        WithDensity(DockDensity.Medium, () =>
        {
            Assert.Equal(40, DockMetrics.Cell);
            Assert.Equal(24, DockMetrics.Icon);
        });
    }

    [Fact]
    public void Every_dimension_grows_with_the_density()
    {
        double[] cells = new double[3], icons = new double[3], glyphs = new double[3];
        double[] separators = new double[3], dividers = new double[3];
        double[] indicators = new double[3], corners = new double[3];

        var order = new[] { DockDensity.Small, DockDensity.Medium, DockDensity.Large };
        for (int i = 0; i < order.Length; i++)
        {
            int index = i;
            WithDensity(order[i], () =>
            {
                cells[index] = DockMetrics.Cell;
                icons[index] = DockMetrics.Icon;
                glyphs[index] = DockMetrics.Glyph;
                separators[index] = DockMetrics.SeparatorExtent;
                dividers[index] = DockMetrics.DividerLength;
                indicators[index] = DockMetrics.IndicatorLength;
                corners[index] = DockMetrics.CellCorner;
            });
        }

        // Nothing may shrink as the density grows, or one part of the strip would visually fight
        // the rest at that size.
        foreach (var (name, values) in new (string, double[])[]
                 {
                     (nameof(DockMetrics.Cell), cells),
                     (nameof(DockMetrics.Icon), icons),
                     (nameof(DockMetrics.Glyph), glyphs),
                     (nameof(DockMetrics.SeparatorExtent), separators),
                     (nameof(DockMetrics.DividerLength), dividers),
                     (nameof(DockMetrics.IndicatorLength), indicators),
                     (nameof(DockMetrics.CellCorner), corners),
                 })
        {
            Assert.True(values[0] < values[1] && values[1] < values[2],
                $"{name} did not grow across densities: {string.Join(" < ", values)}");
        }
    }

    [Theory]
    [InlineData(DockDensity.Small)]
    [InlineData(DockDensity.Medium)]
    [InlineData(DockDensity.Large)]
    public void An_icon_always_fits_inside_its_cell(DockDensity density)
    {
        // The cell is fixed and the icon is drawn inside it; an icon at or over the cell size
        // would have no padding at all and would touch its neighbours.
        WithDensity(density, () =>
        {
            Assert.True(DockMetrics.Icon < DockMetrics.Cell);
            Assert.True(DockMetrics.SeparatorExtent < DockMetrics.Cell);
            Assert.True(DockMetrics.DividerLength <= DockMetrics.Cell);
        });
    }

    [Fact]
    public void Changing_the_density_raises_Changed_only_when_it_actually_changes()
    {
        var original = DockMetrics.Density;
        int raised = 0;
        void Handler() => raised++;

        DockMetrics.Changed += Handler;
        try
        {
            DockMetrics.SetDensity(original);         // no-op
            Assert.Equal(0, raised);

            var other = original == DockDensity.Large ? DockDensity.Small : DockDensity.Large;
            DockMetrics.SetDensity(other);
            Assert.Equal(1, raised);

            DockMetrics.SetDensity(other);            // same again
            Assert.Equal(1, raised);
        }
        finally
        {
            DockMetrics.Changed -= Handler;
            DockMetrics.SetDensity(original);
        }
    }

    // ---- The magnification curve -------------------------------------------

    [Fact]
    public void The_icon_under_the_cursor_gets_the_full_peak()
    {
        Assert.Equal(DockMetrics.MagnifyPeak, DockMetrics.MagnificationAt(0), 6);
    }

    [Fact]
    public void The_swell_is_gone_at_and_beyond_the_reach()
    {
        Assert.Equal(1, DockMetrics.MagnificationAt(DockMetrics.MagnifyReach), 6);
        Assert.Equal(1, DockMetrics.MagnificationAt(DockMetrics.MagnifyReach + 5), 6);
    }

    [Fact]
    public void The_curve_is_symmetric_about_the_cursor()
    {
        // Icons the same distance either side of the cursor must be the same size, or the strip
        // would lean.
        for (double d = 0; d <= 3; d += 0.25)
            Assert.Equal(DockMetrics.MagnificationAt(d), DockMetrics.MagnificationAt(-d), 6);
    }

    [Fact]
    public void The_swell_falls_off_monotonically()
    {
        double previous = DockMetrics.MagnificationAt(0);
        for (double d = 0.1; d <= DockMetrics.MagnifyReach; d += 0.1)
        {
            double current = DockMetrics.MagnificationAt(d);
            Assert.True(current <= previous,
                $"magnification grew moving away from the cursor at {d} cells");
            previous = current;
        }
    }

    [Fact]
    public void The_curve_never_leaves_the_range_between_resting_and_peak()
    {
        for (double d = -4; d <= 4; d += 0.1)
        {
            double scale = DockMetrics.MagnificationAt(d);
            Assert.InRange(scale, 1, DockMetrics.MagnifyPeak);
        }
    }

    [Fact]
    public void A_zero_reach_disables_the_swell_instead_of_dividing_by_zero()
    {
        Assert.Equal(1, DockMetrics.MagnificationAt(0, reach: 0), 6);
        Assert.Equal(1, DockMetrics.MagnificationAt(1, reach: -1), 6);
    }

    // ---- The hovered cell's own curve ---------------------------------------
    //
    // The swell is confined to the icon the cursor is on. These pin the two ends of that: full
    // size across the middle of the cell, and back to resting by its edge — which is what keeps
    // the neighbours out of it.

    [Fact]
    public void The_hovered_icon_is_at_full_peak_across_the_middle_of_its_cell()
    {
        Assert.Equal(DockMetrics.MagnifyPeak, DockMetrics.HoverMagnificationAt(0), 6);
        Assert.Equal(DockMetrics.MagnifyPeak, DockMetrics.HoverMagnificationAt(DockMetrics.MagnifyHold), 6);
        Assert.Equal(DockMetrics.MagnifyPeak, DockMetrics.HoverMagnificationAt(-DockMetrics.MagnifyHold), 6);
    }

    [Fact]
    public void The_swell_is_back_to_resting_by_the_edge_of_the_hovered_cell()
    {
        // Half a cell away is the cell boundary. Anything at or past it — the gap between cells,
        // and every neighbouring cell — must be at resting size, or the swell would have spilled
        // onto icons the cursor is not on.
        Assert.Equal(1, DockMetrics.HoverMagnificationAt(0.5), 6);
        Assert.Equal(1, DockMetrics.HoverMagnificationAt(-0.5), 6);
        Assert.Equal(1, DockMetrics.HoverMagnificationAt(1.5), 6);
    }

    [Fact]
    public void The_hovered_curve_eases_between_the_plateau_and_the_cell_edge()
    {
        // Strictly shrinking across the outer quarter, so the icon settles back rather than
        // dropping off a step as the cursor leaves it.
        double previous = DockMetrics.HoverMagnificationAt(DockMetrics.MagnifyHold);
        for (double d = DockMetrics.MagnifyHold + 0.02; d <= 0.5; d += 0.02)
        {
            double current = DockMetrics.HoverMagnificationAt(d);
            Assert.True(current < previous, $"magnification did not shrink at {d} cells from centre");
            previous = current;
        }
    }
}
