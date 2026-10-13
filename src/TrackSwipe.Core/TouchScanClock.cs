namespace TrackSwipe.Core;

/// <summary>Maps the touchpad's rolling 100 µs scan counter to the application's monotonic clock.</summary>
public sealed class TouchScanClock
{
    public const double TickSeconds = 0.0001;
    private ushort _scan;
    private double _time = double.NaN, _arrival;

    public double Map(ushort scan, double arrival)
    {
        var elapsed = unchecked((ushort)(scan - _scan)) * TickSeconds;
        if (double.IsNaN(_time) || arrival - _arrival > 0.25 || elapsed > 0.25)
            _time = arrival; // inactivity, device reset or a discontinuity: establish a new clock anchor
        else
            _time = Math.Max(_time, Math.Min(arrival, _time + elapsed));
        _scan = scan;
        _arrival = arrival;
        return _time;
    }
}
