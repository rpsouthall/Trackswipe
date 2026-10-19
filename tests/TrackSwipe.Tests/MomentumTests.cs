using TrackSwipe.Core;

namespace TrackSwipe.Tests;

/// <summary>
/// Quick swipes as real fingers make them: a fast stroke, then the fingertips snapping back a few millimetres as they
/// leave the pad, together or one after another. They used to spring back ("forwards and backwards") when the snap back
/// was taken for a deliberate turn.
/// </summary>
public class MomentumTests
{
    private static GestureHarness ThreeFingers() => new(new GestureOptions { Fingers = 3 }) { Fingers = 3 };

    [Theory]
    [InlineData(8, 0.05, 2.0)]
    [InlineData(8, 0.08, 3.0)]
    [InlineData(12, 0.05, 4.0)]
    [InlineData(12, 0.12, 3.0)]
    [InlineData(18, 0.08, 4.0)]
    [InlineData(24, 0.12, 4.0)]
    public void A_quick_flick_whose_fingertips_snap_back_together_still_switches(double mm, double seconds, double snapBack)
    {
        var h = ThreeFingers().Move(-mm, 0, seconds).Move(snapBack, 0, 0.016).Lift();
        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Theory]
    [InlineData(8, 0.05, 3.0, 0.0)]
    [InlineData(12, 0.08, 4.0, 0.03)]
    [InlineData(18, 0.12, 4.0, 0.06)]
    public void A_quick_flick_whose_fingers_leave_one_by_one_still_switches(double mm, double seconds, double rollBack, double pause)
    {
        // The first finger lifts, the others roll back as they follow: what they do then isn't the gesture.
        var h = ThreeFingers().Move(-mm, 0, seconds).Hold(pause);
        h.Fingers = 2;
        h.Move(rollBack, 0, 0.024);
        h.Fingers = 1;
        h.Move(rollBack * 0.3, 0, 0.008).Lift();
        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Fact]
    public void A_short_brisk_swipe_switches()
    {
        // 8 mm in 120 ms, about 67 mm/s: a flick, not a drag.
        var h = ThreeFingers().Move(-8, 0, 0.12).Lift();
        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Theory]
    [InlineData(0.03)]
    [InlineData(0.08)]
    [InlineData(0.15)]
    public void Each_swipe_of_a_quick_series_moves_on_a_desktop(double gap)
    {
        // The second swipe lands while the first is still on its way: the first is completed, and the second goes on.
        var h = ThreeFingers();
        var advanced = 0;
        h.Engine.AdvanceRequested = (_, _) => { advanced++; return true; };
        h.Move(-10, 0, 0.07).LiftBriefly(gap);
        h.Fingers = 3;
        h.Move(-10, 0, 0.07).Lift();
        Assert.Equal(1, advanced);
        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Fact]
    public void Resting_on_a_caught_swipe_lets_it_finish_without_another()
    {
        var h = ThreeFingers();
        h.Engine.AdvanceRequested = (_, _) => true;
        h.Move(-10, 0, 0.07).LiftBriefly(0.05);
        h.Fingers = 3;
        h.Hold(0.2).Lift();
        Assert.Empty(h.Actions);
    }

    [Fact]
    public void A_deliberate_turn_back_still_cancels_however_fast_the_way_out()
    {
        var h = ThreeFingers().Move(-10, 0, 0.05).Move(8, 0, 0.08).Lift();
        Assert.Empty(h.Actions);
    }

    [Fact]
    public void Carrying_on_with_fewer_fingers_still_counts_the_whole_swipe()
    {
        // Not a quick lift: one finger up, the swipe continues slowly with two for a while, back towards the start.
        var h = ThreeFingers().Move(-30, 0, 0.3);
        h.Fingers = 2;
        h.Move(25, 0, 0.4);
        h.Fingers = 1;
        h.Lift();
        Assert.Empty(h.Actions);
    }
}
