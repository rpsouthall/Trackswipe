using TrackSwipe.Core;

namespace TrackSwipe.Core.Tests;

public class GestureEngineTests
{
    [Fact]
    public void Full_swipe_left_commits_next_desktop()
    {
        var h = new GestureHarness().Move(-45, 0, 0.5).Lift();

        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
        Assert.Equal(GesturePhase.Idle, h.Engine.Phase);
    }

    [Fact]
    public void Full_swipe_right_commits_previous_desktop()
    {
        var h = new GestureHarness().Move(45, 0, 0.5).Lift();

        Assert.Equal(new[] { SwipeAction.PreviousDesktop }, h.Actions);
    }

    [Fact]
    public void Short_slow_swipe_cancels()
    {
        var h = new GestureHarness().Move(-10, 0, 0.6).Hold(0.2).Lift();

        Assert.Empty(h.Actions);
        Assert.Equal(1, h.TrackingStarts);
        Assert.Equal(0, h.Engine.DisplayProgress);
    }

    [Fact]
    public void Short_fast_flick_commits()
    {
        var h = new GestureHarness().Move(-12, 0, 0.06).Lift();

        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Fact]
    public void Reversing_mid_swipe_cancels()
    {
        var h = new GestureHarness().Move(-40, 0, 0.4).Move(38, 0, 0.4).Lift();

        Assert.Empty(h.Actions);
        Assert.True(h.MaxDisplay > 0.6, "panel should have followed the fingers out first");
    }

    [Fact]
    public void Releasing_after_passing_halfway_while_still_commits()
    {
        var h = new GestureHarness().Move(-35, 0, 0.5).Hold(0.3).Lift();

        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Fact]
    public void No_desktop_in_that_direction_rubber_bands_and_cancels()
    {
        var h = new GestureHarness();
        h.Engine.TrackingStarted += _ => h.Engine.CanGoPositive = false;

        h.Move(-50, 0, 0.5).Lift();

        Assert.Empty(h.Actions);
        Assert.InRange(h.MaxDisplay, 0.01, h.Engine.Options.RubberBandLimit);
    }

    [Fact]
    public void Wrong_finger_count_is_ignored()
    {
        var h = new GestureHarness { Fingers = 3 }.Move(-45, 0, 0.5).Lift();

        Assert.Empty(h.Actions);
        Assert.Equal(0, h.TrackingStarts);
    }

    [Fact]
    public void Jitter_inside_dead_zone_does_not_start_tracking()
    {
        var h = new GestureHarness();
        for (var i = 0; i < 10; i++) h.Move(1.5, 0.5, 0.03).Move(-1.5, -0.5, 0.03);
        h.Lift();

        Assert.Equal(0, h.TrackingStarts);
        Assert.Empty(h.Actions);
    }

    [Fact]
    public void Diagonal_swipe_is_rejected()
    {
        var h = new GestureHarness().Move(-30, 30, 0.4).Lift();

        Assert.Equal(0, h.TrackingStarts);
        Assert.Empty(h.Actions);
    }

    [Fact]
    public void Finger_lifting_early_does_not_make_progress_jump()
    {
        var h = new GestureHarness().Move(-20, 0, 0.2);
        h.Fingers = 3; // the rightmost finger lifts; a naive centroid would jump 6 mm
        h.Move(-20, 0, 0.2).Lift();

        var steps = h.ProgressHistory.Zip(h.ProgressHistory.Skip(1), (a, b) => Math.Abs(b - a));
        var expectedStep = 20.0 / 25 / h.Engine.Options.FullSwipeMm;
        Assert.All(steps, step => Assert.True(step < expectedStep * 1.5, $"progress jumped by {step:0.000}"));
        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Fact]
    public void Swipe_up_and_down_fire_discrete_actions()
    {
        var up = new GestureHarness().Move(0, -25, 0.3).Lift();
        var down = new GestureHarness().Move(0, 25, 0.3).Lift();

        Assert.Equal(new[] { SwipeAction.SwipeUp }, up.Actions);
        Assert.Equal(new[] { SwipeAction.SwipeDown }, down.Actions);
    }

    [Fact]
    public void Missing_lift_report_releases_after_timeout()
    {
        var h = new GestureHarness().Move(-45, 0, 0.5);

        for (var t = 0.0; t < 2 && h.Engine.Phase != GesturePhase.Idle; t += 1.0 / 120)
            h.Engine.Tick(h.Time + t);

        Assert.Equal(new[] { SwipeAction.NextDesktop }, h.Actions);
    }

    [Fact]
    public void Ended_fires_once_after_committed()
    {
        var events = new List<string>();
        var h = new GestureHarness();
        h.Engine.Committed += _ => events.Add("committed");
        h.Engine.Ended += () => events.Add("ended");

        h.Move(-45, 0, 0.5).Lift();

        Assert.Equal(new[] { "committed", "ended" }, events);
    }
}

public class SpringTests
{
    [Theory]
    [InlineData(0.3, 0.9)]
    [InlineData(0.5, 1.0)]
    public void Settles_on_target_within_a_second(double response, double damping)
    {
        var spring = new Spring(new SpringPreset(response, damping)) { Position = 0.4, Velocity = 2, Target = 1 };

        for (var i = 0; i < 120; i++) spring.Step(1.0 / 120);

        Assert.True(spring.IsAtRest);
        Assert.Equal(1, spring.Position, 2);
    }

    [Fact]
    public void Result_does_not_depend_on_frame_rate()
    {
        var at60 = new Spring(SpringPreset.Snappy) { Position = 0, Target = 1 };
        var at144 = new Spring(SpringPreset.Snappy) { Position = 0, Target = 1 };

        for (var i = 0; i < 6; i++) at60.Step(1.0 / 60);
        for (var i = 0; i < 14; i++) at144.Step(1.0 / 144);
        at144.Step(0.1 - 14.0 / 144);

        Assert.InRange(Math.Abs(at60.Position - at144.Position), 0, 1e-3);
    }
}
