using TrackSwipe.Core;

namespace TrackSwipe.Tests;

/// <summary>A swipe is one movement: it doesn't set off and come back because of what the touchpad or a slow hand did.</summary>
public class GestureSteadinessTests
{
    private static GestureHarness ThreeFingers() => new(new GestureOptions { Fingers = 3 }) { Fingers = 3 };

    [Theory]
    [InlineData(1)] // two of three fingers gone: released there and then, the last one still down
    [InlineData(0)]
    public void A_swipe_is_released_the_moment_most_of_its_fingers_have_gone(int remaining)
    {
        var swipe = ThreeFingers();
        swipe.Move(-25, 0, 0.2);
        swipe.Fingers = remaining;
        swipe.Hold(0.008);
        Assert.Equal(GesturePhase.Settling, swipe.Engine.Phase); // no waiting: nothing held up the swipe setting off
    }

    [Fact]
    public void One_finger_lifting_early_does_not_release_the_swipe()
    {
        var swipe = ThreeFingers();
        swipe.Move(-20, 0, 0.15);
        swipe.Fingers = 2;
        swipe.Move(-2, 0, 0.04);
        Assert.Equal(GesturePhase.Tracking, swipe.Engine.Phase);
        swipe.Lift();
        Assert.Equal(new[] { SwipeAction.NextDesktop }, swipe.Actions);
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
