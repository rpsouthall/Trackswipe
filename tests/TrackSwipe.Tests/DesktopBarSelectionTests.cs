using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class DesktopBarSelectionTests
{
    [Theory]
    [InlineData(1, 0.25, 0.75, 0.25)]
    [InlineData(1, -0.75, 0.25, 0.75)]
    [InlineData(1, 1.2, 0, 1)]
    public void Pill_crossfades_towards_the_desktop_being_swiped_to(int current, double pan, float source, float target)
    {
        Assert.Equal(source, DesktopBarSelection.Weight(current, current, pan, 4));
        Assert.Equal(target, DesktopBarSelection.Weight(current, current + Math.Sign(pan), pan, 4));
    }

    [Fact]
    public void Blocked_overscroll_keeps_the_current_desktop_selected()
    {
        Assert.Equal(1, DesktopBarSelection.Weight(0, 0, -0.1, 4));
        Assert.Equal(0, DesktopBarSelection.Weight(0, 1, -0.1, 4));
    }

    [Fact]
    public void Compact_labels_are_centred_and_separated_by_a_small_fixed_gap()
    {
        var labels = DesktopBarSelection.Arrange(1800, 20, 28, new float[] { 60, 65, 70 }, 1);
        Assert.InRange(labels[1].Left - labels[0].Right, 9, 11);
        Assert.InRange(labels[2].Left - labels[1].Right, 9, 11);
        Assert.InRange(Math.Abs(labels[0].Left - (1800 - labels[^1].Right)), 0, 1);
        Assert.True(labels[^1].Right - labels[0].Left < 320);
    }
}
