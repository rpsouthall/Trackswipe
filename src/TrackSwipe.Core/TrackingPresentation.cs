namespace TrackSwipe.Core;

/// <summary>
/// Resamples touch movement at display times. A short, bounded forecast fills the gaps between
/// reports; a four-millisecond correction removes arrival jitter without changing gesture decisions.
/// </summary>
internal sealed class TrackingPresentation
{
    private const double CorrectionTime = 0.004;
    private double _time = double.NaN;
    private double _value;
    private double _velocity;
    private double _interval = 1.0 / 125;
    private double _displayTime;

    public double Value { get; private set; }
    public bool HasPresented { get; private set; }

    public void Reset()
    {
        _time = double.NaN;
        _velocity = 0;
        _interval = 1.0 / 125;
        _displayTime = 0;
        HasPresented = false;
    }

    public void Add(double time, double value)
    {
        if (!double.IsNaN(_time) && time < _time) return;
        var dt = time - _time;
        if (dt > 0 && dt <= 0.05)
        {
            _interval = dt;
            var velocity = (value - _value) / dt;
            // Stops and reversals must take effect with the newest report, not a trailing average.
            _velocity = velocity == 0 || Math.Sign(velocity) != Math.Sign(_velocity)
                ? velocity
                : _velocity + (velocity - _velocity) * (1 - Math.Exp(-dt / 0.008));
        }
        else if (double.IsNaN(_time) || dt > 0.05)
        {
            _velocity = 0;
        }
        _time = time;
        _value = value;
    }

    public double Sample(double time)
    {
        if (double.IsNaN(_time)) return Value;
        var horizon = Math.Clamp(time - _time + CorrectionTime, 0, Math.Min(0.020, _interval * 1.5));
        var target = _value + Math.Clamp(_velocity * horizon, -0.100, 0.100);
        if (!HasPresented)
        {
            Value = target;
            HasPresented = true;
        }
        else
        {
            var dt = Math.Clamp(time - _displayTime, 0, 0.05);
            Value += (target - Value) * (1 - Math.Exp(-dt / CorrectionTime));
        }
        _displayTime = Math.Max(_displayTime, time);
        return Value;
    }
}
