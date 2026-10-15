using System.Drawing;
using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class DropZoneTests
{
    // Two 100 px tiles 22 px apart, + at the right; scale 1, a 1000 px wide screen.
    private static readonly Rectangle[] Tiles = { new(100, 20, 100, 60), new(222, 20, 100, 60) };
    private static readonly Rectangle Add = new(900, 32, 36, 36);

    private static int? Drop(int x, int y)
    {
        var (zones, add) = DesktopBarLayout.DropZones(Tiles, Add, 1000, 1);
        for (var i = 0; i < zones.Length; i++) if (zones[i].Contains(x, y)) return i;
        return add is { } zone && zone.Contains(x, y) ? -1 : null;
    }

    [Theory]
    [InlineData(150, 50, 0)]   // on the first miniature
    [InlineData(70, 50, 0)]    // left of it
    [InlineData(210, 50, 0)]   // in the gap, nearer the first
    [InlineData(215, 50, 1)]   // in the gap, nearer the second
    [InlineData(150, 2, 0)]    // above it, at the top of the screen
    [InlineData(270, 130, 1)]  // below the second, past its label
    [InlineData(800, 50, -1)]  // well left of +
    [InlineData(990, 10, -1)]  // the corner beyond +
    public void Zones_are_generous(int x, int y, int expected) => Assert.Equal(expected, Drop(x, y));

    [Theory]
    [InlineData(500, 50)]  // between the last desktop and +: neither
    [InlineData(150, 200)] // well down into the spread
    public void Zones_stop_somewhere(int x, int y) => Assert.Null(Drop(x, y));
}

public class NewDesktopSlotTests
{
    // Three 100 px tiles from x = 300, + at the right; scale 1, a 1200 px wide screen.
    private static readonly Rectangle[] Tiles = { new(300, 20, 100, 60), new(422, 20, 100, 60), new(544, 20, 100, 60) };
    private static readonly Rectangle Add = new(1100, 32, 36, 36);

    private static int? Slot(int x, int y, int current)
    {
        var (zones, add) = DesktopBarLayout.DropZones(Tiles, Add, 1200, 1);
        return DesktopBarLayout.NewDesktopSlot(Tiles, zones, add, current, new Point(x, y));
    }

    [Theory]
    [InlineData(100, 50, 1, 1)] // empty bar left of the desktops: just before the current one
    [InlineData(100, 50, 0, 0)]
    [InlineData(850, 50, 1, 2)] // empty bar right of the desktops: just after the current one
    [InlineData(850, 50, 2, 3)]
    [InlineData(10, 2, 2, 2)]   // the far top-left corner still counts as the bar
    public void Empty_bar_space_makes_a_desktop_next_to_the_current_one(int x, int y, int current, int expected) =>
        Assert.Equal(expected, Slot(x, y, current));

    [Theory]
    [InlineData(350, 50)]  // over a desktop: that's a drop onto it
    [InlineData(1110, 50)] // over +: that's +
    [InlineData(100, 300)] // below the bar, in the spread
    public void Only_empty_bar_space_counts(int x, int y) => Assert.Null(Slot(x, y, 1));
}
