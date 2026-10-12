using TrackSwipe.Core;

namespace TrackSwipe.Core.Tests;

public class GestureEngineTests
{
    [Theory]
    [InlineData(3, -45, 0, SwipeAction.NextDesktop)]
    [InlineData(3, 45, 0, SwipeAction.PreviousDesktop)]
    [InlineData(3, 0, -25, SwipeAction.SwipeUp)]
    [InlineData(3, 0, 25, SwipeAction.SwipeDown)]
    [InlineData(4, -45, 0, SwipeAction.NextDesktop)]
    [InlineData(4, 45, 0, SwipeAction.PreviousDesktop)]
    [InlineData(4, 0, -25, SwipeAction.SwipeUp)]
    [InlineData(4, 0, 25, SwipeAction.SwipeDown)]
    public void Selected_finger_count_supports_each_direction(int fingers, double dx, double dy, SwipeAction action)
    {
        var h = new GestureHarness(new GestureOptions { Fingers = fingers }) { Fingers = fingers };
        h.Move(dx, dy, 0.5).Lift();

        Assert.Equal(new[] { action }, h.Actions);
        Assert.Equal(GesturePhase.Idle, h.Engine.Phase);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void Unselected_finger_count_does_not_start_a_gesture(int selected, int contacts)
    {
        var h = new GestureHarness(new GestureOptions { Fingers = selected }) { Fingers = contacts };
        h.Move(-45, 0, 0.5).Lift();

        Assert.Empty(h.Actions);
        Assert.Equal(0, h.TrackingStarts);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void Changing_finger_count_after_reset_cancels_old_swipe_and_accepts_new_count(int previous, int selected)
    {
        var h = new GestureHarness(new GestureOptions { Fingers = previous }) { Fingers = previous };
        h.Move(-35, 0, 0.5);
        Assert.Equal(GesturePhase.Tracking, h.Engine.Phase);

        h.Engine.Reset();
        h.Engine.Options.Fingers = selected;
        h.Lift();
        Assert.Empty(h.Actions);

        h.Fingers = selected;
        h.Move(45, 0, 0.5).Lift();
        Assert.Equal(new[] { SwipeAction.PreviousDesktop }, h.Actions);
    }

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
    public void Candidate_fires_once_when_fingers_land_before_tracking()
    {
        var events = new List<string>();
        var h = new GestureHarness();
        h.Engine.CandidateStarted += () => events.Add("candidate");
        h.Engine.TrackingStarted += _ => events.Add("tracking");

        h.Move(-45, 0, 0.5).Lift();
        var wrongCount = new GestureHarness { Fingers = 3 };
        var wrongCountCandidates = 0;
        wrongCount.Engine.CandidateStarted += () => wrongCountCandidates++;
        wrongCount.Move(-45, 0, 0.5).Lift();

        Assert.Equal(new[] { "candidate", "tracking" }, events);
        Assert.Equal(0, wrongCountCandidates);
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

public class TrackingPresentationTests
{
    private static TouchFrame Report(double time, double progress, bool tip = true) => new(time,
        Enumerable.Range(0, 4).Select(i => new Contact(i, 30 + 12 * i, 40 - 40 * progress, tip, true)).ToArray(), 110, 70);

    private static GestureEngine Tracking()
    {
        var engine = new GestureEngine(new GestureOptions { DeadZoneMm = 0 });
        engine.Process(Report(10, 0));
        engine.Process(Report(10 + 1.0 / 60, 0.2));
        engine.Tick(10 + 1.0 / 60);
        return engine;
    }

    [Fact]
    public void Sixty_hertz_reports_produce_even_motion_on_a_120_hertz_display()
    {
        var engine = Tracking();
        var displayed = new List<double>();
        for (var tick = 1; tick <= 48; tick++)
        {
            var elapsed = tick / 120.0;
            var time = 10 + 1.0 / 60 + elapsed;
            if (tick % 2 == 0) engine.Process(Report(time, 0.2 + elapsed));
            engine.Tick(time);
            if (tick >= 8) displayed.Add(engine.DisplayProgress);
        }
        Assert.All(displayed.Zip(displayed.Skip(1), (a, b) => b - a), delta => Assert.InRange(delta, 0.006, 0.011));
        Assert.Equal(0.6, engine.Progress, 6); // presentation cannot change gesture decisions
    }

    [Theory]
    [InlineData(60, 0.5)]
    [InlineData(60, 3)]
    [InlineData(60, 5)]
    [InlineData(90, 3)]
    [InlineData(125, 3)]
    public void Fast_and_slow_tracking_remain_even_across_different_report_rates(double rate, double velocity)
    {
        var engine = new GestureEngine(new GestureOptions { DeadZoneMm = 0 });
        engine.Process(Report(10, 0));
        var started = 10 + 1 / rate;
        engine.Process(Report(started, 0.1));
        engine.Tick(started);
        var nextReport = 1 / rate;
        var displayed = new List<double>();
        for (var tick = 1; tick <= 16; tick++)
        {
            var elapsed = tick / 120.0;
            while (nextReport <= elapsed + 1e-9)
            {
                engine.Process(Report(started + nextReport, 0.1 + velocity * nextReport));
                nextReport += 1 / rate;
            }
            engine.Tick(started + elapsed);
            if (tick >= 6) displayed.Add(engine.DisplayProgress);
        }
        var expected = velocity / 120;
        Assert.All(displayed.Zip(displayed.Skip(1), (a, b) => b - a), delta => Assert.InRange(delta, expected * 0.75, expected * 1.25));
    }

    [Fact]
    public void Stalled_input_has_a_bounded_forecast()
    {
        var engine = Tracking();
        engine.Process(Report(10 + 2.0 / 60, 0.22));
        for (var tick = 0; tick < 40; tick++) engine.Tick(10 + 2.0 / 60 + tick / 120.0);
        Assert.InRange(engine.DisplayProgress, 0.22, 0.255);
        Assert.Equal(0.22, engine.Progress, 6);
    }

    [Fact]
    public void A_reversal_changes_display_direction_on_the_next_frame()
    {
        var engine = Tracking();
        engine.Process(Report(10 + 2.0 / 60, 0.22));
        engine.Tick(10 + 2.0 / 60);
        engine.Tick(10 + 2.5 / 60);
        var before = engine.DisplayProgress;
        engine.Process(Report(10 + 3.0 / 60, 0.20));
        engine.Tick(10 + 3.0 / 60);
        Assert.True(engine.DisplayProgress < before);
    }

    [Fact]
    public void Release_starts_at_the_last_displayed_position()
    {
        var engine = Tracking();
        engine.Process(Report(10 + 2.0 / 60, 0.22));
        engine.Tick(10 + 2.5 / 60);
        var before = engine.DisplayProgress;
        engine.Process(Report(10 + 2.6 / 60, 0.22, tip: false));
        Assert.Equal(GesturePhase.Settling, engine.Phase);
        Assert.Equal(before, engine.DisplayProgress, 9);
    }

    [Fact]
    public void A_stationary_report_stops_prediction()
    {
        var engine = Tracking();
        engine.Process(Report(10 + 2.0 / 60, 0.22));
        engine.Tick(10 + 2.5 / 60);
        engine.Process(Report(10 + 3.0 / 60, 0.22));
        engine.Tick(10 + 4.0 / 60);
        Assert.Equal(0.22, engine.DisplayProgress, 3);
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
