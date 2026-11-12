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
        // A frame is drawn for a moment a frame or two ahead, from a report that is already a few milliseconds old, so
        // the newest report is routinely 15 to 25 ms behind the time asked for, and how far varies from frame to
        // frame. The forecast must not depend on which side of some line that falls: dropped whenever the report
        // looked "stale", it came and went between frames, and a steady swipe was drawn stepping back and forth by
        // the whole forecast. It is kept in full while reports are arriving at all, and eased away (never dropped)
        // once they have really stopped.
        var age = time - _time;
        var arriving = Math.Max(0.035, _interval * 3.5);
        var fade = age <= arriving ? 1 : Math.Clamp(1 - (age - arriving) / 0.050, 0, 1);
        var horizon = Math.Clamp(age + CorrectionTime, 0, Math.Min(0.020, _interval * 2));
        var target = _value + Math.Clamp(_velocity * horizon * _predictionWeight, -0.050, 0.050) * fade;
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
