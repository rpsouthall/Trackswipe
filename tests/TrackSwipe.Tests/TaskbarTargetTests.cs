using System.Drawing;
using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class TaskbarTargetTests
{
    private static readonly Rectangle Monitor = new(0, 0, 2496, 1664);

    private static PointF Centre(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Fact]
    public void Fallback_is_the_middle_of_a_bottom_taskbar()
    {
        var c = Centre(TaskbarTarget.Fallback(Monitor, Rectangle.FromLTRB(0, 0, 2496, 1616)));
        Assert.Equal(1248, c.X, 1);
        Assert.Equal(1640, c.Y, 1);
    }

    [Fact]
    public void Fallback_follows_a_taskbar_moved_to_the_top()
    {
        var c = Centre(TaskbarTarget.Fallback(Monitor, Rectangle.FromLTRB(0, 48, 2496, 1664)));
        Assert.Equal(1248, c.X, 1);
        Assert.Equal(24, c.Y, 1);
    }

    [Theory]
    [InlineData(80, 0, 2496, 1664, 40, 832)]   // left
    [InlineData(0, 0, 2416, 1664, 2456, 832)]  // right
    public void Fallback_follows_a_vertical_taskbar(int l, int t, int r, int b, float x, float y)
    {
        var c = Centre(TaskbarTarget.Fallback(Monitor, Rectangle.FromLTRB(l, t, r, b)));
        Assert.Equal(x, c.X, 1);
        Assert.Equal(y, c.Y, 1);
    }

    [Fact]
    public void Auto_hidden_taskbar_falls_back_to_the_bottom_edge()
    {
        var c = Centre(TaskbarTarget.Fallback(Monitor, Monitor));
        Assert.Equal(1248, c.X, 1);
        Assert.True(c.Y > 1600);
    }

    [Fact]
    public void Monitor_offsets_are_taken_out()
    {
        // A second monitor to the right, taskbar at its top: coordinates come back local to that monitor.
        var second = new Rectangle(2496, 0, 1920, 1080);
        var c = Centre(TaskbarTarget.Fallback(second, Rectangle.FromLTRB(2496, 40, 4416, 1080)));
        Assert.Equal(960, c.X, 1);
        Assert.Equal(20, c.Y, 1);
    }

    [Fact]
    public void Labelled_button_targets_its_icon_end()
    {
        // "Never combine": a wide button with the icon at its leading edge.
        var c = Centre(TaskbarTarget.Icon(new Rectangle(309, 0, 249, 48), Point.Empty));
        Assert.Equal(309 + 24, c.X, 1);
        Assert.Equal(24, c.Y, 1);
    }

    [Fact]
    public void Icon_only_button_targets_its_middle()
    {
        var c = Centre(TaskbarTarget.Icon(new Rectangle(1000, 1616, 48, 48), new Point(0, 0)));
        Assert.Equal(1024, c.X, 1);
        Assert.Equal(1640, c.Y, 1);
    }
}
