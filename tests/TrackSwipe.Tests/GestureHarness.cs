using TrackSwipe.Core;

namespace TrackSwipe.Tests;

/// <summary>Drives a <see cref="GestureEngine"/> with synthetic 125 Hz touchpad frames and 120 Hz display ticks.</summary>
internal sealed class GestureHarness
{
    private const double FrameInterval = 1.0 / 125;
    private const double TickInterval = 1.0 / 120;
    private static readonly double[] FingerX = { 30, 42, 54, 66, 78 };
    private double _x, _y;

    public GestureHarness(GestureOptions? options = null)
    {
        Engine = new GestureEngine(options ?? new GestureOptions());
        Engine.Committed += action => Actions.Add(action);
        Engine.TrackingStarted += _ => TrackingStarts++;
    }

    public GestureEngine Engine { get; }

    public List<SwipeAction> Actions { get; } = new();

    public List<double> ProgressHistory { get; } = new();

    public int TrackingStarts { get; private set; }

    public double MaxDisplay { get; private set; }

    public double Time { get; private set; } = 1000;

    /// <summary>Contacts currently on the pad. Lowering it lifts the highest-numbered fingers.</summary>
    public int Fingers { get; set; } = 4;

    public GestureHarness Move(double dx, double dy, double seconds)
    {
        var steps = Math.Max(1, (int)Math.Round(seconds / FrameInterval));
        double startX = _x, startY = _y;
        for (var i = 1; i <= steps; i++)
        {
            _x = startX + dx * i / steps;
            _y = startY + dy * i / steps;
            Send(tip: true);
        }
        return this;
    }

    public GestureHarness Hold(double seconds) => Move(0, 0, seconds);

    public GestureHarness Lift()
    {
        Send(tip: false);
        for (var i = 0; i < 240 && Engine.Phase == GesturePhase.Settling; i++)
        {
            Time += TickInterval;
            Engine.Tick(Time);
            Observe();
        }
        return this;
    }

    private void Send(bool tip)
    {
        Time += FrameInterval;
        var contacts = Enumerable.Range(0, Fingers)
            .Select(i => new Contact(i, FingerX[i] + _x, 40 + _y, tip, Confidence: true))
            .ToArray();
        Engine.Process(new TouchFrame(Time, contacts, 110, 70));
        Engine.Tick(Time);
        Observe();
    }

    private void Observe()
    {
        if (Engine.Phase == GesturePhase.Tracking) ProgressHistory.Add(Engine.Progress);
        MaxDisplay = Math.Max(MaxDisplay, Math.Abs(Engine.DisplayProgress));
    }
}
