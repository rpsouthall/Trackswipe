namespace TrackSwipe.Core;

/// <summary>Spring tuning: <see cref="Response"/> is roughly the settle time in seconds.</summary>
public readonly record struct SpringPreset(double Response, double DampingRatio)
{
    public static SpringPreset Snappy { get; } = new(0.3, 0.9);
    public static SpringPreset Smooth { get; } = new(0.5, 1.0);
}

/// <summary>Damped unit-mass spring that carries release velocity into the settle animation.</summary>
public sealed class Spring
{
    private const double MaxStep = 0.001;
    private readonly double _stiffness;
    private readonly double _damping;

    public Spring(SpringPreset preset)
    {
        var angularFrequency = 2 * Math.PI / preset.Response;
        _stiffness = angularFrequency * angularFrequency;
        _damping = 2 * preset.DampingRatio * angularFrequency;
    }

    public double Position { get; set; }
    public double Velocity { get; set; }
    public double Target { get; set; }

    public bool IsAtRest => Math.Abs(Position - Target) < 1e-3 && Math.Abs(Velocity) < 1e-2;

    /// <summary>Advances by <paramref name="seconds"/> using small semi-implicit Euler steps, which stay stable at any frame rate.</summary>
    public void Step(double seconds)
    {
        while (seconds > 0)
        {
            var h = Math.Min(seconds, MaxStep);
            var acceleration = -_stiffness * (Position - Target) - _damping * Velocity;
            Velocity += acceleration * h;
            Position += Velocity * h;
            seconds -= h;
        }
    }
}
