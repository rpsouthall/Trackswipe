using TrackSwipe.Core;
using TrackSwipe.Desktops;
using System.Diagnostics;

namespace TrackSwipe.Overlay;

/// <summary>
/// Decides what each gesture shows: sideways swipes slide between desktops (or pan between them
/// inside Mission Control), upward swipes open Mission Control and downward swipes close it, or, from the
/// desktop, slide every window away to show the desktop. Owns the per-monitor thumbnail surfaces they use.
/// Everything runs on the UI thread.
/// </summary>
internal sealed partial class OverlayController : IDisposable
{
    private enum Mode { None, Slide, MissionOpening, MissionClosing, MissionPan, Peek, Ignored }

    private readonly GestureEngine _engine;
    private readonly WindowDesktopLookup _windowDesktops = new();
    private DesktopSwitcher? _switcher;
    private bool _switcherUnsupported;
    private double _switcherRetryAt;
    private UiTimer? _switcherRetry;
    private readonly Swatches _swatches = new();
    private readonly SurfaceSet _slideSurfaces;
    private readonly SurfaceSet _missionSurfaces;
    private readonly SurfaceSet _peekSurfaces;
    private readonly DesktopSlide _slide;
    private readonly DesktopPeek _peek;
    private Func<bool>? _peekLanded;
    private double _peekStarted;
    private bool _peekSeen;
    private bool _lastWasPeek;
    private double _peekNotBefore;
    private readonly List<(IntPtr Window, int Original)> _transitionsDisabled = new();
    private readonly MissionControl _mission;
    private Mode _mode;
    private DesktopScene? _scene;
    private Func<bool>? _switchVisible;
    private double _switchStarted;
    /// <summary>A switch made mid-gesture (a caught swipe moved on to its desktop): when it has reached the screen.</summary>
    private SwitchLanding? _advanceLanding;
    /// <summary>The slide's last release animation, as handed to the compositor, so a mid-gesture switch can carry it on.</summary>
    private (SettleMotion Motion, long Begin)? _slideSettle;
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
        _switcher = DesktopSwitcher.TryCreate(VirtualDesktops.Read(), out var status, out _switcherUnsupported);
#if DIAGNOSTICS
        if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_LATE_EXPLORER") == "1")
        {
            _switcher = null; // as at sign-in, before Explorer's virtual desktops are up
            status = "not ready yet (simulated); trying again shortly";
        }
#endif
        SwitcherStatus = status;
        _slideSurfaces = new SurfaceSet(_swatches);
        _missionSurfaces = new SurfaceSet(_swatches);
        _peekSurfaces = new SurfaceSet(_swatches);
        _slideSurfaces.Prepare();
        _missionSurfaces.Prepare();
        _peekSurfaces.Prepare();
        _slide = new DesktopSlide(_swatches);
        _peek = new DesktopPeek(_swatches);
        _mission = new MissionControl(_missionSurfaces.Prepare, CaptureScene, _switcher, _swatches);
        _mission.FramesNeeded += () => FramesNeeded?.Invoke();
        _mission.InputBusy = () => _engine.Phase != GesturePhase.Idle;
        _engine.AdvanceRequested = AdvanceDesktop;
        // Captured once now; window changes only mark it stale. Nothing is rebuilt in the background: the
        // fingers landing (Prewarm) recapture and prepare while they cross the dead zone.
        _idleScene = CaptureScene();
        if (_switcher is null && !_switcherUnsupported)
        {
            // Started at sign-in before Explorer's virtual desktops were up: keep asking until they are.
            _switcherRetry = new UiTimer { Interval = 2000 };
            _switcherRetry.Tick += (_, _) => RetrySwitcher();
            _switcherRetry.Start();
        }
    }

    public string SwitcherStatus { get; private set; }

    /// <summary>
    /// Connects to Explorer's desktop switching if that failed earlier for a reason that can pass (Explorer still
    /// starting). Without it, desktop swipes fall back to the shortcut (Windows animates again after our slide) and
    /// Mission Control can't add desktops, so it mustn't stay off for the whole session.
    /// </summary>
    private void RetrySwitcher()
    {
        if (_switcher is not null || _switcherUnsupported || Clock.Now < _switcherRetryAt)
        {
            if (_switcher is not null || _switcherUnsupported) StopSwitcherRetry();
            return;
        }
        _switcherRetryAt = Clock.Now + 1.5;
        var switcher = DesktopSwitcher.TryCreate(VirtualDesktops.Read(), out var status, out _switcherUnsupported);
        if (switcher is null)
        {
            if (_switcherUnsupported) { Log.Write($"Instant desktop switching {status}."); StopSwitcherRetry(); }
            return;
        }
        _switcher = switcher;
        SwitcherStatus = status;
        _mission.Switcher = switcher;
        StopSwitcherRetry();
        Log.Write($"Instant desktop switching {status} (Explorer was still starting earlier).");
    }

    private void StopSwitcherRetry()
    {
        _switcherRetry?.Dispose();
        _switcherRetry = null;
    }

    /// <summary>Raised when something starts animating on its own, outside a gesture.</summary>
    public event Action? FramesNeeded;

    public bool NeedsFrames => _switchVisible is not null || _peekLanded is not null || _mission.IsAnimating || _jump is { ReleasedAt: not null };

    /// <summary>
    /// The right fingers just landed: capture the scene now, while they travel through the dead zone,
    /// so the gesture's first frame doesn't have to wait for it.
    /// </summary>
    public void Prewarm()
    {
        if (_switcher is null) RetrySwitcher();
        if (_mission.IsOpen) _mission.WakeScaledCopies(); // a pan or close may follow: compose the scaled copies first
        if (_jump is { ReleasedAt: null }) _jump = null; // fingers on the touchpad: that press wasn't a click after all
        if (_mission.IsVisible || _switchVisible is not null || _peekLanded is not null || _jump is not null) return;
        var scene = IdleScene();
        _prewarmed = (scene, Clock.Now);
        // Prepare the last-used gesture first. Other directions still use this scene when
        // their axis locks, without registering two complete sets on every finger landing.
        if (_lastAxis == GestureAxis.Horizontal) _slide.Begin(scene, _slideSurfaces.Prepare());
        else if (!_lastWasPeek) _mission.Prepare(scene);
        // Show desktop always: it's only this desktop's windows, reused while nothing changes, and a thumbnail
        // registered at the last moment can show blank for its first frame.
        _peek.Begin(scene, _peekSurfaces.Prepare());
    }

    /// <summary>The desktop as it is now: the last capture while nothing has changed since, otherwise a fresh one.</summary>
    private DesktopScene IdleScene()
    {
        var desktops = VirtualDesktops.Read();
        var scene = _invalidation.Available && !_invalidation.Dirty && _idleScene is { } idle &&
            SameDesktops(idle.Desktops, desktops) && _idleLayout.SequenceEqual(Screen.AllScreens.Select(screen => (screen.Bounds, screen.WorkingArea)))
            ? idle : CaptureScene();
        _idleScene = scene;
        return scene;
    }

    public void TrackingStarted(GestureAxis axis)
    {
        _lastAxis = axis;
        _settling = false;
        if (_jump is not null) { _jump = null; HideSlide(); }
        if (_switchVisible is not null) HideSlide();
        if (_peekLanded is not null) HidePeek();

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
        else if (_engine.TrackingDirection < 0)
        {
            // Swipe down from the desktop: every window slides away with the fingers to show the desktop.
            _lastWasPeek = true;
            var scene = TakeScene(out _);
            var surfaces = _peekSurfaces.Prepare();
            _peek.Begin(scene, surfaces); // usually already done, hidden, as the fingers landed
            if (!_peek.HasWindows)
            {
                // Nothing on screen. Win+D would bring back windows an earlier show-desktop hid, so do nothing.
                Ignore();
                return;
            }
            _engine.CanGoPositive = false; // reversing past the start rubber-bands rather than opening Mission Control
            _engine.CanGoNegative = true;
            _peek.Render(0); // place every thumbnail before showing, so the first frame matches the desktop
            foreach (var surface in surfaces) surface.Show();
            _mode = Mode.Peek;
        }
        else
        {
            _lastWasPeek = false;
            _engine.CanGoPositive = true;
            _engine.CanGoNegative = false; // a swipe that starts upwards stays Mission Control; down is its own gesture
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
            case (Mode.Peek, SwipeAction.SwipeDown):
                ShowDesktop();
                return true;
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
            case Mode.Slide when _switchVisible is null && _advanceLanding is { } landing:
                // Back where a mid-gesture switch went: remove the slide once that switch is on screen beneath it.
                _switchVisible = () => landing.HasLanded(Clock.Now);
                _switchStarted = Clock.Now;
                _switchSeen = false;
                _advanceLanding = null;
                break;
            case Mode.Slide when _switchVisible is null:
                HideSlide();
                break;
            case Mode.MissionOpening when !_mission.IsOpen:
                _mission.Hide();
                break;
            case Mode.Peek when _peekLanded is null:
                HidePeek(); // cancelled: the windows are back where they were
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
        else if (_jump is { ReleasedAt: not null }) PollJump(now);
        else if (_peekLanded is not null) PollPeek(now);
        else if (!_settling && _mode == Mode.Slide) _slide.Render(progress);
        else if (!_settling && _mode == Mode.Peek) _peek.Render(-progress);
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
        Log.Verbose($"Release ({_mode}): progress {_engine.Progress:+0.00;-0.00}, velocity {_engine.Velocity:+0.00;-0.00}/s, settling to {motion.Target:+0;-0;0} (clock {Clock.Now:0.000}).");
        var begin = (long)(_engine.SettleStartedAt * Stopwatch.Frequency);
        if (_mode == Mode.Slide) { _slide.Settle(motion, begin); _slideSettle = (motion, begin); }
        else if (_mode == Mode.Peek) _peek.Settle(motion, begin, value => -value);
        else if (_mode == Mode.MissionOpening) _mission.Settle(motion, begin, value => Math.Max(0, value));
        else if (_mode == Mode.MissionClosing)
        {
            _mission.Settle(motion, begin, value => 1 + value);
            if (motion.Target < 0) _mission.CommitGestureClose();
        }
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
        _jump = null;
        _mission.Hide();
        HideSlide();
        HidePeek();
    }

    public void Dispose()
    {
        _invalidation.Dispose();
        _mission.Dispose();
        _slideSurfaces.Dispose();
        _missionSurfaces.Dispose();
        _peekSurfaces.Dispose();
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
        // From the scene's own current desktop: after a mid-gesture switch it's no longer the one first captured.
        if (_switcher is null || _scene?.Desktops is not { } desktops) return false;
        var next = _scene.Current + step;
        if (next < 0 || next >= desktops.Count) return false;
        var target = desktops.Ids[next];
        _advanceLanding = null;

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
        if (_switchSeen) { Log.Verbose($"Desktop slide: switch on screen, slide removed (clock {now:0.000})."); HideSlide(); return; }
        if (_switchVisible!()) _switchSeen = true;
        if (now - _switchStarted > 0.5)
        {
            Log.Write("Desktop switch wasn't confirmed in time; removed the overlay anyway.");
            HideSlide();
        }
    }

    /// <summary>
    /// The windows are in their buttons: minimize them for real underneath, with Windows' own show desktop (asked of
    /// Explorer directly, the same command as the taskbar corner), so Win+D or that corner brings them all back.
    /// Windows' minimize animation is switched off for each window first; where Windows won't allow that, the overlay
    /// stays up until its animation would have finished.
    /// </summary>
    private void ShowDesktop()
    {
        _peek.Render(1);
        Interop.Composition.Flush(); // on screen before Windows starts minimizing underneath
        var windows = _peek.Windows;
        var allDisabled = true;
        foreach (var window in windows)
        {
            int original = 0, disabled = 1;
            if (Interop.NativeMethods.DwmGetWindowAttribute(window, Interop.NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, out original, sizeof(int)) == 0 &&
                Interop.NativeMethods.DwmSetWindowAttribute(window, Interop.NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int)) == 0)
                _transitionsDisabled.Add((window, original));
            else allDisabled = false;
        }
        KeySender.Send(SwipeAction.SwipeDown);
        _peekNotBefore = Clock.Now + (allDisabled ? 0 : 0.35); // Windows' minimize animation takes about 0.25 s
        _peekLanded = () => windows.All(window => !Interop.NativeMethods.IsWindow(window) || Interop.NativeMethods.IsIconic(window) ||
            !Interop.NativeMethods.IsWindowVisible(window) || DesktopScene.IsShown(window) == false);
        _peekStarted = Clock.Now;
        _peekSeen = false;
        FramesNeeded?.Invoke();
    }

    /// <summary>Removes the overlay a frame after every window that slid away is really minimized.</summary>
    private void PollPeek(double now)
    {
        if (_peekSeen)
        {
            Log.Verbose($"Show desktop: overlay off after {(now - _peekStarted) * 1000:0} ms; native animation {(_peekNotBefore > _peekStarted ? "waited out" : "switched off")}.");
            HidePeek();
            return;
        }
        if (_peekLanded!() && now >= _peekNotBefore) _peekSeen = true;
        else if (now - _peekStarted > 1.0)
        {
            Log.Write("Show desktop wasn't confirmed in time; removed the overlay anyway.");
            HidePeek();
        }
    }

    private void HidePeek()
    {
        _peekSurfaces.Hide();
        _peekLanded = null;
        // Windows' own animations back on, so restoring them later animates as usual.
        foreach (var (window, original) in _transitionsDisabled)
        {
            var value = original;
            Interop.NativeMethods.DwmSetWindowAttribute(window, Interop.NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref value, sizeof(int));
        }
        _transitionsDisabled.Clear();
    }

    /// <summary>Hides the slide. Its thumbnails stay registered, so the next swipe on an unchanged desktop starts instantly.</summary>
    private void HideSlide()
    {
        _slideSurfaces.Hide();
        _switchVisible = null;
        _advanceLanding = null;
    }

    /// <summary>
    /// Fingers caught a slide on its way to the neighbouring desktop: switch to it now, underneath, and re-centre the
    /// slide on it, so the same touch can go on to the desktop after it (or back). The picture doesn't change: the
    /// slide is rebuilt around the new desktop and placed at the equivalent position in the same frame.
    /// </summary>
    private bool AdvanceDesktop(int direction, double position)
    {
        if (_mode != Mode.Slide || _switcher is not { } switcher || _scene?.Desktops is not { } desktops) return false;
        var from = _scene.Current;
        var next = from + direction;
        if (next < 0 || next >= desktops.Count) return false;
        var target = desktops.Ids[next];
        if (!switcher.TrySwitch(target)) return false;
        var landing = new SwitchLanding(_scene.TopWindowOn(next), _scene.TopWindowOn(from), DesktopScene.IsShown,
            () => switcher.CurrentDesktopId() == target, Clock.Now);
        _advanceLanding = landing;
        _scene = _scene.WithCurrent(next);
        _engine.CanGoPositive = next < desktops.Count - 1;
        _engine.CanGoNegative = next > 0;
        _slide.Begin(_scene, _slideSurfaces.Prepare());
        // The switch call blocks for a few frames while the compositor carries on animating: continue that same
        // animation, measured from the new desktop, rather than placing the slide where it was when the fingers landed.
        if (_slideSettle is { } settle) _slide.Settle(settle.Motion.Shifted(-direction), settle.Begin);
        else _slide.Render(position);
        Interop.Composition.Flush();
        Log.Verbose($"Caught a swipe on its way to desktop {next + 1}: switched there and carrying on from it (clock {Clock.Now:0.000}).");
        return true;
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
        var exclude = _slideSurfaces.Handles.Concat(_missionSurfaces.Handles).Concat(_peekSurfaces.Handles).Concat(_mission.DecorationHandles).Concat(_swatches.Handles).ToHashSet();
        return DesktopScene.Capture(VirtualDesktops.Read(), _windowDesktops, exclude);
    }

}
