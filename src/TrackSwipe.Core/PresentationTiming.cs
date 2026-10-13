namespace TrackSwipe.Core;

/// <summary>Chooses a future compositor sampling time after accounting for the batch's build margin.</summary>
public static class PresentationTiming
{
    public static double Choose(double now, double estimated, double period, double margin)
    {
        if (!double.IsFinite(estimated) || !double.IsFinite(period) || period <= 0) return now;
        var earliest = now + Math.Clamp(margin, 0, period * 0.5);
        if (estimated < earliest) estimated += Math.Ceiling((earliest - estimated) / period) * period;
        return estimated;
    }
}
