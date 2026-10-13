using TrackSwipe.Core;

namespace TrackSwipe.Tests;

public class OverscrollTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0.5, 0.5, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(1.12, 1, 0.96)]
    [InlineData(2.08, 1, 0.928)]
    [InlineData(-0.06, 0, 1)]
    public void Overscroll_shrinks_windows_about_their_centres_with_bounded_resistance(double openness, double layout, double scale)
    {
        Assert.Equal(layout, OverviewPresentation.Layout(openness));
        var before = new LayoutRect(100, 200, 800, 500);
        var after = OverviewPresentation.WindowBounds(before, openness);
        Assert.Equal(before.Width * scale, after.Width, 6);
        Assert.Equal(before.Height * scale, after.Height, 6);
        Assert.Equal(before.X + before.Width / 2, after.X + after.Width / 2, 6);
        Assert.Equal(before.Y + before.Height / 2, after.Y + after.Height / 2, 6);
        Assert.InRange(after.Width / before.Width, 0.92, 1);
    }

    [Theory]
    [InlineData(1.08, 0, 1)]
    [InlineData(-1.08, 0, -1)]
    public void Releasing_beyond_the_limit_returns_gradually_instead_of_snapping(double position, double velocity, double target)
    {
        var motion = SettleMotion.Create(SpringPreset.Snappy, position, velocity, target);
        Assert.True(motion.Duration > 0.05);
        Assert.InRange(Math.Abs(motion.At(1.0 / 120) - position), 0, 0.01);
        Assert.Equal(target, motion.At(motion.Duration));
    }

    [Fact]
    public void Overview_flick_carries_momentum_past_the_limit_then_returns_to_it()
    {
        var motion = SettleMotion.Create(SpringPreset.Snappy, 0.95, 6, 1, allowOvershoot: true);
        Assert.True(motion.Samples.Max(sample => sample.Value) > 1.01);
        Assert.Equal(1, motion.At(motion.Duration));
        Assert.InRange(Math.Abs(motion.Samples[^2].Value - 1), 0, 0.003);
    }
}
