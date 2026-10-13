using TrackSwipe.Core;

namespace TrackSwipe.Tests;

public class TrackingTakeoverTests
{
    private static TouchFrame Report(double time, double y, bool tip = true) => new(time,
        Enumerable.Range(0, 4).Select(i => new Contact(i, 30 + i * 12, y, tip, true)).ToArray(), 110, 70);

    [Fact]
    public void A_small_deliberate_swipe_starts_without_the_old_five_millimetre_wait()
    {
        var engine = new GestureEngine();
        engine.Process(Report(10, 50));
        engine.Process(Report(10.008, 48.8));
        Assert.Equal(GesturePhase.Tracking, engine.Phase);
        Assert.Equal(GestureAxis.Vertical, engine.Axis);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void New_fingers_take_over_the_displayed_curve_without_a_jump_including_rubber_banding(bool canGo)
    {
        var engine = new GestureEngine();
        engine.TrackingStarted += _ => engine.CanGoPositive = canGo;
        engine.Process(Report(10, 50));
        engine.Process(Report(10.008, 26));
        engine.Process(Report(10.024, 26));
        engine.Tick(10.028);
        SettleMotion? motion = null;
        engine.SettlingStarted += value => motion = value;
        engine.Process(Report(10.032, 26, false));
        Assert.Equal(GesturePhase.Settling, engine.Phase);
        var interruptions = 0;
        engine.SettlingInterrupted += () => interruptions++;
        var start = engine.SettleStartedAt;
        engine.Process(Report(start + 0.020, 50));
        Assert.Equal(GesturePhase.Tracking, engine.Phase);
        engine.Tick(start + 0.028);
        Assert.Equal(motion!.At(0.028), engine.DisplayProgress, 6);
        Assert.Equal(1, interruptions);
        var before = engine.DisplayProgress;
        engine.Process(Report(start + 0.036, 54));
        engine.Tick(start + 0.040);
        Assert.True(engine.DisplayProgress < before);
    }

    [Fact]
    public void Rapid_reversal_moves_back_without_a_large_opposite_forecast()
    {
        var engine = new GestureEngine(new GestureOptions { DeadZoneMm = 0 });
        engine.Process(Report(10, 50));
        engine.Process(Report(10.008, 46));
        engine.Process(Report(10.016, 42));
        engine.Tick(10.020);
        var before = engine.DisplayProgress;
        engine.Process(Report(10.024, 43));
        engine.Tick(10.028);
        Assert.True(engine.DisplayProgress < before);
        Assert.InRange(engine.DisplayProgress, engine.Progress - 0.002, engine.Progress + 0.01);
    }

    [Fact]
    public void A_single_quantized_repeat_preserves_slow_motion_but_a_real_stop_settles()
    {
        var engine = new GestureEngine(new GestureOptions { DeadZoneMm = 0 });
        engine.Process(Report(10, 50));
        engine.Process(Report(10.008, 46));
        engine.Process(Report(10.016, 45.96));
        engine.Process(Report(10.024, 45.92));
        engine.Process(Report(10.032, 45.92));
        engine.Tick(10.036);
        Assert.True(engine.DisplayProgress > engine.Progress);
        engine.Process(Report(10.040, 45.92));
        engine.Process(Report(10.048, 45.92));
        engine.Tick(10.056);
        Assert.Equal(engine.Progress, engine.DisplayProgress, 4);
    }

    [Fact]
    public void Lift_anchors_the_curve_to_the_displayed_frame_instead_of_an_older_input_timestamp()
    {
        var engine = new GestureEngine(new GestureOptions { DeadZoneMm = 0 });
        engine.Process(Report(10, 50));
        engine.Process(Report(10.008, 44));
        engine.Process(Report(10.016, 38));
        engine.Tick(10.030); // predicted presentation is ahead of the next hardware report
        var before = engine.DisplayProgress;
        SettleMotion? motion = null;
        engine.SettlingStarted += value => motion = value;
        engine.Process(Report(10.024, 38, false));
        Assert.Equal(10.030, engine.SettleStartedAt, 6);
        engine.Tick(10.030);
        Assert.Equal(before, engine.DisplayProgress, 9);
        Assert.Equal(before, motion!.At(0), 9);
    }

    [Fact]
    public void Lift_after_a_reversal_does_not_inherit_the_old_forward_velocity()
    {
        var engine = new GestureEngine(new GestureOptions { DeadZoneMm = 0 });
        engine.Process(Report(10, 50));
        for (var i = 1; i <= 8; i++)
        {
            engine.Process(Report(10 + i * 0.008, 50 - i * 2));
            engine.Tick(10 + i * 0.008 + 0.002);
        }
        engine.Process(Report(10.072, 37));
        engine.Tick(10.074);
        Assert.True(engine.Velocity > 0); // commit history still sees the long upward movement
        SettleMotion? motion = null;
        engine.SettlingStarted += value => motion = value;
        engine.Process(Report(10.080, 37, false));
        Assert.True(motion!.At(1.0 / 240) < motion.At(0));
    }
}
