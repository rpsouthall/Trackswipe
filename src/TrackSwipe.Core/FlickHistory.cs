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
    /// Longest the fingers may come back before lifting and still count as rolling off the pad rather than turning.
    /// </summary>
    public const double RollOffTime = 0.05;

    /// <summary>
    /// The fastest movement (progress per second, signed) over any <see cref="Span"/> ending within <see cref="Window"/>
    /// of <paramref name="now"/>, or null if there's none, or if the fingers have since turned back from it: come back by
    /// more than <paramref name="rollBack"/> for longer than <see cref="RollOffTime"/>. Fingertips leaving the pad after a
    /// quick flick typically snap back 2 to 4 mm in a few milliseconds; a deliberate turn takes longer. (A snap back that
    /// undoes nearly all of a flick still doesn't switch: the engine wants some net travel for a flick too.)
    /// </summary>
    public double? Peak(double now, double rollBack)
    {
        // The fastest stretch each way. The one that counts is in the direction the fingers went overall: a quick snap
        // back as they leave the pad can be faster than the flick itself, but it's the flick that was meant.
        double forward = 0, backward = 0;
        int forwardEnd = -1, backwardEnd = -1;
        for (var end = 1; end < _samples.Count; end++)
        {
            if (now - _samples[end].Time > Window) continue;
            var start = end - 1;
            while (start >= 0 && _samples[end].Time - _samples[start].Time < Span) start--;
            if (start < 0) continue;
            var velocity = (_samples[end].Value - _samples[start].Value) / (_samples[end].Time - _samples[start].Time);
            if (velocity > forward) { forward = velocity; forwardEnd = end; }
            if (velocity < backward) { backward = velocity; backwardEnd = end; }
        }
        if (forwardEnd < 0 && backwardEnd < 0) return null;
        var net = _samples.Count > 0 ? _samples[^1].Value - _samples[0].Value : 0;
        var useForward = backwardEnd < 0 || (forwardEnd >= 0 && (net > 0 || (net == 0 && forward >= -backward)));
        var best = useForward ? forward : backward;
        var bestEnd = useForward ? forwardEnd : backwardEnd;
        if (best == 0) return null;

        var direction = Math.Sign(best);
        var furthest = double.NegativeInfinity;
        for (var i = bestEnd; i < _samples.Count; i++) furthest = Math.Max(furthest, _samples[i].Value * direction);
        var back = furthest - _samples[^1].Value * direction;
        if (back <= rollBack) return best;
        // When the fingers started coming back: the last moment they were still (within the allowance) at their furthest.
        var turnedAt = _samples[bestEnd].Time;
        for (var i = bestEnd; i < _samples.Count; i++)
            if (_samples[i].Value * direction >= furthest - rollBack) turnedAt = _samples[i].Time;
        return now - turnedAt > RollOffTime ? null : best;
    }
}
