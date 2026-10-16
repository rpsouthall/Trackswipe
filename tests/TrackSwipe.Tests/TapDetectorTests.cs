using TrackSwipe.Core;
using TrackSwipe.Touchpad;

namespace TrackSwipe.Tests;

public class TapDetectorTests
{
    private const double FrameInterval = 1.0 / 125;

    /// <summary>Sends touchpad scans through the reader's filter (gestures of three fingers) to a tap detector.</summary>
    private sealed class Pad
    {
        private readonly TapDetector _detector = new();
        private readonly Dictionary<int, (double X, double Y)> _fingers = new();
        private bool _engaged;
        private double _touchStarted;

        public Pad() => _detector.Tapped += () => Taps++;

        public int Taps { get; private set; }
        public double Time { get; private set; } = 100;
        public TapDetector Detector => _detector;

        /// <summary>Puts fingers down (ids from 0), spaced across the pad.</summary>
        public Pad Land(params int[] ids)
        {
            foreach (var id in ids) _fingers[id] = (30 + 12 * id, 40);
            return Scan();
        }

        public Pad Lift(params int[] ids)
        {
            foreach (var id in ids) _fingers.Remove(id);
            return Scan();
        }

        public Pad Hold(double seconds)
        {
            for (var t = 0.0; t < seconds; t += FrameInterval) Scan();
            return this;
        }

        public Pad Move(double dx, double seconds)
        {
            var steps = Math.Max(1, (int)(seconds / FrameInterval));
            for (var i = 0; i < steps; i++)
            {
                foreach (var id in _fingers.Keys.ToList()) _fingers[id] = (_fingers[id].X + dx / steps, _fingers[id].Y);
                Scan();
            }
            return this;
        }

        private Pad Scan()
        {
            Time += FrameInterval;
            var contacts = _fingers.Select(f => new Contact(f.Key, f.Value.X, f.Value.Y, Tip: true, Confidence: true)).ToArray();
            // As the touchpad reader does: note when the touch began, and deliver only reports with a gesture's
            // fingers and the drop below them.
            if (contacts.Length == 0) _touchStarted = 0;
            else if (_touchStarted == 0) _touchStarted = Time;
            if (TouchFilter.Deliver(contacts.Length, 3, forwardAll: false, ref _engaged))
                _detector.Process(new TouchFrame(Time, contacts, 100, 70) { TouchStarted = _touchStarted });
            return this;
        }
    }

    [Fact]
    public void A_quick_still_three_finger_tap_is_recognised_once()
    {
        var pad = new Pad().Land(0, 1, 2).Hold(0.1).Lift(0, 1, 2);
        Assert.Equal(1, pad.Taps);
    }

    [Fact]
    public void Fingers_lifting_one_after_another_still_make_one_tap()
    {
        var pad = new Pad().Land(0, 1, 2).Hold(0.1).Lift(2).Hold(0.03).Lift(1).Hold(0.03).Lift(0);
        Assert.Equal(1, pad.Taps);
    }

    [Fact]
    public void Fingers_landing_a_moment_apart_still_tap()
    {
        var pad = new Pad().Land(0).Hold(0.03).Land(1).Hold(0.03).Land(2).Hold(0.06).Lift(0, 1, 2);
        Assert.Equal(1, pad.Taps);
    }

    [Fact]
    public void Two_taps_in_quick_succession_are_two()
    {
        var pad = new Pad().Land(0, 1, 2).Hold(0.1).Lift(0, 1, 2).Hold(0.15).Land(0, 1, 2).Hold(0.1).Lift(0, 1, 2);
        Assert.Equal(2, pad.Taps);
    }

    [Fact]
    public void Holding_the_fingers_down_is_not_a_tap()
    {
        var pad = new Pad().Land(0, 1, 2).Hold(0.6).Lift(0, 1, 2);
        Assert.Equal(0, pad.Taps);
    }

    [Fact]
    public void The_start_of_a_swipe_is_not_a_tap()
    {
        var pad = new Pad().Land(0, 1, 2).Move(6, 0.1).Lift(0, 1, 2);
        Assert.Equal(0, pad.Taps);
    }

    [Fact]
    public void A_tap_with_other_finger_counts_is_ignored()
    {
        Assert.Equal(0, new Pad().Land(0, 1, 2, 3).Hold(0.1).Lift(3).Lift(0, 1, 2).Taps);
        Assert.Equal(0, new Pad().Land(0, 1).Hold(0.1).Lift(0, 1).Taps);
    }

    [Fact]
    public void A_finger_joining_late_spoils_the_tap()
    {
        var pad = new Pad().Land(0, 1).Hold(0.2).Land(2).Hold(0.05).Lift(0, 1, 2);
        Assert.Equal(0, pad.Taps);
    }

    [Fact]
    public void A_cancelled_touch_is_not_a_tap()
    {
        var pad = new Pad().Land(0, 1, 2).Hold(0.05);
        pad.Detector.Cancel();
        pad.Hold(0.05).Lift(0, 1, 2);
        Assert.Equal(0, pad.Taps);
    }
}
