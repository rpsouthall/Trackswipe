using TrackSwipe.Core;

namespace TrackSwipe.Tests;

public class ExposeLayoutTests
{
    private static readonly LayoutRect Area = new(100, 300, 2300, 1200);

    [Fact]
    public void Single_window_is_centred_and_never_enlarged()
    {
        var window = new LayoutRect(0, 0, 800, 600);

        var placed = ExposeLayout.Arrange(new[] { window }, Area, gap: 40).Single();

        Assert.Equal(800, placed.Width, 6);
        Assert.Equal(Area.CenterX, placed.CenterX, 6);
        Assert.Equal(Area.CenterY, placed.CenterY, 6);
    }

    [Fact]
    public void Maximised_windows_shrink_to_fit_without_overlapping()
    {
        var windows = Enumerable.Range(0, 7).Select(_ => new LayoutRect(-11, -11, 2518, 1686)).ToArray();

        var placed = ExposeLayout.Arrange(windows, Area, gap: 40);

        AssertFitsWithoutOverlap(placed);
        Assert.All(placed, rect => Assert.Equal(2518.0 / 1686, rect.Width / rect.Height, 6));
    }

    [Fact]
    public void Mixed_sizes_fit_and_keep_aspect_ratios()
    {
        var windows = new[]
        {
            new LayoutRect(0, 0, 2496, 1600),
            new LayoutRect(200, 100, 900, 700),
            new LayoutRect(1500, 900, 600, 900),
            new LayoutRect(50, 1200, 1200, 300),
        };

        var placed = ExposeLayout.Arrange(windows, Area, gap: 40);

        AssertFitsWithoutOverlap(placed);
        for (var i = 0; i < windows.Length; i++)
            Assert.Equal(windows[i].Width / windows[i].Height, placed[i].Width / placed[i].Height, 6);
    }

    [Fact]
    public void Left_to_right_order_is_kept_within_a_row()
    {
        var windows = new[] { new LayoutRect(1600, 0, 500, 400), new LayoutRect(0, 0, 500, 400) };

        var placed = ExposeLayout.Arrange(windows, Area, gap: 40);

        Assert.True(placed[1].X < placed[0].X);
    }

    private static void AssertFitsWithoutOverlap(LayoutRect[] placed)
    {
        foreach (var rect in placed)
        {
            Assert.InRange(rect.X, Area.X - 1e-6, Area.Right);
            Assert.InRange(rect.Right, Area.X, Area.Right + 1e-6);
            Assert.InRange(rect.Y, Area.Y - 1e-6, Area.Bottom);
            Assert.InRange(rect.Bottom, Area.Y, Area.Bottom + 1e-6);
        }
        for (var i = 0; i < placed.Length; i++)
            for (var j = i + 1; j < placed.Length; j++)
                Assert.False(placed[i].Overlaps(placed[j]), $"windows {i} and {j} overlap");
    }
}
