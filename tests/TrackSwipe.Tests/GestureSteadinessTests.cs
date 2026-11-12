using TrackSwipe.Core;

namespace TrackSwipe.Tests;

/// <summary>A swipe is one movement: it doesn't set off and come back because of what the touchpad or a slow hand did.</summary>
public class GestureSteadinessTests
{
    private static GestureHarness ThreeFingers() => new(new GestureOptions { Fingers = 3 }) { Fingers = 3 };

    [Theory]
    [InlineData(1, 0.016)] // two of three fingers lost for a couple of reports
    [InlineData(1, 0.040)]
    [InlineData(2, 0.040)] // one lost: always tolerated
    public void Fingers_the_touchpad_loses_for_a_moment_do_not_release_the_swipe(int remaining, double seconds)
    {
        var swipe = ThreeFingers();
        swipe.Move(-20, 0, 0.15);
        swipe.Fingers = remaining;
        swipe.Move(-2, 0, seconds);
        Assert.Equal(GesturePhase.Tracking, swipe.Engine.Phase); // not sent on its way, to be caught again
        swipe.Fingers = 3;
        swipe.Move(-10, 0, 0.08);
        Assert.Equal(GesturePhase.Tracking, swipe.Engine.Phase);
        Assert.Equal(remaining == 1 ? 1 : 0, swipe.Engine.DropoutsAbsorbed);
        // The panel only ever moved forwards.
        for (var i = 1; i < swipe.ProgressHistory.Count; i++)
            Assert.True(swipe.ProgressHistory[i] >= swipe.ProgressHistory[i - 1] - 1e-9, $"progress went back at sample {i}");
        swipe.Lift();
        Assert.Equal(new[] { SwipeAction.NextDesktop }, swipe.Actions);
    }

    [Fact]
    public void Lifting_all_but_one_finger_and_resting_it_releases_after_a_moment()
    {
        var swipe = ThreeFingers();
        swipe.Move(-25, 0, 0.2);
        swipe.Fingers = 1;
        swipe.Hold(0.03);
        Assert.Equal(GesturePhase.Tracking, swipe.Engine.Phase); // could still be the touchpad
        swipe.Hold(0.05);
        Assert.Equal(GesturePhase.Settling, swipe.Engine.Phase); // no: they let go
        swipe.Lift();
        Assert.Equal(new[] { SwipeAction.NextDesktop }, swipe.Actions);
    }

    [Fact]
    public void Every_finger_lifting_at_once_releases_at_once()
    {
        var swipe = ThreeFingers();
        swipe.Move(-25, 0, 0.2);
        swipe.Fingers = 0;
        swipe.Hold(0.008);
        Assert.Equal(GesturePhase.Settling, swipe.Engine.Phase);
    }

    [Theory]
    [InlineData(-10, 0.5)] // short and slow: neither far enough nor fast enough for the old rules
    [InlineData(-12, 0.25)]
    [InlineData(-30, 0.3)]
    public void A_swipe_up_that_heads_up_opens_however_slow(double mm, double seconds)
    {
        var swipe = ThreeFingers().Move(0, mm, seconds).Lift();
        Assert.Equal(new[] { SwipeAction.SwipeUp }, swipe.Actions);
    }

    [Fact]
    public void A_swipe_down_that_heads_down_goes_however_slow()
    {
        var swipe = ThreeFingers().Move(0, 10, 0.5).Lift();
        Assert.Equal(new[] { SwipeAction.SwipeDown }, swipe.Actions);
    }

    [Fact]
    public void A_drift_of_the_fingers_up_or_down_does_nothing()
    {
        Assert.Empty(ThreeFingers().Move(0, -5, 0.4).Lift().Actions);
        Assert.Empty(ThreeFingers().Move(0, 5, 0.4).Lift().Actions);
    }

    [Fact]
    public void A_quick_flick_up_opens_even_when_short()
    {
        var swipe = ThreeFingers().Move(0, -5, 0.04).Lift();
        Assert.Equal(new[] { SwipeAction.SwipeUp }, swipe.Actions);
    }

    [Fact]
    public void A_swipe_up_caught_by_turning_back_returns()
    {
        var swipe = ThreeFingers().Move(0, -20, 0.2).Move(0, 12, 0.2).Lift();
        Assert.Empty(swipe.Actions);
    }

    [Fact]
    public void Once_released_a_swipe_up_never_moves_back_towards_where_it_started()
    {
        var swipe = ThreeFingers().Move(0, -10, 0.4);
        var released = swipe.Engine.DisplayProgress;
        swipe.Lift();
        Assert.Equal(new[] { SwipeAction.SwipeUp }, swipe.Actions);
        Assert.True(swipe.MaxDisplay >= released);
    }
}
