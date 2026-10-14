namespace TrackSwipe.Core;

/// <summary>
/// Recent finger progress, for telling a flick at release. A real flick is short: the fingers travel fast for a few
/// dozen milliseconds, then slow, often pause and roll back a little as they leave the pad. Averaged over everything
/// after the fast part, that looks like a slow drag; the fastest stretch just before the lift is what shows the flick.
/// </summary>
public sealed class FlickHistory
{
    /// <summary>How far back before the lift a fast stretch still counts.</summary>
    public const double Window = 0.15;
    /// <summary>Speed is measured over at least this long, so one noisy report can't fake a flick.</summary>
    public const double Span = 0.025;

    private readonly List<(double Time, double Value)> _samples = new();

    public void Clear() => _samples.Clear();

    public void Add(double time, double value)
    {
        _samples.Add((time, value));
        var stale = 0;
        while (stale < _samples.Count && time - _samples[stale].Time > Window + Span + 0.05) stale++;
        if (stale > 0) _samples.RemoveRange(0, stale);
    }

    /// <summary>
    /// The fastest movement (progress per second, signed) over any <see cref="Span"/> ending within <see cref="Window"/>
    /// of <paramref name="now"/>, or null if there's none, or if the fingers have since come back from it by more than
    /// <paramref name="rollBack"/> (a deliberate turn, not fingers rolling off the pad).
    /// </summary>
    public double? Peak(double now, double rollBack)
    {
        double best = 0;
        var bestEnd = -1;
        for (var end = 1; end < _samples.Count; end++)
        {
            if (now - _samples[end].Time > Window) continue;
            var start = end - 1;
            while (start >= 0 && _samples[end].Time - _samples[start].Time < Span) start--;
            if (start < 0) continue;
            var velocity = (_samples[end].Value - _samples[start].Value) / (_samples[end].Time - _samples[start].Time);
            if (Math.Abs(velocity) > Math.Abs(best)) { best = velocity; bestEnd = end; }
        }
        if (bestEnd < 0 || best == 0) return null;

        var direction = Math.Sign(best);
        var furthest = double.NegativeInfinity;
        for (var i = bestEnd; i < _samples.Count; i++) furthest = Math.Max(furthest, _samples[i].Value * direction);
        return furthest - _samples[^1].Value * direction > rollBack ? null : best;
    }
}
