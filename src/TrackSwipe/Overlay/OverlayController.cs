using TrackSwipe.Core;
using TrackSwipe.Desktops;
using System.Diagnostics;

namespace TrackSwipe.Overlay;

/// <summary>
/// Decides what each gesture shows: sideways swipes slide between desktops (or pan between them
/// inside Mission Control), upward swipes open Mission Control and downward swipes close it. Owns the per-monitor thumbnail surfaces both use.
/// Everything runs on the UI thread.
/// </summary>
internal sealed partial class OverlayController : IDisposable
{
    private enum Mode { None, Slide, MissionOpening, MissionClosing, MissionPan, Ignored }

    private readonly GestureEngine _engine;
    private readonly WindowDesktopLookup _windowDesktops = new();
    private readonly DesktopSwitcher? _switcher;
    private readonly Swatches _swatches = new();
    private readonly SurfaceSet _slideSurfaces;
    private readonly SurfaceSet _missionSurfaces;
    private readonly DesktopSlide _slide;
    private readonly MissionControl _mission;
    private Mode _mode;
    private DesktopScene? _scene;
    private Func<bool>? _switchVisible;
    private double _switchStarted;
    private bool _switchSeen;
    private (DesktopScene Scene, double CapturedAt)? _prewarmed;
    private bool _settling;
    private GestureAxis _lastAxis = GestureAxis.Vertical;
    private readonly SceneInvalidation _invalidation = new();
    private DesktopScene? _idleScene;
    private (Rectangle Bounds, Rectangle Work)[] _idleLayout = Array.Empty<(Rectangle, Rectangle)>();

    public OverlayController(GestureEngine engine)
    {
        _engine = engine;
        _switcher = DesktopSwitcher.TryCreate(VirtualDesktops.Read(), out var status);
        SwitcherStatus = status;
        _slideSurfaces = new SurfaceSet(_swatches);
        _missionSurfaces = new SurfaceSet(_swatches);
        _slideSurfaces.Prepare();
        _missionSurfaces.Prepare();
        _slide = new DesktopSlide(_swatches);
        _mission = new MissionControl(_missionSurfaces.Prepare, CaptureScene, _switcher, _swatches);
        _mission.FramesNeeded += () => FramesNeeded?.Invoke();
        _mission.InputBusy = () => _engine.Phase != GesturePhase.Idle;
        // Captured once now; window changes only mark it stale. Nothing is rebuilt in the background: the
        // fingers landing (Prewarm) recapture and prepare while they cross the dead zone.
        _idleScene = CaptureScene();
    }

    public string SwitcherStatus { get; }

    /// <summary>Raised when something starts animating on its own, outside a gesture.</summary>
    public event Action? FramesNeeded;

    public bool NeedsFrames => _switchVisible is not null || _mission.IsAnimating;

    /// <summary>
    /// The right fingers just landed: capture the scene now, while they travel through the dead zone,
    /// so the gesture's first frame doesn't have to wait for it.
    /// </summary>
    public void Prewarm()
    {
        if (_mission.IsOpen) _mission.WakeScaledCopies(); // a pan or close may follow: compose the scaled copies first
        if (_mission.IsVisible || _switchVisible is not null) return;
        var desktops = VirtualDesktops.Read();
        var scene = _invalidation.Available && !_invalidation.Dirty && _idleScene is { } idle &&
            SameDesktops(idle.Desktops, desktops) && _idleLayout.SequenceEqual(Screen.AllScreens.Select(screen => (screen.Bounds, screen.WorkingArea)))
            ? idle : CaptureScene();
        _idleScene = scene;
        _prewarmed = (scene, Clock.Now);
        // Prepare the last-used gesture first. Other directions still use this scene when
        // their axis locks, without registering two complete sets on every finger landing.
        if (_lastAxis == GestureAxis.Horizontal) _slide.Begin(scene, _slideSurfaces.Prepare());
        else _mission.Prepare(scene);
    }

    public void TrackingStarted(GestureAxis axis)
    {
        _lastAxis = axis;
        _settling = false;
        if (_switchVisible is not null) HideSlide();

        if (axis == GestureAxis.Horizontal)
        {
            if (_mission.IsOpen)
            {
                _engine.CanGoPositive = _mission.CanPan(+1);
                _engine.CanGoNegative = _mission.CanPan(-1);
                _mission.BeginPan();
                _mode = Mode.MissionPan;
                return;
            }
            if (_mission.IsVisible)
            {
                Ignore(); // mid-animation
                return;
            }
            _scene = TakeScene(out _);
            var desktops = _scene.Desktops;
            _engine.CanGoPositive = desktops is null || _scene.Current < desktops.Count - 1;
            _engine.CanGoNegative = desktops is null || _scene.Current > 0;
            var surfaces = _slideSurfaces.Prepare();
            _slide.Begin(_scene, surfaces); // usually already done, hidden, as the fingers landed
            _slide.Render(0); // place every thumbnail before showing, so the first frame matches the desktop
            foreach (var surface in surfaces) surface.Show();
            _mode = Mode.Slide;
            return;
        }

        if (_mission.IsOpen)
        {
            _engine.CanGoPositive = false;
            _engine.CanGoNegative = true;
            _mission.BeginGestureClose();
            _mode = Mode.MissionClosing;
        }
        else if (_mission.IsVisible)
        {
            Ignore(); // mid-animation
        }
        else
        {
            _engine.CanGoPositive = true;
            _engine.CanGoNegative = true; // swipe down from the desktop: show desktop
            var started = Clock.Now;
            var scene = TakeScene(out var prewarmed);
            var captured = Clock.Now;
            BeginMission(scene);
            Log.Verbose($"Mission Control start: scene {(captured - started) * 1000:0} ms{(prewarmed ? " (captured as fingers landed)" : "")}, " +
                $"overview {(Clock.Now - captured) * 1000:0} ms ({_mission.LastBuildCost}), {_missionSurfaces.Prepare().Sum(surface => surface.SpriteCount)} thumbnails");
            _mode = Mode.MissionOpening;
        }
    }

    /// <summary>Returns false when the caller should perform the action with a keyboard shortcut instead.</summary>
    public bool Committed(SwipeAction action)
    {
        switch (_mode, action)
        {
            case (Mode.Slide, SwipeAction.NextDesktop or SwipeAction.PreviousDesktop):
                if (TrySwitchInstantly(action)) return true;
                HideSlide(); // fall back to the shortcut, and Windows' own animation
                return false;
            case (Mode.MissionOpening, SwipeAction.SwipeUp):
                _mission.SetOpen();
                return true;
            case (Mode.MissionOpening, SwipeAction.SwipeDown):
                _mission.Hide();
                return false;
            case (Mode.MissionClosing, SwipeAction.SwipeDown):
                _mission.FinishGestureClose();
                return true;
            case (Mode.MissionPan, SwipeAction.NextDesktop or SwipeAction.PreviousDesktop):
                _mission.CommitPan(action == SwipeAction.NextDesktop ? 1 : -1);
                return true;
            case (Mode.Ignored, _):
                return true;
            default:
                return false;
        }
    }

    public void Ended()
    {
        _settling = false;
        switch (_mode)
        {
            case Mode.Slide when _switchVisible is null:
                HideSlide();
                break;
            case Mode.MissionOpening when !_mission.IsOpen:
                _mission.Hide();
                break;
            case Mode.MissionClosing when _mission.IsVisible && !_mission.IsClosing:
                _mission.SetOpen(); // close was cancelled
                break;
            case Mode.MissionPan:
                _mission.EndPan();
                break;
        }
        _mode = Mode.None;
    }

    public void RenderFrame(double now)
    {
        var progress = _engine.DisplayProgress;
        if (_switchVisible is not null) PollSwitch(now);
        else if (!_settling && _mode == Mode.Slide) _slide.Render(progress);
        else if (!_settling && _mode == Mode.MissionOpening) _mission.Render(Math.Max(0, progress));
        else if (!_settling && _mode == Mode.MissionClosing) _mission.Render(1 + progress);
        else if (!_settling && _mode == Mode.MissionPan) _mission.Pan(progress);
        _mission.Tick(now);
    }

    /// <summary>Animates Mission Control closed, as clicking empty space does.</summary>
    public void DismissMissionControl()
    {
        if (_mission.IsOpen) _mission.Dismiss();
    }

    public void SettlingStarted(SettleMotion motion)
    {
        var begin = (long)(_engine.SettleStartedAt * Stopwatch.Frequency);
        if (_mode == Mode.Slide) _slide.Settle(motion, begin);
        else if (_mode == Mode.MissionOpening) _mission.Settle(motion, begin, value => Math.Max(0, value));
        else if (_mode == Mode.MissionClosing) _mission.Settle(motion, begin, value => 1 + value);
        else if (_mode == Mode.MissionPan) _mission.Settle(motion, begin, _ => 1, pan: true);
        else return;
        _settling = true;
    }

    public void ResumeTracking() => _settling = false;

    /// <summary>Drops everything on screen. Used when pausing and after errors.</summary>
    public void Cancel()
    {
        _settling = false;
        _mode = Mode.None;
        _mission.Hide();
        HideSlide();
    }

    public void Dispose()
    {
        _invalidation.Dispose();
        _mission.Dispose();
        _slideSurfaces.Dispose();
        _missionSurfaces.Dispose();
        _swatches.Dispose(); // after the surfaces, whose base thumbnails mirror one of them
    }

    private void Ignore()
    {
        _engine.CanGoPositive = _engine.CanGoNegative = false;
        _mode = Mode.Ignored;
    }

    /// <summary>
    /// Replaces the native animation: the slide already shows the target desktop exactly where it
    /// will be, so switch without animation underneath it, then remove the overlay once the switch lands.
    /// </summary>
    private bool TrySwitchInstantly(SwipeAction action)
    {
        var step = action == SwipeAction.NextDesktop ? 1 : -1;
        if (_switcher is null || _scene?.Desktops?.Neighbour(step) is not { } target) return false;

        _slide.Render(step);
        Interop.Composition.Flush(); // on screen before the switch call, which blocks for a few frames
        if (!_switcher.TrySwitch(target)) return false;
        var switcher = _switcher;
        var arriving = _scene.TopWindowOn(_scene.Current + step);
        var leaving = _scene.TopWindowOn(_scene.Current);
        var landing = new SwitchLanding(arriving, leaving, DesktopScene.IsShown,
            () => switcher.CurrentDesktopId() == target, Clock.Now);
        _switchVisible = () => landing.HasLanded(Clock.Now);
        _switchStarted = Clock.Now;
        _switchSeen = false;
        return true;
    }

    /// <summary>Reveals the destination once its windows are visible in successive compositor frames.</summary>
    private void PollSwitch(double now)
    {
        if (_switchSeen) { HideSlide(); return; }
        if (_switchVisible!()) _switchSeen = true;
        if (now - _switchStarted > 0.5)
        {
            Log.Write("Desktop switch wasn't confirmed in time; removed the overlay anyway.");
            HideSlide();
        }
    }

    /// <summary>Hides the slide. Its thumbnails stay registered, so the next swipe on an unchanged desktop starts instantly.</summary>
    private void HideSlide()
    {
        _slideSurfaces.Hide();
        _switchVisible = null;
    }

    private void BeginMission(DesktopScene scene) => _mission.BeginOpening(scene);

    /// <summary>The scene captured as the fingers landed, if it's recent; otherwise a fresh one.</summary>
    private DesktopScene TakeScene(out bool prewarmed)
    {
        const double lifetime = 1.0;
        var candidate = _prewarmed;
        _prewarmed = null;
        prewarmed = candidate is { } p && Clock.Now - p.CapturedAt < lifetime;
        return prewarmed ? candidate!.Value.Scene : CaptureScene();
    }

    private static bool SameDesktops(VirtualDesktopInfo? a, VirtualDesktopInfo? b) =>
        a is not null && b is not null && a.Index == b.Index && a.Ids.SequenceEqual(b.Ids) && a.Names.SequenceEqual(b.Names);

    private DesktopScene CaptureScene()
    {
        _invalidation.Arm(); // changes from here on mark this capture stale
        _idleLayout = Screen.AllScreens.Select(screen => (screen.Bounds, screen.WorkingArea)).ToArray();
        var exclude = _slideSurfaces.Handles.Concat(_missionSurfaces.Handles).Concat(_mission.DecorationHandles).Concat(_swatches.Handles).ToHashSet();
        return DesktopScene.Capture(VirtualDesktops.Read(), _windowDesktops, exclude);
    }

}
