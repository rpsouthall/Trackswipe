using TrackSwipe.Core;

namespace TrackSwipe.Tests;

/// <summary>
/// What is drawn while fingers move one way only ever moves that way. As on a real machine: touchpad reports at their
/// own rate, each display frame drawn for a moment some way ahead of the newest report it has (the compositor shows
/// it a frame or two later), and that distance varying from frame to frame.
/// </summary>
public class DisplaySteadinessTests
{
    /// <summary>Runs a steady swipe and returns what was drawn each display frame while the fingers were down.</summary>
    private static List<double> Drawn(double reportHz, double displayHz, double lead, double mmPerSecond, double seconds, bool vertical,
        double lateStart = 0)
    {
        var engine = new GestureEngine(new GestureOptions { Fingers = 3 });
        var drawn = new List<double>();
        var reportInterval = 1 / reportHz;
        var nextReport = 0;
        Contact[] Fingers(double travel) => Enumerable.Range(0, 3)
            .Select(i => new Contact(i, 30 + 12 * i + (vertical ? 0 : -travel), 40 + (vertical ? -travel : 0), true, true)).ToArray();
        for (var frame = 0; frame / displayHz < seconds; frame++)
        {
            var now = 100 + frame / displayHz;
            // Every report scanned by now has arrived (a first batch may arrive late: the reader was reading in batches).
            while (100 + nextReport * reportInterval <= now - (nextReport * reportInterval < lateStart ? lateStart : 0.001))
            {
                var time = 100 + nextReport * reportInterval;
                engine.Process(new TouchFrame(time, Fingers(mmPerSecond * nextReport * reportInterval), 110, 70));
                nextReport++;
            }
            engine.Tick(now + lead);
            if (engine.Phase == GesturePhase.Tracking) drawn.Add(engine.DisplayProgress);
        }
        return drawn;
    }

    [Theory]
    // report rate, display rate, how far ahead frames are drawn, finger speed, horizontal or vertical
    [InlineData(125, 120, 0.014, 220, true)]  // a quick swipe up on a 120 Hz display
    [InlineData(125, 120, 0.018, 220, true)]
    [InlineData(125, 120, 0.022, 300, false)] // a fast sideways swipe, frames drawn well ahead
    [InlineData(125, 60, 0.024, 220, true)]   // a 60 Hz display
    [InlineData(64, 120, 0.016, 220, false)]  // a slower touchpad (or timer-driven touches)
    [InlineData(125, 120, 0.016, 60, true)]   // slow and steady
    public void A_steady_swipe_is_never_drawn_moving_backwards(double reportHz, double displayHz, double lead, double mmPerSecond, bool vertical)
    {
        var drawn = Drawn(reportHz, displayHz, lead, mmPerSecond, 0.16, vertical);
        Assert.True(drawn.Count > 6);
        for (var i = 1; i < drawn.Count; i++)
            Assert.True(drawn[i] >= drawn[i - 1] - 1e-6, $"frame {i}: drawn at {drawn[i]:0.000} after {drawn[i - 1]:0.000}");
    }

    [Fact]
    public void Reports_that_arrive_late_in_a_batch_do_not_make_the_start_of_a_swipe_step_back()
    {
        var drawn = Drawn(125, 120, 0.016, 220, 0.16, vertical: true, lateStart: 0.032);
        for (var i = 1; i < drawn.Count; i++)
            Assert.True(drawn[i] >= drawn[i - 1] - 1e-6, $"frame {i}: drawn at {drawn[i]:0.000} after {drawn[i - 1]:0.000}");
    }

    /// <summary>
    /// Fingers don't leave the pad together: one goes, the others follow over the next few reports, slowing and
    /// rolling back a little as they do. What was drawn must not step back while they go, nor as the swipe sets off.
    /// </summary>
    [Theory]
    [InlineData(2, 0.040, -30, true)]  // two fingers left for 40 ms, rolling back
    [InlineData(2, 0.048, 0, false)]   // two left, still
    [InlineData(2, 0.016, -60, false)]
    [InlineData(2, 0.024, 40, true)]   // still moving on a little as they go
    public void Fingers_leaving_the_pad_one_after_another_never_draw_the_swipe_stepping_back(int left, double lifting, double rollMmPerSecond, bool vertical)
    {
        var engine = new GestureEngine(new GestureOptions { Fingers = 3 });
        var actions = new List<SwipeAction>();
        engine.Committed += actions.Add;
        var drawn = new List<double>();
        const double reportInterval = 1.0 / 125, swipe = 0.064, speed = 220, lead = 0.016;
        var nextReport = 0;
        for (var frame = 0; frame < 120; frame++)
        {
            var now = 100 + frame / 120.0;
            while (100 + nextReport * reportInterval <= now - 0.001)
            {
                var t = nextReport * reportInterval;
                var fingers = t < swipe ? 3 : t < swipe + lifting ? left : 0;
                var travel = t < swipe ? speed * t : speed * swipe + rollMmPerSecond * (t - swipe);
                var contacts = Enumerable.Range(0, fingers)
                    .Select(i => new Contact(i, 30 + 12 * i + (vertical ? 0 : -travel), 40 + (vertical ? -travel : 0), true, true)).ToArray();
                if (t <= swipe + lifting + reportInterval) engine.Process(new TouchFrame(100 + t, contacts, 110, 70));
                nextReport++;
            }
            engine.Tick(now + lead);
            if (engine.Phase is GesturePhase.Tracking or GesturePhase.Settling) drawn.Add(engine.DisplayProgress);
        }
        Assert.Single(actions); // it went
        Assert.True(drawn.Count > 10);
        for (var i = 1; i < drawn.Count; i++)
            Assert.True(drawn[i] >= drawn[i - 1] - 1e-6, $"frame {i}: drawn at {drawn[i]:0.000} after {drawn[i - 1]:0.000}");
    }

    [Fact]
    public void When_reports_stop_the_forecast_eases_away_rather_than_dropping()
    {
        var engine = new GestureEngine(new GestureOptions { Fingers = 3 });
        Contact[] Fingers(double travel) => Enumerable.Range(0, 3).Select(i => new Contact(i, 30 + 12 * i, 40 - travel, true, true)).ToArray();
        var time = 100.0;
        for (var i = 0; i < 12; i++, time += 0.008) engine.Process(new TouchFrame(time, Fingers(200 * i * 0.008), 110, 70));
        // No more reports: frames keep being drawn.
        var last = double.NaN;
        var largestStep = 0.0;
        for (var frame = 0; frame < 12; frame++)
        {
            engine.Tick(time + 0.010 + frame / 120.0);
            if (!double.IsNaN(last)) largestStep = Math.Max(largestStep, Math.Abs(engine.DisplayProgress - last));
            last = engine.DisplayProgress;
        }
        Assert.True(largestStep < 0.02, $"largest step between frames with no input was {largestStep:0.000}");
    }
}
