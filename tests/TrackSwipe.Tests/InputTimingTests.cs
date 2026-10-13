using TrackSwipe.Core;

namespace TrackSwipe.Tests;

public class InputTimingTests
{
    [Fact]
    public void Scan_clock_preserves_hardware_intervals_despite_delivery_jitter()
    {
        var clock = new TouchScanClock();
        Assert.Equal(10, clock.Map(100, 10), 8);
        Assert.Equal(10.008, clock.Map(180, 10.012), 8);
        Assert.Equal(10.016, clock.Map(260, 10.017), 8);
        Assert.Equal(10.024, clock.Map(340, 10.030), 8);
    }

    [Fact]
    public void Scan_clock_handles_wrap_and_reanchors_after_inactivity_or_device_reset()
    {
        var clock = new TouchScanClock();
        clock.Map(65500, 10);
        Assert.Equal(10.008, clock.Map(44, 10.009), 8);
        Assert.Equal(12, clock.Map(0, 12), 8);
        Assert.Equal(12.008, clock.Map(80, 12.010), 8);
        Assert.Equal(12.020, clock.Map(0, 12.020), 8);
    }

    [Fact]
    public void Continuation_reports_keep_the_same_scan_time_without_advancing_clock()
    {
        var clock = new TouchScanClock();
        clock.Map(80, 10);
        Assert.Equal(10, clock.Map(80, 10.004), 8);
        Assert.Equal(10.008, clock.Map(160, 10.011), 8);
    }

    [Theory]
    [InlineData(10.001, 10.008333333, 0.001, 10.008333333)]
    [InlineData(10.008, 10.008333333, 0.001, 10.016666666)]
    [InlineData(10.020, 10.008333333, 0.001, 10.025)]
    public void Late_callbacks_sample_a_future_refresh_instead_of_replaying_an_old_target(double now, double next, double margin, double expected) =>
        Assert.Equal(expected, PresentationTiming.Choose(now, next, 1.0 / 120, margin), 6);
}
