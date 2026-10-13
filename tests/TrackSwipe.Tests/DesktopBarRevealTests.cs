using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class DesktopBarRevealTests
{
    [Fact]
    public void Overview_starts_collapsed_and_hover_expands_it()
    {
        var reveal = new DesktopBarReveal();
        Assert.Equal(0, reveal.Value(10));
        var motion = reveal.Set(true, 10)!;
        Assert.True(reveal.Animating(10.02));
        Assert.Equal(1, reveal.Value(10 + motion.Duration + 0.01));
        Assert.False(reveal.Animating(10 + motion.Duration + 0.01));
    }

    [Fact]
    public void Moving_away_mid_reveal_reverses_from_the_current_position()
    {
        var reveal = new DesktopBarReveal();
        reveal.Set(true, 10);
        var before = reveal.Value(10.07);
        var collapse = reveal.Set(false, 10.07)!;
        Assert.Equal(before, collapse.At(0), 8);
        Assert.Equal(0, reveal.Value(10.07 + collapse.Duration + 0.01));
        Assert.Null(reveal.Set(false, 11)); // normal mouse movement doesn't restart the animation
    }

    [Fact]
    public void Expanded_hover_region_allows_moving_from_labels_into_previews()
    {
        Assert.True(DesktopBarReveal.Hovering(30, 60, 240, false));
        Assert.False(DesktopBarReveal.Hovering(160, 60, 240, false));
        Assert.True(DesktopBarReveal.Hovering(160, 60, 240, true));
        Assert.False(DesktopBarReveal.Hovering(260, 60, 240, true));
    }
}
