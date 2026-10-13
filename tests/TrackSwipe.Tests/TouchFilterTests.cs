using TrackSwipe.Touchpad;

namespace TrackSwipe.Tests;

public class TouchFilterTests
{
    [Fact]
    public void Pointer_movement_and_scrolling_never_reach_an_idle_engine()
    {
        var engaged = false;
        for (var i = 0; i < 100; i++) Assert.False(TouchFilter.Deliver(1, 3, forwardAll: false, ref engaged));
        for (var i = 0; i < 100; i++) Assert.False(TouchFilter.Deliver(2, 3, forwardAll: false, ref engaged));
        Assert.False(TouchFilter.Deliver(0, 3, forwardAll: false, ref engaged));
    }

    [Fact]
    public void Gesture_finger_reports_and_the_drop_below_are_delivered()
    {
        var engaged = false;
        Assert.True(TouchFilter.Deliver(3, 3, false, ref engaged));
        Assert.True(TouchFilter.Deliver(4, 3, false, ref engaged));
        Assert.True(TouchFilter.Deliver(2, 3, false, ref engaged)); // the lift
        Assert.False(TouchFilter.Deliver(2, 3, false, ref engaged));
        Assert.False(TouchFilter.Deliver(0, 3, false, ref engaged));
    }

    [Fact]
    public void Everything_is_delivered_while_a_gesture_or_the_monitor_needs_it()
    {
        var engaged = false;
        Assert.True(TouchFilter.Deliver(1, 3, true, ref engaged));
        Assert.True(TouchFilter.Deliver(0, 3, true, ref engaged));
        Assert.True(TouchFilter.Deliver(0, 3, false, ref engaged)); // state caught up: one more, then quiet
        Assert.False(TouchFilter.Deliver(0, 3, false, ref engaged));
    }

    /// <summary>
    /// The touchpad thread decides on engine state that can lag several reports behind the composition
    /// thread. Real gestures surrounded by pointer movement and scrolling must end exactly as without the filter.
    /// </summary>
    [Theory]
    [InlineData(-1, 0, 0, 4)]
    [InlineData(-1, 4, 0, 4)]
    [InlineData(1, 4, 0, 4)]
    [InlineData(0, 4, -1, 4)]
    [InlineData(0, 4, 1, 4)]
    [InlineData(-1, 4, 2, 4)]
    [InlineData(1, 0, 0, 3)]
    [InlineData(-1, 4, 0, 3)]
    [InlineData(0, 4, 1, 3)]
    public void Filtered_gestures_end_as_unfiltered_ones(int direction, int lag, int vertical, int fingers)
    {
        var trace = Trace(direction, vertical, fingers);
        var unfiltered = Run(trace, filter: false, lag, fingers);
        Assert.Contains("commit", unfiltered); // the trace really is a committed gesture
        Assert.Equal(unfiltered, Run(trace, filter: true, lag, fingers));
    }

    [Fact]
    public void Cancelled_and_rejected_touches_end_as_unfiltered_ones()
    {
        // Gesture fingers resting then lifting without moving; then five fingers (rejected) dropping to one.
        var trace = new List<(int Fingers, double X, double Y)>();
        for (var i = 0; i < 30; i++) trace.Add((1, i, 0));
        for (var i = 0; i < 30; i++) trace.Add((4, 0, 0));
        trace.Add((0, 0, 0));
        for (var i = 0; i < 20; i++) trace.Add((5, i * 2, 0));
        for (var i = 0; i < 30; i++) trace.Add((1, i, 0));
        trace.Add((0, 0, 0));
        for (var i = 0; i < 10; i++) trace.Add((4, 0, -i * 4.0));
        for (var i = 0; i < 10; i++) trace.Add((4, 0, -40 - i * 4.0));
        trace.Add((0, 0, 0));
        foreach (var lag in new[] { 0, 1, 4 })
            Assert.Equal(Run(trace, filter: false, lag, 4), Run(trace, filter: true, lag, 4));
    }

    private static List<(int Fingers, double X, double Y)> Trace(int direction, int vertical, int fingers)
    {
        var trace = new List<(int, double, double)>();
        for (var i = 0; i < 40; i++) trace.Add((1, i * 0.5, i * 0.3));        // pointer movement
        for (var i = 0; i < 20; i++) trace.Add((2, 0, i * 2.0));               // two-finger scroll
        trace.Add((0, 0, 0));
        for (var i = 0; i <= 32; i++)                                           // the swipe
            trace.Add((fingers, -direction * 40.0 * i / 32, -vertical * 30.0 * i / 32));
        trace.Add((fingers - 2, -direction * 40.0, -vertical * 30.0));          // staggered lift
        trace.Add((0, 0, 0));
        for (var i = 0; i < 60; i++) trace.Add((1, i * 0.5, 0));               // pointer again, during the settle
        return trace;
    }

    private static string Run(List<(int Fingers, double X, double Y)> trace, bool filter, int lag, int fingers)
    {
        var engine = new GestureEngine(new GestureOptions { Fingers = fingers });
        var events = new List<string>();
        engine.Committed += action => events.Add($"commit {action}");
        engine.TrackingStarted += axis => events.Add($"track {axis}");
        engine.Ended += () => events.Add("end");
        var phases = new Queue<GesturePhase>();
        var engaged = false;
        var time = 1000.0;
        foreach (var (count, x, y) in trace)
        {
            time += 1.0 / 125;
            // The touchpad thread sees the engine as it was `lag` reports ago.
            phases.Enqueue(engine.Phase);
            var seen = phases.Count > lag ? phases.Dequeue() : GesturePhase.Idle;
            if (!filter || TouchFilter.Deliver(count, fingers, seen != GesturePhase.Idle, ref engaged))
            {
                var contacts = Enumerable.Range(0, Math.Max(1, count))
                    .Select(i => new Contact(i, 30 + i * 12 + x, 40 + y, Tip: count > 0, Confidence: true)).ToArray();
                engine.Process(new TouchFrame(time, contacts, 110, 70));
            }
            engine.Tick(time);
        }
        for (var i = 0; i < 400 && engine.Phase != GesturePhase.Idle; i++) engine.Tick(time += 1.0 / 120);
        events.Add($"final {engine.Phase}");
        return string.Join(", ", events);
    }
}
