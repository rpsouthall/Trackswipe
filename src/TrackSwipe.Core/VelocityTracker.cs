namespace TrackSwipe.Core;

/// <summary>Estimates velocity from the samples inside a short trailing window.</summary>
public sealed class VelocityTracker
{
    private readonly double _window;
    private readonly List<(double Time, double Value)> _samples = new();

    public VelocityTracker(double windowSeconds) => _window = windowSeconds;

    public void Add(double time, double value)
    {
        _samples.Add((time, value));
        Trim(time);
    }

    public void Clear() => _samples.Clear();

    /// <summary>Least-squares slope of the recent samples, in value units per second. Zero once the fingers stop.</summary>
    public double Estimate(double now)
    {
        Trim(now);
        if (_samples.Count < 2) return 0;

        double meanTime = 0, meanValue = 0;
        foreach (var (time, value) in _samples)
        {
            meanTime += time;
            meanValue += value;
        }
        meanTime /= _samples.Count;
        meanValue /= _samples.Count;

        double covariance = 0, variance = 0;
        foreach (var (time, value) in _samples)
        {
            var dt = time - meanTime;
            covariance += dt * (value - meanValue);
            variance += dt * dt;
        }
        return variance < 1e-12 ? 0 : covariance / variance;
    }

    private void Trim(double now)
    {
        var stale = 0;
        while (stale < _samples.Count && now - _samples[stale].Time > _window) stale++;
        if (stale > 0) _samples.RemoveRange(0, stale);
    }
}
