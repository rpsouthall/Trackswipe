using TrackSwipe.Core;

namespace TrackSwipe.Overlay;

/// <summary>Retargetable hover expansion; animation samples are submitted once to the compositor.</summary>
internal sealed class DesktopBarReveal
{
    private SettleMotion? _motion;
    private double _started, _value;
    public bool Expanded { get; private set; }
    public bool Animating(double now) => _motion is { } motion && now - _started < motion.Duration;
    public double Value(double now) => Math.Clamp(_motion?.At(now - _started) ?? _value, 0, 1);
    public void Reset(double value = 0) { _motion = null; _value = value; Expanded = value >= 0.5; }

    public SettleMotion? Set(bool expanded, double now)
    {
        if (expanded == Expanded) return null;
        var value = Value(now);
        var velocity = _motion is null ? 0 : (Value(now) - Value(now - 0.001)) / 0.001;
        Expanded = expanded;
        _started = now;
        return _motion = SettleMotion.Create(new SpringPreset(0.22, 1), value, velocity, expanded ? 1 : 0, 0.002);
    }

    public static bool Hovering(int y, int compactBottom, int expandedBottom, bool expanded) =>
        y >= 0 && y <= (expanded ? expandedBottom : compactBottom);
}
