namespace TrackSwipe.Core;

public readonly record struct MotionSample(double Time, double Value);

/// <summary>A spring sampled once at release, shared by compositor playback and gesture completion.</summary>
public sealed class SettleMotion
{
    private readonly MotionSample[] _samples;
    private SettleMotion(MotionSample[] samples) => _samples = samples;
    public IReadOnlyList<MotionSample> Samples => _samples;
    public double Duration => _samples[^1].Time;
    public double Target => _samples[^1].Value;

    public static SettleMotion Create(SpringPreset preset, double position, double velocity, double target, double tolerance = 0.003, bool allowOvershoot = false)
    {
        const double step = 1.0 / 240;
        var spring = new Spring(preset) { Position = position, Velocity = velocity, Target = target };
        var outside = target != 0 && (position - target) * Math.Sign(target) > tolerance;
        var samples = new List<MotionSample> { new(0, position) };
        for (var tick = 1; tick <= 480; tick++)
        {
            spring.Step(step);
            var done = target != 0
                ? allowOvershoot ? spring.IsAtRest
                    : outside ? (spring.Position - target) * Math.Sign(target) <= tolerance
                    : (spring.Position - target) * Math.Sign(target) >= -tolerance
                : Math.Sign(spring.Position) != Math.Sign(position) || spring.IsAtRest;
            samples.Add(new MotionSample(tick * step, done || tick == 480 ? target : spring.Position));
            if (done) break;
        }
        return new SettleMotion(samples.ToArray());
    }

    /// <summary>The same motion measured from somewhere else: every value moved by <paramref name="offset"/>.</summary>
    public SettleMotion Shifted(double offset) =>
        new(_samples.Select(sample => sample with { Value = sample.Value + offset }).ToArray());

    public double At(double time)
    {
        if (time <= 0) return _samples[0].Value;
        if (time >= Duration) return Target;
        var index = Math.Min(_samples.Length - 2, (int)(time * 240));
        var from = _samples[index];
        var to = _samples[index + 1];
        return from.Value + (to.Value - from.Value) * (time - from.Time) / (to.Time - from.Time);
    }
}
