namespace TrackSwipe.Core;

/// <summary>
/// Resamples touch movement at display times. A short, bounded forecast fills the gaps between
/// reports; a two-millisecond correction removes arrival jitter without changing gesture decisions.
/// </summary>
internal sealed class TrackingPresentation
{
    private const double CorrectionTime = 0.002;
    private double _time = double.NaN;
    private double _value;
    private double _velocity;
    private double _interval = 1.0 / 125;
    private double _displayTime;
    private double _motionTime, _motionValue;
    private double _predictionWeight = 1;

    public double Value { get; private set; }
    public bool HasPresented { get; private set; }
    public double PresentedAt => _displayTime;
    public double MotionVelocity(double time) => time - _motionTime <= 0.025 ? _velocity : 0;

    public void Reset()
    {
        _time = double.NaN;
        _velocity = 0;
        _interval = 1.0 / 125;
        _displayTime = 0;
        _motionTime = double.NaN;
        _predictionWeight = 1;
        HasPresented = false;
    }

    public void Add(double time, double value)
    {
        if (!double.IsNaN(_time) && time < _time) return;
        var dt = time - _time;
        if (dt > 0 && dt <= 0.05)
        {
            _interval += (dt - _interval) * 0.2;
            if (value != _value)
            {
                var velocity = (value - _motionValue) / Math.Max(dt, time - _motionTime);
                var reversing = _velocity != 0 && velocity != 0 && Math.Sign(velocity) != Math.Sign(_velocity);
                // A reversal takes effect immediately. A repeated quantized coordinate
                // needn't mean the fingers stopped: estimate across that repeated sample.
                _velocity = Math.Sign(velocity) != Math.Sign(_velocity) ? velocity
                    : _velocity + (velocity - _velocity) * (1 - Math.Exp(-dt / 0.006));
                _predictionWeight = reversing ? 0 : Math.Min(1, _predictionWeight + dt / 0.016);
                _motionTime = time;
                _motionValue = value;
            }
            else if (time - _motionTime >= Math.Clamp(_interval * 1.5, 0.012, 0.016)) _velocity = 0;
        }
        else if (double.IsNaN(_time) || dt > 0.05)
        {
            _velocity = 0;
            _motionTime = time;
            _motionValue = value;
        }
        _time = time;
        _value = value;
    }

    public double Sample(double time)
    {
        if (double.IsNaN(_time)) return Value;
        var fresh = time - _time <= Math.Min(0.025, _interval * 2.5);
        var horizon = fresh ? Math.Clamp(time - _time + CorrectionTime, 0, Math.Min(0.020, _interval * 2)) : 0;
        var target = _value + Math.Clamp(_velocity * horizon * _predictionWeight, -0.050, 0.050);
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
