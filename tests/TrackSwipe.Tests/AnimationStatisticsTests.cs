namespace TrackSwipe.Tests;

public class AnimationStatisticsTests
{
    [Fact]
    public void SummaryPreservesTotalsAndWorstFramesAndResets()
    {
        var stats = new AnimationStatistics();
        stats.Add(0, 0.001);
        stats.Add(0.0083, 0.0103);
        stats.Add(0.0166, 0.0176);
        stats.Add(0.0666, 0.0706);
        Assert.Equal(4, stats.Frames);
        Assert.Equal(0.0666, stats.Span, 6);
        Assert.Equal(0.0083, stats.MedianGap, 6);
        Assert.Equal(0.0500, stats.WorstGap, 6);
        Assert.Equal(0.004, stats.WorstWork, 6);
        Assert.Equal(0.002, stats.AverageWork, 6);
        Assert.Equal(1, stats.SlowGaps);
        Assert.Equal(3, stats.SlowSamples[0].Frame);
        stats.Reset();
        Assert.Equal(0, stats.Frames);
        Assert.Equal(0, stats.Span);
        Assert.Equal(0, stats.MedianGap);
        Assert.True(stats.SlowSamples.IsEmpty);
        stats.Add(100, 100.001);
        Assert.Equal(0, stats.Span);
    }

    [Fact]
    public void LongAnimationUsesFixedStorageAndDoesNotAllocate()
    {
        var stats = new AnimationStatistics();
        for (var i = 0; i < 10000; i++) stats.Add(i * 0.02, i * 0.02 + 0.001);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 10000; i < 200000; i++) stats.Add(i * 0.02, i * 0.02 + 0.001);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(200000, stats.Frames);
        Assert.Equal(199999, stats.SlowGaps);
        Assert.Equal(32, stats.SlowSamples.Length);
        Assert.Equal(0.02, stats.MedianGap, 6);
    }
}
