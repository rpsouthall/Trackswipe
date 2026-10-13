using System.Drawing;
using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class RestoreAndReorderTests
{
    [Fact]
    public void Moving_third_to_second_preserves_desktop_identity_and_other_desktops_order()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var moved = DesktopReorder.Move(ids, ids[2], 1);
        Assert.Equal(new[] { ids[0], ids[2], ids[1], ids[3] }, moved);
        Assert.Equal(1, DesktopBarLayout.ViewedIndex(moved, ids[2], ids[0], 0));
        Assert.Equal(ids, DesktopReorder.Move(moved, ids[2], 2));
        Assert.Equal(ids, DesktopReorder.Move(ids, Guid.NewGuid(), 1));
        Assert.Equal(ids, DesktopReorder.Move(ids, ids[2], -1));
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(199, 0)]
    [InlineData(201, 1)]
    [InlineData(399, 1)]
    [InlineData(401, 2)]
    [InlineData(2000, 2)]
    public void Drop_position_crosses_neighbour_midpoints_and_clamps_to_ends(float x, int target)
    {
        var slots = new[] { new Rectangle(50, 20, 100, 60), new Rectangle(250, 20, 100, 60), new Rectangle(450, 20, 100, 60) };
        Assert.Equal(target, DesktopReorder.Target(slots, x));
    }

    [Fact]
    public void Hold_or_deliberate_movement_starts_drag_without_consuming_a_short_click()
    {
        var press = new Point(100, 50);
        Assert.False(DesktopReorder.Starts(press, new Point(103, 52), 1.5f, 0.1));
        Assert.True(DesktopReorder.Starts(press, press, 1.5f, 0.25));
        Assert.True(DesktopReorder.Starts(press, new Point(108, 50), 1.5f, 0.02));
    }

    [Theory]
    [InlineData(null, "Desktop 2")]
    [InlineData("Desktop 3", "Desktop 2")]
    [InlineData("Work", "Work")]
    [InlineData("Desktop research", "Desktop research")]
    public void Default_labels_follow_position_and_custom_labels_survive(string? label, string expected) =>
        Assert.Equal(expected, DesktopReorder.Label(1, label));

    [Fact]
    public void Window_and_viewport_move_and_scale_together_inside_dragged_desktop()
    {
        var tile = new RectangleF(100, 20, 200, 100);
        var lifted = new RectangleF(400, 15, 208, 104);
        Assert.Equal(lifted, DesktopReorder.Map(tile, tile, lifted));
        Assert.Equal(new RectangleF(452, 41, 104, 52), DesktopReorder.Map(new RectangleF(150, 45, 100, 50), tile, lifted));
    }

    [Fact]
    public void Snapped_restore_geometry_is_used_only_for_the_same_window_and_placement()
    {
        var history = new RestoreFrameHistory();
        var window = new IntPtr(42);
        var normal = new Rectangle(100, 150, 800, 600);
        var snapped = new Rectangle(0, 0, 1248, 1592);
        history.Remember(window, normal, snapped, 7);
        Assert.Equal(snapped, history.Find(window, normal, 7));
        Assert.Null(history.Find(window, normal, 8));
        Assert.Null(history.Find(window, new Rectangle(200, 150, 800, 600), 7));
        history.Prune(Array.Empty<IntPtr>());
        Assert.Null(history.Find(window, normal, 7));
    }

    [Fact]
    public void Clipped_gpu_cover_stays_aligned_with_window_during_correction()
    {
        var source = new Rectangle(-100, 20, 400, 200);
        var captured = new Rectangle(0, 20, 300, 200);
        Assert.Equal(new RectangleF(250, 100, 150, 100), RestoreCoverLayout.Map(new RectangleF(200, 100, 200, 100), source, captured));
    }
}
