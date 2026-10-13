using System.Drawing;
using System.Numerics;
using TrackSwipe.Core;
using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class CompositorMotionTests
{
    [Theory]
    [InlineData(0.2, 3, 1)]
    [InlineData(-0.2, -3, -1)]
    [InlineData(0.2, 0, 0)]
    [InlineData(-0.2, 0, 0)]
    [InlineData(1, 0, 0)]
    public void Motion_has_ordered_finite_samples_and_an_exact_endpoint(double from, double velocity, double target)
    {
        var motion = SettleMotion.Create(SpringPreset.Snappy, from, velocity, target);
        Assert.Equal(from, motion.At(0));
        Assert.Equal(target, motion.At(motion.Duration));
        Assert.Equal(target, motion.At(motion.Duration + 1));
        Assert.InRange(motion.Duration, 1.0 / 240, 2);
        Assert.All(motion.Samples, sample => Assert.True(double.IsFinite(sample.Value)));
        Assert.All(motion.Samples.Zip(motion.Samples.Skip(1), (a, b) => b.Time - a.Time), dt => Assert.True(dt > 0));
    }

    [Fact]
    public void Gesture_completes_after_a_ui_pause_without_replaying_lost_frames()
    {
        var harness = new GestureHarness().Move(0, -25, 0.3);
        SettleMotion? motion = null;
        harness.Engine.SettlingStarted += curve => motion = curve;
        var contacts = Enumerable.Range(0, 4).Select(i => new Contact(i, 30 + 12 * i, 15, false, true)).ToArray();
        harness.Engine.Process(new TouchFrame(harness.Time + 0.008, contacts, 110, 70));
        Assert.NotNull(motion);
        harness.Engine.Tick(harness.Engine.SettleStartedAt + motion!.Duration + 0.2);
        Assert.Equal(GesturePhase.Idle, harness.Engine.Phase);
        Assert.Equal(new[] { SwipeAction.SwipeUp }, harness.Actions);
    }

    [Theory]
    [InlineData(0, 800, 600)]
    [InlineData(90, 600, 800)]
    [InlineData(180, 800, 600)]
    [InlineData(270, 600, 800)]
    public void GPU_capture_rotation_maps_the_source_to_the_correct_monitor_extent(int rotation, int width, int height)
    {
        var offset = new PointF(-30, 45);
        var matrix = GpuCaptureLayout.Transform(new Size(800, 600), rotation, offset);
        var corners = new[] { Vector2.Zero, new Vector2(800, 0), new Vector2(0, 600), new Vector2(800, 600) }
            .Select(point => Vector2.Transform(point, matrix)).ToArray();
        Assert.Equal(offset.X, corners.Min(point => point.X));
        Assert.Equal(offset.Y, corners.Min(point => point.Y));
        Assert.Equal(offset.X + width, corners.Max(point => point.X));
        Assert.Equal(offset.Y + height, corners.Max(point => point.Y));
    }
}
