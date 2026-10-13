using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class CurveFitTests
{
    private static (double[] Times, double[] Values) Spring(double from, double to, double scale = 1000)
    {
        var motion = SettleMotion.Create(SpringPreset.Snappy, from, 0, to);
        return (motion.Samples.Select(s => s.Time).ToArray(), motion.Samples.Select(s => s.Value * scale).ToArray());
    }

    private static double Evaluate(List<CubicSegment> segments, double time)
    {
        var segment = segments.Last(s => s.Start <= time + 1e-12);
        return segment.At(time - segment.Start);
    }

    private static double SampledLine(double[] times, double[] values, double time)
    {
        var i = Math.Clamp(Array.FindLastIndex(times, t => t <= time), 0, times.Length - 2);
        return values[i] + (values[i + 1] - values[i]) * (time - times[i]) / (times[i + 1] - times[i]);
    }

    [Fact]
    public void A_spring_needs_about_a_quarter_of_the_segments_and_stays_within_a_fraction_of_a_pixel()
    {
        var (times, values) = Spring(0, 1);
        var segments = CurveFit.Segments(times, values);
        Assert.True(segments.Count * 3.5 <= times.Length - 1, $"{segments.Count} segments for {times.Length} samples");
        foreach (var (time, value) in times.Zip(values))
            Assert.True(Math.Abs(Evaluate(segments, time) - value) <= 0.2 + 1e-9);

        // Between samples, compare both representations with the spring stepped ten times finer: the cubic
        // pieces must be at least as faithful as the straight lines they replace (the final snap excluded).
        var spring = new Spring(SpringPreset.Snappy) { Position = 0, Velocity = 0, Target = 1 };
        double worstCubic = 0, worstLine = 0;
        for (var tick = 1; tick * (1.0 / 2400) < times[^2]; tick++)
        {
            spring.Step(1.0 / 2400);
            var t = tick / 2400.0;
            worstCubic = Math.Max(worstCubic, Math.Abs(Evaluate(segments, t) - spring.Position * 1000));
            worstLine = Math.Max(worstLine, Math.Abs(SampledLine(times, values, t) - spring.Position * 1000));
        }
        Assert.True(worstCubic <= worstLine + 0.05, $"cubic {worstCubic:0.000} px vs lines {worstLine:0.000} px");
    }

    [Fact]
    public void A_corner_falls_back_to_the_samples_exactly()
    {
        var (times, values) = Spring(0, 1);
        var clipped = values.Select(v => Math.Min(v, 500)).ToArray(); // a clip meeting an edge
        var segments = CurveFit.Segments(times, clipped);
        foreach (var (time, value) in times.Zip(clipped))
            Assert.True(Math.Abs(Evaluate(segments, time) - value) <= 0.2 + 1e-9);
    }

    [Fact]
    public void Constant_values_are_one_segment()
    {
        var times = Enumerable.Range(0, 100).Select(i => i / 240.0).ToArray();
        var segments = CurveFit.Segments(times, times.Select(_ => 42.0).ToArray());
        Assert.Single(segments);
        Assert.Equal(42, segments[0].At(0.3));
    }

    [Fact]
    public void Two_frames_make_a_straight_line()
    {
        var segments = CurveFit.Segments(new[] { 0.0, 0.1 }, new[] { 1.0, 0.0 });
        Assert.Single(segments);
        Assert.Equal(0.5, segments[0].At(0.05), 6);
    }

    [Fact]
    public void Each_piece_ends_exactly_where_the_next_begins()
    {
        var (times, values) = Spring(1, 0, 2500);
        var segments = CurveFit.Segments(times, values);
        for (var i = 0; i + 1 < segments.Count; i++)
        {
            var end = segments[i].At(segments[i + 1].Start - segments[i].Start);
            Assert.Equal(segments[i + 1].C0, end, 6);
        }
    }
}
