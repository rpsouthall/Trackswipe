namespace TrackSwipe.Overlay;

/// <summary>One cubic piece, v(t) = C0 + C1*t + C2*t^2 + C3*t^3, with t in seconds from <see cref="Start"/>.</summary>
internal readonly record struct CubicSegment(double Start, double C0, double C1, double C2, double C3)
{
    public double At(double t) => C0 + t * (C1 + t * (C2 + t * C3));
}

/// <summary>
/// Turns densely sampled motion (240 Hz) into few cubic pieces for the compositor. Hermite pieces about a
/// display frame long follow a spring far more closely than the samples' straight lines, at a quarter of the
/// segments. Each piece is checked against every sample it spans; one that strays (a clip meeting an edge
/// makes a corner) falls back to the original straight lines there, so the result is never less accurate.
/// </summary>
internal static class CurveFit
{
    public const double KnotSpacing = 1.0 / 60;

    public static List<CubicSegment> Segments(IReadOnlyList<double> times, IReadOnlyList<double> values)
    {
        var n = values.Count;
        var segments = new List<CubicSegment>();
        double min = values[0], max = values[0];
        for (var i = 1; i < n; i++) { min = Math.Min(min, values[i]); max = Math.Max(max, values[i]); }
        var tolerance = Math.Max((max - min) * 0.0002, 1e-6);
        if (n < 2 || max - min <= tolerance * 0.5)
        {
            segments.Add(new CubicSegment(times[0], values[0], 0, 0, 0));
            return segments;
        }

        var slopes = new double[n];
        for (var i = 0; i < n; i++)
        {
            var a = Math.Max(0, i - 1);
            var b = Math.Min(n - 1, i + 1);
            slopes[i] = (values[b] - values[a]) / (times[b] - times[a]);
        }

        var step = Math.Max(1, (int)Math.Round(KnotSpacing / ((times[^1] - times[0]) / (n - 1))));
        for (var start = 0; start < n - 1;)
        {
            // Try a display-frame-long piece; where it strays, halve it; a single interval is a straight line.
            var length = Math.Min(step, n - 1 - start);
            while (true)
            {
                var end = start + length;
                if (length == 1)
                {
                    segments.Add(new CubicSegment(times[start], values[start], (values[end] - values[start]) / (times[end] - times[start]), 0, 0));
                    break;
                }
                var piece = Hermite(times[start], values[start], slopes[start], times[end], values[end], slopes[end]);
                var fits = true;
                for (var i = start + 1; i < end && fits; i++)
                    fits = Math.Abs(piece.At(times[i] - times[start]) - values[i]) <= tolerance;
                if (fits) { segments.Add(piece); break; }
                length /= 2;
            }
            start += length;
        }
        return segments;
    }

    private static CubicSegment Hermite(double t0, double v0, double m0, double t1, double v1, double m1)
    {
        var h = t1 - t0;
        var delta = (v1 - v0) / h;
        return new CubicSegment(t0, v0, m0, (3 * delta - 2 * m0 - m1) / h, (m0 + m1 - 2 * delta) / (h * h));
    }
}
