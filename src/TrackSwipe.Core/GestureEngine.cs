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
    /// <summary>How far (mm) fingers may come back after a flick's fast part and still count as rolling off the pad.</summary>
    private const double FlickRollBackMm = 1.5;
    /// <summary>
    /// How long after the first finger lifts the decision still uses the moment it lifted. Movement in between is the
    /// fingers leaving the pad (fingertips roll back a few millimetres as they go), not the gesture; a lift that takes
    /// longer is fingers carrying on with fewer, and counts as before.
    /// </summary>
    private const double LiftWindow = 0.15;

    private readonly Dictionary<int, (double X, double Y)> _previous = new();
    private readonly VelocityTracker _velocity = new(VelocityWindow);
    private readonly FlickHistory _flick = new();
    private readonly TrackingPresentation _presentation = new();
    private SettleMotion? _settleMotion;
    private SettleMotion? _interruptedMotion;
    private double _interruptedStarted, _interruptedPosition;
    /// <summary>When a caught swipe was moved on to its target desktop: how far the gesture's origin moved (±1).</summary>
    private double _interruptedShift;
    private double _travelX, _travelY;
    private double _lastFrameTime = double.NegativeInfinity;
    private double _lastTickTime;
    private double _target;
    /// <summary>When the first finger of a tracked gesture lifted (NaN while all are down), and the progress then.</summary>
    private double _liftAt = double.NaN, _liftProgress;
    /// <summary>
    /// When this touch caught a swipe on its way: whether it moved that swipe on to its desktop (+1 or -1, else 0), the
    /// progress it caught it at (which this touch's travel is measured from; 0 for a fresh touch), and where that swipe
    /// was going.
    /// </summary>
    private double _caughtShift, _caughtFrom, _caughtTarget;
    /// <summary>
    /// Furthest this touch has travelled each way (progress, from <see cref="_caughtFrom"/>), and the last time it was
    /// still within <see cref="TurnAllowanceMm"/> of each: how a swipe that was caught (turned back) is told apart.
    /// </summary>
    private double _furthestAhead, _furthestBehind, _nearAheadAt, _nearBehindAt;
    /// <summary>How far fingers may come back from their furthest and still be heading on (a sideways swipe).</summary>
    private const double TurnAllowanceMm = 3;

    public GestureEngine(GestureOptions? options = null) => Options = options ?? new GestureOptions();

    public GestureOptions Options { get; }

    public GesturePhase Phase { get; private set; }

    public GestureAxis Axis { get; private set; }

    /// <summary>Raw finger progress. +1 is one full swipe towards the next desktop (fingers moving left), or upwards for vertical swipes.</summary>
    public double Progress { get; private set; }

    /// <summary>Progress to draw: follows the fingers, rubber-bands where a direction is blocked, then animates while settling.</summary>
    public double DisplayProgress { get; private set; }

    /// <summary>
    /// Which way the fingers were heading when tracking started (+1 next desktop or up, -1 previous desktop or
    /// down), so a gesture can choose what to show before its first frame.
    /// </summary>
    public int TrackingDirection { get; private set; }

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
    public event Action? SettlingInterrupted;

    /// <summary>
    /// Fingers caught a sideways swipe on its way to the neighbouring desktop (+1 or -1): may that switch be completed
    /// now, so the new touch carries on from there? Given the direction and the position to show afterwards,
    /// measured from the new desktop. True if it switched; the gesture then continues from the new desktop.
    /// </summary>
    public Func<int, double, bool>? AdvanceRequested { get; set; }

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
                if (count < fingers && double.IsNaN(_liftAt)) { _liftAt = frame.Time; _liftProgress = Progress; }
                else if (count >= fingers) _liftAt = double.NaN;
                _travelX += dx;
                _travelY += dy;
                UpdateProgress(frame.Time);
                // Tolerate one finger lifting early; release once the rest follow.
                if (count <= Math.Max(0, fingers - 2)) Release(frame.Time);
                break;

            case GesturePhase.Rejected:
                if (count == 0) Phase = GesturePhase.Idle;
                break;
            case GesturePhase.Settling when count == fingers && _settleMotion is { } motion:
                // A fresh touch takes ownership of the animation at its current position.
                _interruptedMotion = motion;
                _interruptedStarted = _lastTickTime;
                _interruptedPosition = motion.At(Math.Max(0, frame.Time - _lastTickTime));
                _settleMotion = null;
                Phase = GesturePhase.Tracking;
                // A swipe caught on its way to the next desktop belongs to that desktop now: otherwise this touch
                // could only reach the desktop the first swipe was heading for, and would meet its rubber band there.
                _interruptedShift = 0;
                if (Axis == GestureAxis.Horizontal && _target != 0 &&
                    AdvanceRequested?.Invoke(Math.Sign(_target), _interruptedPosition - _target) == true)
                    _interruptedShift = _target;
                DisplayProgress = _interruptedPosition - _interruptedShift;
                Progress = Unshape(DisplayProgress);
                _caughtShift = _interruptedShift;
                _caughtFrom = Progress;
                _caughtTarget = _target - _interruptedShift;
                ResetTravel(frame.Time);
                _liftAt = double.NaN;
                SetTravel(Progress);
                _velocity.Clear();
                _flick.Clear();
                _presentation.Reset();
                _presentation.Add(frame.Time, Progress);
                SettlingInterrupted?.Invoke();
                break;
        }
    }

    /// <summary>Advances the settle animation and release timeout. Call once per display frame while <see cref="NeedsFrames"/>.</summary>
    public void Tick(double time)
    {
        if (Phase == GesturePhase.Tracking && time - _lastFrameTime > Options.ReleaseTimeout) Release(time);
        if (Phase == GesturePhase.Tracking)
        {
            if (_interruptedMotion is { } interrupted)
            {
                // Match the compositor's position at the first new display time, not an
                // older input-arrival time. Any new finger travel is preserved as an offset.
                Progress += Unshape(interrupted.At(Math.Max(0, time - _interruptedStarted)) - _interruptedShift) - Unshape(_interruptedPosition - _interruptedShift);
                SetTravel(Progress);
                _presentation.Reset();
                _presentation.Add(_lastFrameTime, Progress);
                _velocity.Clear();
                _velocity.Add(_lastFrameTime, Progress);
                _flick.Clear();
                _flick.Add(_lastFrameTime, Progress);
                _interruptedMotion = null;
            }
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
        _interruptedMotion = null;
        _liftAt = double.NaN;
        _caughtShift = _caughtFrom = _caughtTarget = 0;
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
        for (var i = 0; i < frame.Contacts.Count; i++)
        {
            var contact = frame.Contacts[i];
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
        for (var i = 0; i < frame.Contacts.Count; i++)
        {
            var contact = frame.Contacts[i];
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
        _liftAt = double.NaN;
        _caughtShift = _caughtFrom = _caughtTarget = 0;
        ResetTravel(time);
        _velocity.Clear();
        _flick.Clear();
        _presentation.Reset();
        TrackingDirection = Math.Sign(axis == GestureAxis.Horizontal ? -_travelX : -_travelY);
        TrackingStarted?.Invoke(axis);
        UpdateProgress(time);
    }

    private void UpdateProgress(double time)
    {
        Progress = Axis == GestureAxis.Horizontal ? -_travelX / Options.FullSwipeMm : -_travelY / Options.FullSwipeVerticalMm;
        // The panel follows the fingers to the end, but what decides the gesture stops at the first lift (LiftWindow).
        if (double.IsNaN(_liftAt) || time - _liftAt > LiftWindow)
        {
            _velocity.Add(time, Progress);
            _flick.Add(time, Progress);
            var travel = Progress - _caughtFrom;
            var allowance = TurnAllowanceMm / (Axis == GestureAxis.Horizontal ? Options.FullSwipeMm : Options.FullSwipeVerticalMm);
            _furthestAhead = Math.Max(_furthestAhead, travel);
            _furthestBehind = Math.Min(_furthestBehind, travel);
            if (travel >= _furthestAhead - allowance) _nearAheadAt = time;
            if (travel <= _furthestBehind + allowance) _nearBehindAt = time;
        }
        _presentation.Add(time, Progress);
        Velocity = _velocity.Estimate(time);
        DisplayProgress = Shape(Progress);
    }

    private void Release(double time)
    {
        _interruptedMotion = null;
        // Judged as of the first finger lifting, if the rest followed quickly: not by the fingers leaving the pad.
        var lifting = !double.IsNaN(_liftAt) && time - _liftAt <= LiftWindow;
        var decidedAt = lifting ? _liftAt : time;
        var p = lifting ? _liftProgress : Progress;
        // A flick is judged by how fast the fingers went just before lifting (unless they then turned back), not by the
        // average over the slowing, pause and roll-off that follow it, which made quick flicks spring back.
        var fullSwipe = Axis == GestureAxis.Horizontal ? Options.FullSwipeMm : Options.FullSwipeVerticalMm;
        var average = _velocity.Estimate(decidedAt);
        var v = _flick.Peak(decidedAt, FlickRollBackMm / fullSwipe) is { } peak && Math.Abs(peak) > Math.Abs(average) ? peak : average;
        var flick = Axis == GestureAxis.Horizontal ? Options.FlickVelocity : Options.VerticalFlickVelocity;
        var threshold = Axis == GestureAxis.Horizontal ? Options.CommitProgress : Options.VerticalCommitProgress;

        if (Axis == GestureAxis.Horizontal)
        {
            // As macOS does: a sideways swipe that heads somewhere goes there, however slow or short (past a twitch).
            // Only catching it, turning back before lifting, stops it; nothing is worked out from speeds and distances.
            var travel = p - _caughtFrom;
            var heading = Math.Abs(travel) >= Options.MinSwipeProgress ? Math.Sign(travel) : 0;
            if (heading != 0 && TurnedBack(heading, travel, decidedAt)) heading = 0;
            double target;
            if (heading == 0) target = _caughtTarget; // caught (or barely moved): wherever it was already going
            else if (_caughtShift != 0) target = heading; // a quick series: one more desktop, or back to the last
            else target = heading > 0 ? Math.Ceiling(_caughtFrom + 1e-9) : Math.Floor(_caughtFrom - 1e-9); // the next stop that way
            target = Math.Clamp(target, -1, 1);
            if ((target > 0 && !CanGoPositive) || (target < 0 && !CanGoNegative)) target = 0;
            _target = target;
        }
        else
        {
            var positive = CanGoPositive && ((p >= threshold && v > -flick) || (v >= flick && p >= Options.MinFlickProgress));
            var negative = CanGoNegative && ((p <= -threshold && v < flick) || (v <= -flick && p <= -Options.MinFlickProgress));
            _target = positive ? 1 : negative ? -1 : 0;
        }

        // Hand the release velocity to the spring, as seen through the rubber band.
        const double h = 1e-3;
        // Start from the last displayed position, even if lift reports arrived between display frames.
        var displayed = _presentation.HasPresented ? Shape(_presentation.Value) : DisplayProgress;
        // Decisions use the trailing velocity above; the visual spring inherits the much
        // more recent tracking velocity, including a reversal just before lifting.
        var visualVelocity = _presentation.HasPresented ? _presentation.MotionVelocity(time) : v;
        var visualProgress = _presentation.HasPresented ? _presentation.Value : p;
        var springVelocity = (Shape(visualProgress + visualVelocity * h) - Shape(visualProgress)) / h;
        // Fingers often roll back a little as they leave the pad, so a flick can end with a moment of motion away
        // from where it's going. Carried into the spring, that makes the panel dip backwards before it heads off.
        // As GNOME does, never start the settle moving away from its target: use the steadier trailing velocity
        // the decision was made on if it points the right way, otherwise start from rest.
        if ((_target - displayed) * springVelocity < 0)
        {
            var trailing = (Shape(p + v * h) - Shape(p)) / h;
            springVelocity = (_target - displayed) * trailing > 0 ? trailing : 0;
        }
        // A flick carries its own speed on to the target, even if the fingers slowed or paused before leaving the pad.
        if (_target != 0 && Math.Abs(v) >= flick && Math.Sign(v) == Math.Sign(_target - displayed))
        {
            var flickVelocity = (Shape(p + v * h) - Shape(p)) / h;
            if (Math.Abs(flickVelocity) > Math.Abs(springVelocity)) springVelocity = flickVelocity;
        }
        _settleMotion = SettleMotion.Create(Options.Spring, displayed, springVelocity,
            _target, Options.CommitTolerance, allowOvershoot: Axis == GestureAxis.Vertical && _target > 0);
        DisplayProgress = displayed;
        // The position above was drawn for a predicted display time. Starting its curve
        // at an older hardware lift timestamp would immediately advance it a second time.
        _lastTickTime = _presentation.HasPresented ? Math.Max(time, _presentation.PresentedAt) : time;
        Phase = GesturePhase.Settling;
        SettlingStarted?.Invoke(_settleMotion);
    }

    /// <summary>
    /// Whether the fingers turned back from where they were heading before the decision: came back from their furthest
    /// by more than <see cref="TurnAllowanceMm"/> and a fifth of the way, for longer than fingers rolling off the pad take.
    /// </summary>
    private bool TurnedBack(int heading, double travel, double at)
    {
        var furthest = heading > 0 ? _furthestAhead : -_furthestBehind;
        var back = furthest - travel * heading;
        var allowance = TurnAllowanceMm / Options.FullSwipeMm;
        var stillNearAt = heading > 0 ? _nearAheadAt : _nearBehindAt;
        return back > Math.Max(allowance, furthest * 0.2) && at - stillNearAt > FlickHistory.RollOffTime;
    }

    private void ResetTravel(double time)
    {
        _furthestAhead = _furthestBehind = 0;
        _nearAheadAt = _nearBehindAt = time;
    }

    private void Finish(SwipeAction? action)
    {
        Phase = GesturePhase.Idle;
        Axis = GestureAxis.None;
        _settleMotion = null;
        _interruptedMotion = null;
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

    private void SetTravel(double progress)
    {
        if (Axis == GestureAxis.Horizontal) _travelX = -progress * Options.FullSwipeMm;
        else _travelY = -progress * Options.FullSwipeVerticalMm;
    }

    private double Unshape(double displayed)
    {
        var magnitude = Math.Abs(displayed);
        double InverseBand(double value) => Options.RubberBandLimit * value /
            (0.55 * Math.Max(0.000001, Options.RubberBandLimit - value));
        var canGo = displayed >= 0 ? CanGoPositive : CanGoNegative;
        var raw = !canGo ? InverseBand(magnitude) : magnitude <= 1 ? magnitude : 1 + InverseBand(magnitude - 1);
        return Math.CopySign(raw, displayed);
    }
}
