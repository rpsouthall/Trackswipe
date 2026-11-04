using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// When <see cref="NativeReclaim"/> lets a collection run: soon after the first request, never more
/// often than its minimum gap. The collection itself is not tested here — what it frees is native
/// window and handle state that only a running process has, and the live soak in
/// <c>docs/performance</c> carries that measurement.
/// </summary>
public class NativeReclaimTests
{
    [Fact]
    public void The_first_collection_waits_only_the_short_delay()
    {
        Assert.Equal(NativeReclaim.Delay, NativeReclaim.DelayUntilNext(null));
    }

    [Fact]
    public void A_collection_that_just_ran_pushes_the_next_one_out_to_the_minimum_gap()
    {
        var delay = NativeReclaim.DelayUntilNext(TimeSpan.FromSeconds(1));
        Assert.Equal(NativeReclaim.MinGap - TimeSpan.FromSeconds(1), delay);
    }

    [Theory]
    [InlineData(7)]   // the gap is up exactly as the short delay would be
    [InlineData(10)]
    [InlineData(600)]
    public void Once_the_gap_has_passed_only_the_short_delay_applies(int secondsSinceLastRun)
    {
        Assert.Equal(NativeReclaim.Delay, NativeReclaim.DelayUntilNext(TimeSpan.FromSeconds(secondsSinceLastRun)));
    }

    [Fact]
    public void The_next_collection_is_never_sooner_than_the_gap_after_the_last()
    {
        for (int s = 0; s <= 12; s++)
        {
            var since = TimeSpan.FromSeconds(s);
            Assert.True(since + NativeReclaim.DelayUntilNext(since) >= NativeReclaim.MinGap,
                $"{s} s after a run, the next would land inside the minimum gap");
        }
    }
}
