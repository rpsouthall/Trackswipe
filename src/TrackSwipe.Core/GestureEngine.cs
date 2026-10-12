namespace TrackSwipe.Core;

public enum GesturePhase { Idle, Candidate, Tracking, Rejected, Settling }

public enum GestureAxis { None, Horizontal, Vertical }

public enum SwipeAction { NextDesktop, PreviousDesktop, SwipeUp, SwipeDown }

/// <summary>
/// Turns touchpad frames into a swipe. Both axes expose a signed progress (+1 = next desktop,
/// or up) that follows the fingers and settles on a spring after release, so reversing
/// mid-gesture just works. Not thread-safe: call <see cref="Process"/> and <see cref="Tick"/> from one thread.
/// </summary>
public sealed class GestureEngine
{
    private const double VelocityWindow = 0.1;

    private readonly Dictionary<int, (double X, double Y)> _previous = new();
    private readonly VelocityTracker _velocity = new(VelocityWindow);
    private readonly TrackingPresentation _presentation = new();
    private SettleMotion? _settleMotion;
    private double _travelX, _travelY;
    private double _lastFrameTime = double.NegativeInfinity;
    private double _lastTickTime;
    private double _target;

    public GestureEngine(GestureOptions? options = null) => Options = options ?? new GestureOptions();

    public GestureOptions Options { get; }

    public GesturePhase Phase { get; private set; }

    public GestureAxis Axis { get; private set; }

    /// <summary>Raw finger progress. +1 is one full swipe towards the next desktop (fingers moving left), or upwards for vertical swipes.</summary>
    public double Progress { get; private set; }

    /// <summary>Progress to draw: follows the fingers, rubber-bands where a direction is blocked, then animates while settling.</summary>
    public double DisplayProgress { get; private set; }

    /// <summary>Progress per second, measured over the last few frames.</summary>
    public double Velocity { get; private set; }
    public double SettleStartedAt => _lastTickTime;

    public int ActiveContacts { get; private set; }

    /// <summary>
    /// Set from <see cref="TrackingStarted"/>: whether the swipe can commit in the positive direction
    /// (next desktop, or up) or only rubber-bands.
    /// </summary>
    public bool CanGoPositive { get; set; } = true;

    /// <summary>As <see cref="CanGoPositive"/>, for the previous desktop, or down.</summary>
    public bool CanGoNegative { get; set; } = true;

    /// <summary>True while <see cref="Tick"/> needs calling every display frame.</summary>
    public bool NeedsFrames => Phase is GesturePhase.Tracking or GesturePhase.Settling;

    /// <summary>
    /// The right number of fingers just landed; a swipe may follow once they move past the dead zone.
    /// A chance to prepare ahead of <see cref="TrackingStarted"/>, while the fingers cover that distance.
    /// </summary>
    public event Action? CandidateStarted;

    public event Action<GestureAxis>? TrackingStarted;

    public event Action<SwipeAction>? Committed;

    /// <summary>Raised when a tracked gesture is fully over, after <see cref="Committed"/> if it committed.</summary>
    public event Action? Ended;
    public event Action<SettleMotion>? SettlingStarted;

    public void Process(TouchFrame frame)
    {
        // A long silence means we missed the lift; start fresh rather than stay stuck.
        if (frame.Time - _lastFrameTime > Options.ReleaseTimeout)
        {
            _previous.Clear();
            if (Phase is GesturePhase.Candidate or GesturePhase.Rejected) Phase = GesturePhase.Idle;
        }

        var (count, dx, dy) = MeasureMovement(frame);
        ActiveContacts = count;
        _lastFrameTime = frame.Time;
        var fingers = Options.Fingers;

        switch (Phase)
        {
            case GesturePhase.Idle:
                if (count == fingers)
                {
                    _travelX = _travelY = 0;
                    Phase = GesturePhase.Candidate;
                    CandidateStarted?.Invoke();
                }
                break;

            case GesturePhase.Candidate:
                if (count != fingers)
                {
                    Phase = count > fingers ? GesturePhase.Rejected : GesturePhase.Idle;
                    break;
                }
                _travelX += dx;
                _travelY += dy;
                TryLockAxis(frame.Time);
                break;

            case GesturePhase.Tracking:
                _travelX += dx;
                _travelY += dy;
                UpdateProgress(frame.Time);
                // Tolerate one finger lifting early; release once the rest follow.
                if (count <= Math.Max(0, fingers - 2)) Release(frame.Time);
                break;

            case GesturePhase.Rejected:
                if (count == 0) Phase = GesturePhase.Idle;
                break;
        }
    }

    /// <summary>Advances the settle animation and release timeout. Call once per display frame while <see cref="NeedsFrames"/>.</summary>
    public void Tick(double time)
    {
        if (Phase == GesturePhase.Tracking && time - _lastFrameTime > Options.ReleaseTimeout) Release(time);
        if (Phase == GesturePhase.Tracking)
        {
            DisplayProgress = Shape(_presentation.Sample(time));
            return;
        }
        if (Phase != GesturePhase.Settling || _settleMotion is null) return;
        var elapsed = Math.Max(0, time - _lastTickTime);
        var position = _settleMotion.At(elapsed);

        if (_target != 0)
        {
            // Fire as soon as the panel arrives (or overshoots); the OS takes over from there.
            if (elapsed >= _settleMotion.Duration)
            {
                Finish((Axis, _target > 0) switch
                {
                    (GestureAxis.Horizontal, true) => SwipeAction.NextDesktop,
                    (GestureAxis.Horizontal, false) => SwipeAction.PreviousDesktop,
                    (_, true) => SwipeAction.SwipeUp,
                    _ => SwipeAction.SwipeDown,
                });
                return;
            }
        }
        else if (elapsed >= _settleMotion.Duration)
        {
            // Cancelling: stop at zero instead of overshooting into the opposite panel.
            Finish(null);
            return;
        }

        DisplayProgress = position;
    }

    public void Reset()
    {
        Phase = GesturePhase.Idle;
        Axis = GestureAxis.None;
        _settleMotion = null;
        Progress = DisplayProgress = Velocity = 0;
        _presentation.Reset();
        _previous.Clear();
    }

    /// <summary>
    /// Average movement of the contacts present in both this frame and the last, so fingers
    /// landing or lifting at different times don't make the centroid jump.
    /// </summary>
    private (int Count, double Dx, double Dy) MeasureMovement(TouchFrame frame)
    {
        int count = 0, shared = 0;
        double dx = 0, dy = 0;
        foreach (var contact in frame.Contacts)
        {
            if (!contact.IsActive) continue;
            count++;
            if (_previous.TryGetValue(contact.Id, out var previous))
            {
                dx += contact.X - previous.X;
                dy += contact.Y - previous.Y;
                shared++;
            }
        }

        _previous.Clear();
        foreach (var contact in frame.Contacts)
        {
            if (contact.IsActive) _previous[contact.Id] = (contact.X, contact.Y);
        }

        return shared == 0 ? (count, 0, 0) : (count, dx / shared, dy / shared);
    }

    private void TryLockAxis(double time)
    {
        var ax = Math.Abs(_travelX);
        var ay = Math.Abs(_travelY);
        var distance = Math.Sqrt(ax * ax + ay * ay);
        if (distance < Options.DeadZoneMm) return;

        if (ax >= ay * Options.AxisLockRatio) Begin(GestureAxis.Horizontal, time);
        else if (ay >= ax * Options.AxisLockRatio) Begin(GestureAxis.Vertical, time);
        else if (distance >= 2 * Options.DeadZoneMm) Phase = GesturePhase.Rejected; // still diagonal: not ours
    }

    private void Begin(GestureAxis axis, double time)
    {
        Axis = axis;
        Phase = GesturePhase.Tracking;
        _velocity.Clear();
        _presentation.Reset();
        TrackingStarted?.Invoke(axis);
        UpdateProgress(time);
    }

    private void UpdateProgress(double time)
    {
        Progress = Axis == GestureAxis.Horizontal ? -_travelX / Options.FullSwipeMm : -_travelY / Options.FullSwipeVerticalMm;
        _velocity.Add(time, Progress);
        _presentation.Add(time, Progress);
        Velocity = _velocity.Estimate(time);
        DisplayProgress = Shape(Progress);
    }

    private void Release(double time)
    {
        var p = Progress;
        var v = _velocity.Estimate(time);
        var flick = Options.FlickVelocity;
        var threshold = Axis == GestureAxis.Horizontal ? Options.CommitProgress : Options.VerticalCommitProgress;

        var positive = CanGoPositive && ((p >= threshold && v > -flick) || (v >= flick && p >= Options.MinFlickProgress));
        var negative = CanGoNegative && ((p <= -threshold && v < flick) || (v <= -flick && p <= -Options.MinFlickProgress));
        _target = positive ? 1 : negative ? -1 : 0;

        // Hand the release velocity to the spring, as seen through the rubber band.
        const double h = 1e-3;
        // Start from the last displayed position, even if lift reports arrived between display frames.
        var displayed = _presentation.HasPresented ? Shape(_presentation.Value) : DisplayProgress;
        _settleMotion = SettleMotion.Create(Options.Spring, displayed, (Shape(p + v * h) - Shape(p)) / h, _target, Options.CommitTolerance);
        DisplayProgress = displayed;
        _lastTickTime = time;
        Phase = GesturePhase.Settling;
        SettlingStarted?.Invoke(_settleMotion);
    }

    private void Finish(SwipeAction? action)
    {
        Phase = GesturePhase.Idle;
        Axis = GestureAxis.None;
        _settleMotion = null;
        Progress = DisplayProgress = Velocity = 0;
        if (action is { } committed) Committed?.Invoke(committed);
        Ended?.Invoke();
    }

    /// <summary>Maps finger progress to panel position: 1:1 where there is a desktop to go to, rubber-banded where there isn't.</summary>
    private double Shape(double progress)
    {
        var magnitude = Math.Abs(progress);
        var canGo = progress >= 0 ? CanGoPositive : CanGoNegative;
        var shaped = !canGo ? RubberBand(magnitude)
            : magnitude <= 1 ? magnitude
            : 1 + RubberBand(magnitude - 1);
        return Math.CopySign(shaped, progress);
    }

    private double RubberBand(double distance)
    {
        var limit = Options.RubberBandLimit;
        return limit * (1 - 1 / (distance * 0.55 / limit + 1));
    }
}
