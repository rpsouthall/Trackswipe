using TrackSwipe.Core;
using TrackSwipe.Desktops;

namespace TrackSwipe.Overlay;

/// <summary>
/// Decides what each gesture shows: sideways swipes slide between desktops (or pan between them
/// inside Mission Control), upward swipes open Mission Control and downward swipes close it. Owns the per-monitor thumbnail surfaces both use.
/// Everything runs on the UI thread.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private enum Mode { None, Slide, MissionOpening, MissionClosing, MissionPan, Ignored }

    private const double SwitchTimeout = 0.5;

    private readonly GestureEngine _engine;
    private readonly WindowDesktopLookup _windowDesktops = new();
    private readonly DesktopSwitcher? _switcher;
    private readonly List<ThumbnailSurface> _surfaces = new();
    private readonly Swatches _swatches = new();
    private readonly DesktopSlide _slide;
    private readonly MissionControl _mission;
    private (Rectangle Bounds, Rectangle WorkArea)[] _layout = Array.Empty<(Rectangle, Rectangle)>();
    private Mode _mode;
    private DesktopScene? _scene;
    private Func<bool>? _switchVisible;
    private double _switchStarted;
    private bool _switchSeen;

    public OverlayController(GestureEngine engine)
    {
        _engine = engine;
        _switcher = DesktopSwitcher.TryCreate(VirtualDesktops.Read(), out var status);
        SwitcherStatus = status;
        PrepareSurfaces();
        _slide = new DesktopSlide(_swatches);
        _mission = new MissionControl(PrepareSurfaces, CaptureScene, _switcher, _swatches);
        _mission.FramesNeeded += () => FramesNeeded?.Invoke();
    }

    public string SwitcherStatus { get; }

    /// <summary>Raised when something starts animating on its own, outside a gesture.</summary>
    public event Action? FramesNeeded;

    public bool NeedsFrames => _switchVisible is not null || _mission.IsAnimating;

    public void TrackingStarted(GestureAxis axis)
    {
        if (_switchVisible is not null) HideSlide(); // a previous switch is still landing; don't stack on it

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
            _scene = CaptureScene();
            var desktops = _scene.Desktops;
            _engine.CanGoPositive = desktops is null || _scene.Current < desktops.Count - 1;
            _engine.CanGoNegative = desktops is null || _scene.Current > 0;
            var surfaces = PrepareSurfaces();
            _slide.Begin(_scene, surfaces);
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
            var scene = CaptureScene();
            var captured = Clock.Now;
            BeginMission(scene);
            Log.Write($"Mission Control start: scene {(captured - started) * 1000:0} ms, overview {(Clock.Now - captured) * 1000:0} ms, " +
                $"{PrepareSurfaces().Sum(surface => surface.SpriteCount)} thumbnails");
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
        else if (_mode == Mode.Slide) _slide.Render(progress);
        else if (_mode == Mode.MissionOpening) _mission.Render(Math.Max(0, progress));
        else if (_mode == Mode.MissionClosing) _mission.Render(1 + Math.Min(0, progress));
        else if (_mode == Mode.MissionPan) _mission.Pan(progress);
        _mission.Tick(now);
    }

    /// <summary>Animates Mission Control closed, as clicking empty space does.</summary>
    public void DismissMissionControl()
    {
        if (_mission.IsOpen) _mission.Dismiss();
    }

    /// <summary>Drops everything on screen. Used when pausing and after errors.</summary>
    public void Cancel()
    {
        _mode = Mode.None;
        _mission.Hide();
        HideSlide();
    }

    /// <summary>For --diagnose: logs what gestures would see, then holds the slide and Mission Control at fixed points.</summary>
    public void RunDiagnostics(Action done)
    {
        var scene = CaptureScene();
        var surfaces = PrepareSurfaces();
        _slide.Begin(scene, surfaces);
        Log.Write($"Diagnose: {scene.DesktopCount} desktops, current index {scene.Current}; " +
            string.Join(", ", Enumerable.Range(0, scene.DesktopCount).Select(d => $"desktop {d + 1}: {scene.WindowsOn(d).Count(w => w.IsAppWindow)} app windows")) +
            $"; {surfaces.Sum(surface => surface.SpriteCount)} slide thumbnails; {scene.Stats}");
        _slide.Render(0);
        foreach (var surface in surfaces) surface.Show();

        var stages = new (double Until, Action Show)[]
        {
            (1.5, () => _slide.Render(0)),
            (3.0, () => _slide.Render(0.5)),
            (4.5, () => _slide.Render(1)),
            (4.6, () => { HideSlide(); BeginMission(CaptureScene()); }),
            (6.0, () => _mission.Render(0.5)),
            (7.5, () =>
            {
                if (_mission.IsOpen) return;
                _mission.SetOpen();
                Log.Write($"Diagnose Mission Control: {_mission.Describe()}");
            }),
            (9.0, () => _mission.Pan(_mission.CanPan(+1) ? 0.5 : _mission.CanPan(-1) ? -0.5 : 0)),
        };
        // Allow visual inspection without racing the default 1.5-second diagnostic stages.
        var diagnosticSlowdown = double.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLOWDOWN"),
            System.Globalization.CultureInfo.InvariantCulture, out var slowdown) ? Math.Clamp(slowdown, 1, 20) : 1;
        var start = Clock.Now;
        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var t = (Clock.Now - start) / diagnosticSlowdown;
            var stage = stages.FirstOrDefault(s => t < s.Until);
            if (stage.Show is not null)
            {
                stage.Show();
                return;
            }
            timer.Dispose();
            _mission.Hide();
            done();
        };
        timer.Start();
    }

    /// <summary>
    /// For --diagnose-mission: opens Mission Control, pans to a neighbouring desktop and back the way a
    /// swipe would, then closes. Ends on the starting desktop, so nothing really switches. With
    /// <paramref name="switchDirection"/> set, pans that way once and closes there, really switching.
    /// </summary>
    public void RunMissionDiagnostics(Action done, int switchDirection = 0)
    {
        BeginMission(CaptureScene());
        var direction = 0;
        var step = 0;
        var start = Clock.Now;
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0:
                    _mission.SetOpen();
                    direction = switchDirection != 0 ? switchDirection : _mission.CanPan(+1) ? 1 : -1;
                    Log.Write($"Diagnose pan: direction {direction}, {_mission.Describe()}");
                    step++;
                    return;
                case 1 when t < 1.0: return;
                case 1 when t < 1.5: _mission.Pan(direction * (t - 1.0) / 0.5); return;
                case 1: _mission.Pan(direction); _mission.CommitPan(direction); _mission.EndPan(); step++; return;
                case 2 when switchDirection != 0: step = 3; return;
                case 2 when t < 2.1: return;
                case 2 when t < 2.6: _mission.Pan(-direction * (t - 2.1) / 0.5); return;
                case 2: _mission.Pan(-direction); _mission.CommitPan(-direction); _mission.EndPan(); step++; return;
                case 3 when t < 3.2: return;
                case 3: _mission.Dismiss(); step++; return;
                case 4 when _mission.IsVisible && t < 6: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    Log.Write("Diagnose pan: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    /// <summary>
    /// For --diagnose-focus: opens Mission Control, then clicks <paramref name="window"/> and waits for the hand-off.
    /// With <paramref name="bySwipe"/>, points at it instead and swipes down: once abandoned halfway, then for real.
    /// </summary>
    public void RunFocusDiagnostics(IntPtr window, Action done, bool bySwipe = false)
    {
        if (bySwipe)
        {
            RunSwipeDownDiagnostics(window, done);
            return;
        }
        BeginMission(CaptureScene());
        var step = 0;
        var start = Clock.Now;
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0: _mission.SetOpen(); step++; return;
                case 1 when t < 1.0: return;
                case 1:
                    if (!_mission.Choose(window)) Log.Write($"Diagnose focus: window 0x{window:X} isn't in the spread");
                    step++;
                    return;
                case 2 when _mission.IsVisible && t < 6: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    Log.Write("Diagnose focus: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    private void RunSwipeDownDiagnostics(IntPtr window, Action done)
    {
        BeginMission(CaptureScene());
        Interop.NativeMethods.GetCursorPos(out var originalCursor);
        var step = 0;
        var start = Clock.Now;
        var stepStart = start;
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        void Next() { step++; stepStart = Clock.Now; }
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - stepStart;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0:
                    _mission.SetOpen();
                    if (_mission.CentreOf(window) is { } centre) SetCursorPos(centre.X, centre.Y);
                    else Log.Write($"Diagnose swipe: window 0x{window:X} isn't in the spread");
                    Next();
                    return;
                case 1 when t < 0.5: return;
                case 1: _mission.BeginGestureClose(); Next(); return;
                // First swipe: down to 60% open, then back up, released open.
                case 2 when t < 0.25: _mission.Render(1 - 0.4 * t / 0.25); return;
                case 2 when t < 0.5: _mission.Render(0.6 + 0.4 * (t - 0.25) / 0.25); return;
                case 2: _mission.SetOpen(); Log.Write("Diagnose swipe: abandoned swipe released open"); Next(); return;
                case 3 when t < 0.5: return;
                case 3: _mission.BeginGestureClose(); Next(); return;
                // Second swipe: all the way down.
                case 4 when t < 0.35: _mission.Render(1 - t / 0.35); return;
                case 4: _mission.Render(0); _mission.FinishGestureClose(); Next(); return;
                case 5 when _mission.IsVisible && t < 3: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    SetCursorPos(originalCursor.X, originalCursor.Y);
                    Log.Write("Diagnose swipe: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    public void Dispose()
    {
        _mission.Dispose();
        foreach (var surface in _surfaces) surface.Dispose();
        _swatches.Dispose();
        _surfaces.Clear();
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
        if (!_switcher.TrySwitch(target)) return false;

        var sample = _scene.TopWindowOn(_scene.Current + step);
        _switchVisible = () => DesktopScene.IsShown(sample) ?? _switcher.CurrentDesktopId() == target;
        _switchStarted = Clock.Now;
        _switchSeen = false;
        return true;
    }

    /// <summary>Removes the slide one frame after the real switch is visible, so the hand-off is seamless.</summary>
    private void PollSwitch(double now)
    {
        if (_switchSeen || _switchVisible!())
        {
            if (_switchSeen)
            {
                HideSlide();
                return;
            }
            _switchSeen = true;
        }
        if (now - _switchStarted > SwitchTimeout)
        {
            Log.Write("Desktop switch wasn't confirmed in time; removed the overlay anyway.");
            HideSlide();
        }
    }

    private void HideSlide()
    {
        _switchVisible = null;
        if (_mission.IsVisible) return; // the surfaces belong to Mission Control right now
        foreach (var surface in _surfaces)
        {
            surface.Hide();
        }
    }

    private void BeginMission(DesktopScene scene)
    {
        // Slide registrations may be parked on these shared surfaces. Mission Control uses its
        // own stacking and layout, so discard the slide cache before showing the overview.
        if (!_mission.IsVisible)
            foreach (var surface in _surfaces) surface.Clear();
        _mission.BeginOpening(scene);
    }

    private DesktopScene CaptureScene()
    {
        var exclude = _surfaces.Select(surface => surface.Handle).Concat(_mission.DecorationHandles).Concat(_swatches.Handles).ToHashSet();
        return DesktopScene.Capture(VirtualDesktops.Read(), _windowDesktops, exclude);
    }

    /// <summary>Creates one surface per monitor, again whenever the monitor layout changes.</summary>
    private IReadOnlyList<ThumbnailSurface> PrepareSurfaces()
    {
        var screens = Screen.AllScreens;
        var layout = screens.Select(screen => (screen.Bounds, screen.WorkingArea)).ToArray();
        if (_surfaces.Count == 0 || !layout.SequenceEqual(_layout))
        {
            foreach (var surface in _surfaces) surface.Dispose();
            _surfaces.Clear();
            _surfaces.AddRange(screens.Select(screen => new ThumbnailSurface(screen)));
            _layout = layout;
        }
        return _surfaces;
    }
}
