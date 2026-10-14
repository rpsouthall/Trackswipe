using Microsoft.Win32;
using TrackSwipe.Core;
using TrackSwipe.Desktops;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// macOS-style Mission Control. Openness 0 is the desktop exactly as it is; 1 is the overview:
/// the current desktop's windows spread out without overlapping, a bar of live desktop
/// miniatures across the top, the wallpaper dimmed. Everything in between is interpolated, so
/// a gesture can drive it continuously and reverse it.
/// </summary>
internal sealed partial class MissionControl : IDisposable
{
    private enum State { Hidden, Tracking, Open, Closing }

    private enum HitKind { None, Window, Desktop, CloseDesktop, AddDesktop, DisabledControl, DropDesktop }

    private readonly record struct Hit(HitKind Kind, Part? Window = null, int Desktop = -1);

    private const int EscapeHotKey = 1;
    private const byte DimmedWallpaper = 140;

    private readonly Func<IReadOnlyList<ThumbnailSurface>> _surfaces;
    private readonly Func<DesktopScene> _capture;
    private DesktopSwitcher? _switcher;

    /// <summary>Set once Explorer's desktop switching becomes available after starting without it.</summary>
    public DesktopSwitcher? Switcher
    {
        set
        {
            _switcher = value;
            // The cached overview was laid out without desktop controls (no add button): build it afresh next time.
            if (_state == State.Hidden) { _prepared = null; _scene = null; }
        }
    }
    private readonly Swatches _swatches;
    private readonly Dictionary<ThumbnailSurface, GpuDecorationLayer> _decorations = new();
    private readonly Dictionary<ThumbnailSurface, GpuRestoreCover> _freezeCovers = new();
    private readonly List<Plan> _plans = new();
    private readonly Color _accent = ReadAccent();

    private State _state;
    private DesktopScene? _scene;
    private double _openness;
    private double _pan;
    private SettleMotion? _closeMotion;
    private double _lastTick;
    private Part? _focus;
    private (Rectangle From, byte Opacity)? _chosenJourney;
    private Hit _hover;
    private Plan? _hoverPlan;
    private bool _escapeRegistered;
    private int _realDesktop;
    private readonly Queue<Action> _deferred = new();
    private WindowHandoff? _handoff;
    private double? _leaveUntil;
    private readonly Dictionary<(ThumbnailSurface Surface, IntPtr Window, string Slot), Sprite> _reusable = new();
    private readonly UiTimer _cacheExpiry = new() { Interval = 15000 };
    private bool _pendingShow;
    private double? _closeDecidedAt;
    /// <summary>Zooming into a desktop's tile: which desktop, when to switch to it underneath, and when the zoom lands.</summary>
    private (int Desktop, double SwitchAt, double Until, bool Switched)? _zoom;
    private double? _zoomStarted;
    private bool _activatedEarly;

    public MissionControl(Func<IReadOnlyList<ThumbnailSurface>> surfaces, Func<DesktopScene> capture, DesktopSwitcher? switcher, Swatches swatches)
    {
        _surfaces = surfaces;
        _capture = capture;
        _switcher = switcher;
        _swatches = swatches;
        _cacheExpiry.Tick += (_, _) => { _cacheExpiry.Stop(); if (_state == State.Hidden) ReleaseCache(); };
    }

    /// <summary>Raised when an animation starts that needs display frames (see <see cref="Tick"/>).</summary>
    public event Action? FramesNeeded;

    public bool IsVisible => _state != State.Hidden;

    public bool IsOpen => _state == State.Open && !DesktopBarBusy;

    public bool IsAnimating => _state == State.Closing || DesktopBarBusy || _barRevealAnimating || _handoff is not null || _afterWake is not null || _zoom is not null || _focusing is not null;

    /// <summary>Closing, or handing off to the real desktop; a gesture shouldn't reopen it.</summary>
    public bool IsClosing => _state == State.Closing;

    public IEnumerable<IntPtr> DecorationHandles => Array.Empty<IntPtr>();

    /// <summary>Shows Mission Control at openness 0, ready for a gesture to drive it.</summary>
    public void BeginOpening(DesktopScene scene)
    {
        _barReveal.Reset();
        _barRevealAnimating = false;
        _cacheExpiry.Stop();
        var started = Clock.Now;
        _realDesktop = scene.Current;
        var prepared = _prepared == scene && _plans.Count > 0 && _surfaces().SequenceEqual(_plans.Select(plan => plan.Surface));
        if (!prepared)
        {
            Build(scene, defer: true);
            RemoveStale();
            DisposeSpareTileDecorations();
            // Labels are invisible for the first half of opening; render their GPU surfaces in deferred steps.
            foreach (var plan in _plans) _deferred.Enqueue(() => PaintWindowHighlight(plan));
        }
        _prepared = null;
        var built = Clock.Now;
        double place = 0, show = 0;
        foreach (var plan in _plans)
        {
            var a = Clock.Now;
            Render(plan, 0);
            var b = Clock.Now;
            plan.Decoration.SetAlpha(0);
            plan.Decoration.Show();
            plan.CompactLabels?.Show();
            foreach (var pill in plan.CompactPills.Values) pill.Show();
            plan.AddControl?.Show();
            foreach (var tile in plan.Tiles) tile.Decoration!.Show();
            place += b - a;
            show += Clock.Now - b;
        }
        LastBuildCost = prepared
            ? $"prepared as fingers landed; place {place * 1000:0} ms, show {show * 1000:0} ms"
            : $"thumbnails {(built - started) * 1000:0} ms, place {place * 1000:0} ms, show {show * 1000:0} ms, {_deferred.Count} steps deferred";
        _openness = 0;
        _pendingShow = true;
        _state = State.Tracking;
    }

    /// <summary>
    /// Builds the overview for <paramref name="scene"/> while hidden, as fingers land, so that if the
    /// swipe goes up, opening only has to show it. Thumbnails of unchanged windows are reused from the
    /// last preparation, so repeated touches cost little.
    /// </summary>
    public void Prepare(DesktopScene scene)
    {
        if (_state != State.Hidden) return;
        _cacheExpiry.Stop();
        if (_scene is { } previous && SameScene(previous, scene) && _plans.Count > 0 &&
            _surfaces().SequenceEqual(_plans.Select(plan => plan.Surface)))
        {
            _scene = _prepared = scene;
            _cacheExpiry.Start();
            return;
        }
        Build(scene, defer: true); // current spread first; neighbours and miniatures are prepared in bounded steps
        RemoveStale();
        DisposeSpareTileDecorations();
        foreach (var plan in _plans)
        {
            Render(plan, 0);
            plan.Decoration.SetAlpha(0);
            PaintWindowHighlight(plan);
        }
        _prepared = scene;
        _cacheExpiry.Start();
    }

    private static bool SameScene(DesktopScene a, DesktopScene b) => a.Current == b.Current && a.Items.SequenceEqual(b.Items) &&
        (a.Desktops?.Ids ?? Array.Empty<Guid>()).SequenceEqual(b.Desktops?.Ids ?? Array.Empty<Guid>()) &&
        (a.Desktops?.Names ?? Array.Empty<string?>()).SequenceEqual(b.Desktops?.Names ?? Array.Empty<string?>());

    private DesktopScene? _prepared;

    /// <summary>Where the last <see cref="BeginOpening"/> spent its time, for the log.</summary>
    public string LastBuildCost { get; private set; } = "";

    /// <summary>Hands control back to a gesture (swiping down to close).</summary>
    /// <remarks>
    /// If the pointer is over a window, the swipe closes onto it, as clicking it would: it comes to the
    /// front and your fingers carry it to its real position.
    /// </remarks>
    public void BeginGestureClose()
    {
        StopBarReveal();
        RunDeferred(budget: double.PositiveInfinity);
        var target = WindowUnderPointer();
        SetInteractive(false);
        SetHover(null, default);
        if (target is not null) Choose(target);
        _state = State.Tracking;
    }

    public void Render(double openness)
    {
        if (_state == State.Hidden) return;
        _openness = openness;
        foreach (var plan in _plans) Render(plan, _openness);
        ShowPending();
    }

    private void ShowPending()
    {
        if (!_pendingShow) return;
        _pendingShow = false;
        foreach (var plan in _plans) plan.Surface.Show();
    }

    /// <summary>Whether there's a desktop <paramref name="step"/> places away to pan to.</summary>
    public bool CanPan(int step) =>
        _state == State.Open && _scene is { } scene && scene.Current + step >= 0 && scene.Current + step < scene.DesktopCount;

    /// <summary>A sideways swipe started while open: stop taking clicks until it ends.</summary>
    public void BeginPan()
    {
        StopBarReveal();
        RunDeferred(budget: double.PositiveInfinity); // the neighbouring desktops are about to slide in
        SetInteractive(false);
        SetHover(null, default);
    }

    /// <summary>Slides the window spreads sideways: +1 shows the next desktop's spread.</summary>
    public void Pan(double progress)
    {
        if (_state != State.Open) return;
        _pan = progress;
        foreach (var plan in _plans) Render(plan, _openness);
    }

    /// <summary>
    /// Makes the desktop the pan landed on the one being viewed. The real switch waits until Mission
    /// Control closes: switching now would hide and show windows under the overview, and their repaints flicker.
    /// </summary>
    public void CommitPan(int step)
    {
        if (!CanPan(step)) return;
        _pan = 0;
        Rebuild(_scene!.WithCurrent(_scene.Current + step)); // the target spread is now exactly where the pan left it
    }

    /// <summary>A swipe down was released to close: the chosen window can take input already, while it settles.</summary>
    public void CommitGestureClose()
    {
        if (_state == State.Hidden) return;
        _closeDecidedAt = Clock.Now;
        ActivateChosenEarly();
    }

    /// <summary>A swipe down finished closing it: hand off to the viewed desktop.</summary>
    public void FinishGestureClose()
    {
        if (_state == State.Hidden) return;
        _state = State.Closing;
        Closed();
    }

    public void EndPan()
    {
        _pan = 0;
        foreach (var plan in _plans) Render(plan, _openness);
        if (_state == State.Open) { SetInteractive(true); ScheduleSharpen(); }
    }

    /// <summary>Settles fully open and starts taking clicks.</summary>
    public void SetOpen()
    {
        if (_state == State.Hidden) return;
        RunDeferred(budget: double.PositiveInfinity); // fully open: everything must be there to click
        Unchoose(); // a swipe down that snapped back open closes onto nothing
        Render(1);
        _state = State.Open;
        SetInteractive(true);
        foreach (var plan in _plans) Paint(plan);
        if (_plans.FirstOrDefault(plan => plan.Surface.IsPrimary) is { } primary && !_escapeRegistered)
            _escapeRegistered = RegisterHotKey(primary.Decoration.Handle, EscapeHotKey, 0, VK_ESCAPE);
        ScheduleSharpen();
    }

    public void Tick(double now)
    {
        RunAfterWake();
        TickBarReveal(now);
        TickFocus(now);
        RunDeferred(budget: 0.003);
        if (_leaveUntil is { } finish)
        {
            if (now >= finish) Hide();
            return;
        }

        if (_zoom is { } zoom)
        {
            if (!zoom.Switched && now >= zoom.SwitchAt)
            {
                _zoom = zoom with { Switched = true };
                SwitchUnderZoom(zoom.Desktop);
                if (_zoom is null) return; // fell back to the shortcut and hid
            }
            if (_handoff is { } switching)
            {
                switching.Tick(now);
                if (!switching.IsDone) return;
            }
            if (now >= zoom.Until)
            {
                _zoom = null;
                BeginLeave(now);
            }
            return;
        }

        if (_state == State.Closing && _closeMotion is { } motion)
        {
            if (now - _lastTick >= motion.Duration)
            {
                _closeMotion = null;
                Closed();
                return;
            }
        }

        // Closed: switching, restoring and activating underneath until the real desktop matches the overlay.
        if (_handoff is { } handoff)
        {
            handoff.Tick(now);
            if (handoff.IsDone)
            {
                BeginLeave(now);
            }
            return;
        }

        TickDesktopBar(now);
    }

    public void Hide()
    {
        DropSharpThumbnails();
        if (_closeDecidedAt is { } decided && _focus is { } chosen)
            Log.Verbose($"Mission Control closed onto a window: overlay off {(Clock.Now - decided) * 1000:0} ms after the close was decided; " +
                $"input reached it {(_activatedEarly ? "straight away" : "at the hand-off")}, foreground now {GetForegroundWindow() == chosen.Sprite.Item.Window}.");
        _closeDecidedAt = null;
        _activatedEarly = false;
        _zoom = null;
        _focusing = null;
        StopDwell();
        if (_zoomStarted is { } zoomed) Log.Verbose($"Zoom into a desktop: overlay off {(Clock.Now - zoomed) * 1000:0} ms after the click (clock {Clock.Now:0.000}).");
        _zoomStarted = null;
        foreach (var plan in _plans)
        {
            // The other monitors' windows of a desktop zoomed into: only for that zoom, never reused.
            foreach (var part in plan.Parts.Where(part => part.Slot == "zoom"))
            {
                plan.Surface.Remove(part.Sprite);
                if (part.Backing is not null) plan.Surface.Remove(part.Backing);
            }
            plan.Parts.RemoveAll(part => part.Slot == "zoom");
        }
        var handoff = _handoff;
        var complete = _deferred.Count == 0;
        Unchoose();
        SetHover(null, default);
        _state = State.Hidden;
        _pan = 0;
        _closeMotion = null;
        ResetDesktopBar();
        _handoff = null;
        _leaveUntil = null;
        _deferred.Clear();
        _prepared = null;
        _pendingShow = false;
        _reusable.Clear();
        _focus = null;
        _chosenJourney = null;
        _hover = default;
        _hoverPlan = null;
        UnregisterEscape();
        foreach (var plan in _plans)
        {
            plan.Decoration.Hide();
            plan.CompactLabels?.Hide();
            foreach (var pill in plan.CompactPills.Values) pill.Hide();
            plan.AddControl?.Hide();
            plan.Surface.Hide();
            foreach (var tile in plan.Tiles) tile.Decoration!.Hide();
        }
        foreach (var cover in _freezeCovers.Values) cover.Dispose();
        _freezeCovers.Clear();
        Interop.Composition.Gpu.Capture.Dispose();
        handoff?.Finish();
        _cacheExpiry.Stop();
        if (complete && _plans.Sum(plan => plan.Surface.SpriteCount) <= 128) _cacheExpiry.Start();
        else ReleaseCache();
    }

    private void ReleaseCache()
    {
        _prepared = null;
        _deferred.Clear();
        foreach (var plan in _plans)
        {
            plan.Surface.RemoveGroup(plan.BarGroup);
            plan.Surface.Clear();
            foreach (var tile in plan.Tiles) tile.Decoration!.Dispose();
            plan.CompactLabels?.Dispose();
            foreach (var pill in plan.CompactPills.Values) pill.Dispose();
            plan.AddControl?.Dispose();
        }
        _plans.Clear(); _scene = null;
    }

    /// <summary>For diagnostics: where things were laid out, per monitor.</summary>
    public string Describe() => string.Join("; ", _plans.Select(plan =>
        $"monitor {plan.Surface.Monitor.Size.Width}x{plan.Surface.Monitor.Size.Height}: {plan.Parts.Count} thumbnails, " +
        $"{plan.Tiles.Count} desktop tiles{(plan.AddTile is null ? "" : " + add")}, spread windows " +
        string.Join(" ", plan.Windows.Select(part =>
        {
            var shown = Rectangle.Round(OverviewWindowBounds(plan, part)); // where it is on screen now (compact or expanded bar)
            return $"[{part.From.Width}x{part.From.Height} -> {shown.Width}x{shown.Height} at {shown.X},{shown.Y}]";
        }))));

    public void Dispose()
    {
        Hide();
        _cacheExpiry.Dispose();
        _dwell?.Dispose();
        ReleaseCache();
        foreach (var decoration in _decorations.Values) decoration.Dispose();
        _decorations.Clear();
        foreach (var cover in _freezeCovers.Values) cover.Dispose();
        _freezeCovers.Clear();
    }

    /// <summary>Animates closed onto the viewed desktop, as clicking empty space does.</summary>
    public void Dismiss() => Close();

    /// <summary>For diagnostics: the screen point at the centre of <paramref name="window"/> in the spread.</summary>
    public Point? CentreOf(IntPtr window)
    {
        foreach (var plan in _plans)
        {
            if (plan.Windows.FirstOrDefault(part => part.Sprite.Item.Window == window) is not { } part) continue;
            var bounds = OverviewWindowBounds(plan, part);
            return new Point(plan.Surface.Monitor.X + (int)(bounds.X + bounds.Width / 2), plan.Surface.Monitor.Y + (int)(bounds.Y + bounds.Height / 2));
        }
        return null;
    }

    /// <summary>For diagnostics: the screen point at the centre of <paramref name="desktop"/>'s tile in the bar.</summary>
    public Point? TileCentre(int desktop)
    {
        foreach (var plan in _plans)
            if (plan.Tiles.FirstOrDefault(tile => tile.Desktop == desktop) is { } tile)
                return new Point(plan.Surface.Monitor.X + tile.Bounds.X + tile.Bounds.Width / 2, plan.Surface.Monitor.Y + tile.Bounds.Y + tile.Bounds.Height / 2);
        return null;
    }

    /// <summary>For diagnostics: the desktop whose windows the overview shows.</summary>
    public int? ViewedDesktop => _scene?.Current;

    /// <summary>For diagnostics: expands the desktop bar, as pointing at it does.</summary>
    public void ExpandDesktopBar() => SetBarExpanded(true);

    /// <summary>For diagnostics: acts as if <paramref name="desktop"/>'s tile were clicked. False if it can't be.</summary>
    public bool ClickDesktop(int desktop)
    {
        if (!IsOpen || BarExpansion < 0.98 || _plans.All(plan => plan.Tiles.All(tile => tile.Desktop != desktop))) return false;
        SwitchTo(desktop);
        return true;
    }

    /// <summary>For diagnostics: acts as if <paramref name="window"/> were clicked. False if it isn't in the spread.</summary>
    public bool Choose(IntPtr window)
    {
        var part = _plans.SelectMany(plan => plan.Windows).FirstOrDefault(part => part.Sprite.Item.Window == window);
        if (part is null || _state != State.Open) return false;
        Close(part);
        return true;
    }

    /// <summary>Animates closed. <paramref name="focus"/>, if given, is brought to the front and activated once closed.</summary>
    private void Close(Part? focus = null)
    {
        if (_state is State.Hidden or State.Closing || DesktopBarBusy) return;
        StopDwell();
        if (DeferUntilAwake(() => Close(focus))) return; // sharp thumbnails: wake the scaled copies a frame first
        StopBarReveal();
        RunDeferred(budget: double.PositiveInfinity);
        SetInteractive(false);
        SetHover(null, default);
        UnregisterEscape();
        if (focus is not null) Choose(focus);
        _closeDecidedAt = Clock.Now;
        ActivateChosenEarly();
        _closeMotion = SettleMotion.Create(SpringPreset.Snappy, _openness, 0, 0);
        _lastTick = Clock.Now;
        _state = State.Closing;
        Settle(_closeMotion, (long)(_lastTick * System.Diagnostics.Stopwatch.Frequency), value => value);
        FramesNeeded?.Invoke();
        if (int.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_BLOCK_UI_MS"), out var block) && block > 0)
        {
            Log.Write("Compositor stall verification: UI pause started.");
            Thread.Sleep(Math.Clamp(block, 1, 500));
            Log.Write("Compositor stall verification: UI pause finished.");
        }
    }

    /// <summary>
    /// Makes <paramref name="focus"/> the window Mission Control closes onto: it comes to the front and,
    /// once closed, is restored and activated. <see cref="Unchoose"/> reverses this if the close is abandoned.
    /// </summary>
    private void Choose(Part focus)
    {
        // Bring it to the front, but activating a window can't lift it above always-on-top windows, so
        // neither may its thumbnail: those (and the taskbar, which is one) stay above it, in their own order.
        var stack = new List<Sprite>();
        void Push(Part part)
        {
            if (part.Backing is not null) stack.Add(part.Backing);
            stack.Add(part.Sprite);
        }
        Push(focus);
        if (PlanFor(focus.Surface) is { } focusPlan)
        {
            foreach (var part in focusPlan.Parts.Where(part => part != focus && part.Slot == "spread" && part.Sprite.Item.IsTopmost)) Push(part);
            foreach (var part in focusPlan.Parts.Where(part => part.Slot == "taskbar")) Push(part);
        }
        focus.Surface.Restack(stack);

        // A minimized window doesn't shrink away: it flies, fully opaque, to where it will reappear.
        _chosenJourney = (focus.From, focus.FromOpacity);
        if (focus.Sprite.Item.IsMinimized)
        {
            focus.From = focus.Sprite.Home;
            focus.FromOpacity = 255;
        }
        _focus = focus;
        if (focus.Sprite.Item.IsMinimized) Interop.Composition.Gpu.Capture.Prepare(focus.Surface.Monitor);
    }

    /// <summary>
    /// The close is decided. If the chosen window will rest exactly where it is now (on this desktop, not minimized),
    /// raise and activate it straight away, underneath the overlay, which is already click-through: typing and clicks
    /// reach it while the overview is still animating closed, rather than after the hand-off. Windows that have to be
    /// restored or wait for a desktop switch still go through the hand-off.
    /// </summary>
    private void ActivateChosenEarly()
    {
        if (_focus is not { } focus || _scene is null || _scene.Current != _realDesktop || focus.Sprite.Item.IsMinimized) return;
        var window = focus.Sprite.Item.Window;
        if (!IsWindow(window) || IsIconic(window)) return;
        SetWindowPos(window, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        Foreground.Take(window);
        _activatedEarly = GetForegroundWindow() == window;
    }

    /// <summary>Undoes <see cref="Choose"/>, e.g. when a swipe down is released before it closes.</summary>
    private void Unchoose()
    {
        if (_focus is { } focus && _chosenJourney is { } journey)
        {
            focus.From = journey.From;
            focus.FromOpacity = journey.Opacity;
        }
        _focus = null;
        _chosenJourney = null;
    }

    /// <summary>The app window in the spread under the mouse pointer, if any.</summary>
    private Part? WindowUnderPointer()
    {
        if (!GetCursorPos(out var cursor)) return null;
        foreach (var plan in _plans)
        {
            if (!plan.Surface.Monitor.Contains(cursor.X, cursor.Y)) continue;
            var hit = HitTest(plan, new Point(cursor.X - plan.Surface.Monitor.X, cursor.Y - plan.Surface.Monitor.Y));
            return hit.Kind == HitKind.Window ? hit.Window : null;
        }
        return null;
    }

    /// <summary>Shows that desktop's windows spread out, then closes onto them; the real switch happens as it lands.</summary>
    private void SwitchTo(int desktop)
    {
        if (_scene is null) return;
        // The desktop already on show just lands, as clicking empty space does: zooming is for going somewhere else.
        if (desktop != _scene.Current && ZoomInto(desktop)) return;
        if (desktop != _scene.Current) Rebuild(_scene.WithCurrent(desktop));
        Close();
    }

    /// <summary>
    /// Fully closed: the overview now shows the viewed desktop exactly as it will look, the chosen window
    /// (if any) resting where it will reappear. Hand off to the real desktop underneath before removing it.
    /// </summary>
    private void Closed()
    {
        if (_scene is null)
        {
            Hide();
            return;
        }
        Render(0);
        Interop.Composition.Flush(); // on screen before the switch call, which blocks for a few frames
        var target = _scene.Current;
        Guid? switchingTo = null;
        if (target != _realDesktop)
        {
            if (_switcher is null || _scene.Desktops is not { } desktops || !_switcher.TrySwitch(desktops.Ids[target]))
            {
                // No instant switching: step there with the shortcut and let Windows animate.
                var focusWindow = _focus?.Sprite.Item.Window ?? IntPtr.Zero;
                var steps = target - _realDesktop;
                Hide();
                for (var i = 0; i < Math.Abs(steps); i++)
                    KeySender.Send(steps > 0 ? SwipeAction.NextDesktop : SwipeAction.PreviousDesktop);
                if (focusWindow != IntPtr.Zero)
                {
                    if (IsIconic(focusWindow)) ShowWindowAsync(focusWindow, SW_RESTORE);
                    Foreground.Take(focusWindow);
                }
                return;
            }
            switchingTo = desktops.Ids[target];
        }

        if (switchingTo is null && _focus is null)
        {
            BeginLeave(Clock.Now);
            return;
        }
        var focusPlan = _focus is null ? null : PlanFor(_focus.Surface);
        var switcher = _switcher;
        var landing = switchingTo is { } desktop && switcher is not null
            ? new SwitchLanding(_scene.TopWindowOn(target), _scene.TopWindowOn(_realDesktop), DesktopScene.IsShown,
                () => switcher.CurrentDesktopId() == desktop, Clock.Now)
            : null;
        _handoff = new WindowHandoff(switchingTo, landing,
            _focus?.Sprite, _focus?.Backing, _focus?.From ?? Rectangle.Empty, Clock.Now,
            focusPlan is null ? null : (rect, timestamp) => Freeze(focusPlan, rect, timestamp),
            focusPlan is null ? null : (frames, begin) => { if (_freezeCovers.TryGetValue(focusPlan.Surface, out var cover)) cover.Animate(frames, begin); });
        FramesNeeded?.Invoke();
    }

    /// <summary>Covers <paramref name="rect"/> of a surface with a still image of what it shows right now.</summary>
    private bool Freeze(Plan plan, Rectangle rect, long notBefore)
    {
        if (GpuRestoreCover.Capture(plan.Surface, rect, notBefore) is not { } cover) return false;
        Unfreeze(plan);
        _freezeCovers[plan.Surface] = cover;
        return true;
    }

    private void BeginLeave(double now)
    {
        const double duration = 0.10;
        _leaveUntil = now + duration;
        var begin = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var plan in _plans) plan.Surface.FadeOut(begin, duration);
        Interop.Composition.Flush();
        FramesNeeded?.Invoke();
    }

    private void Unfreeze(Plan plan)
    {
        if (_freezeCovers.Remove(plan.Surface, out var cover)) cover.Dispose();
    }

    /// <summary>
    /// Rebuilds in place at the current openness, e.g. after panning or adding a desktop. The new
    /// thumbnails go in on top of the old ones and the old ones only come out once the new ones are
    /// placed, so no frame is ever drawn without them.
    /// </summary>
    private void Rebuild(DesktopScene scene)
    {
        Build(scene);
        foreach (var plan in _plans)
        {
            Render(plan, _openness);
            if (!plan.Surface.IsShown) plan.Surface.Show();
            if (!plan.Decoration.IsShown) plan.Decoration.Show();
            Paint(plan);
        }
        RemoveStale();
        DisposeSpareTileDecorations();
        SetInteractive(_state == State.Open);
    }

    /// <summary>
    /// Runs queued build steps from a deferred <see cref="Build"/> until <paramref name="budget"/> seconds
    /// have passed, then places what they added. Spreads the cost of opening across the first frames.
    /// </summary>
    private void RunDeferred(double budget)
    {
        if (_deferred.Count == 0) return;
        var counts = _plans.Select(plan => plan.Parts.Count).ToArray();
        var until = Clock.Now + budget;
        while (_deferred.Count > 0 && Clock.Now < until) _deferred.Dequeue()();
        for (var i = 0; i < _plans.Count; i++)
            for (var p = counts[i]; p < _plans[i].Parts.Count; p++)
                RenderPart(_plans[i], _plans[i].Parts[p], _openness);
        if (_deferred.Count == 0) RemoveStale();
    }

    /// <summary>Removes the thumbnails the last rebuild didn't reuse.</summary>
    private void RemoveStale()
    {
        if (_deferred.Count > 0) return; // queued roles can still reuse these registrations
        foreach (var sprite in _reusable.Values) sprite.Surface.Remove(sprite);
        _reusable.Clear();
    }

    /// <summary>
    /// The thumbnail for a window in a given role, reusing the one already on screen when there is one.
    /// Replacing it instead would briefly stack old and new, and stacked translucent thumbnails (the
    /// dimmed wallpaper) flash brighter for a frame.
    /// </summary>
    private Sprite? Acquire(ThumbnailSurface surface, SceneItem item, string slot, SceneItem? mirror = null)
    {
        var source = mirror ?? item;
        if (_reusable.Remove((surface, item.Window, slot), out var sprite))
        {
            if (sprite.TryReuse(source)) return sprite;
            surface.Remove(sprite); // the window moved since; its placement would be stale
        }
        return surface.Add(source);
    }

    /// <summary>A Mica-coloured backing for a window that relies on a system backdrop, registered first so it sits underneath.</summary>
    private Sprite? AcquireBacking(ThumbnailSurface surface, SceneItem item, string slot) =>
        item.NeedsBacking ? Acquire(surface, item, slot + "-back", _swatches.BackingFor(item)) : null;

    /// <param name="defer">Register only what the first frame shows; queue the rest for <see cref="RunDeferred"/>.</param>
    private void Build(DesktopScene scene, bool defer = false)
    {
        RecycleTileDecorations();
        _deferred.Clear();
        _prepared = null;
        _scene = scene;
        _hover = default;
        _hoverPlan = null;
        // Only thumbnails on surfaces that still exist: a monitor change recreates the surfaces, and disposing
        // the old ones released every sprite on them.
        var live = _surfaces();
        foreach (var part in _plans.Where(plan => live.Contains(plan.Surface)).SelectMany(plan => plan.Parts))
        {
            _reusable.TryAdd((part.Surface, part.Key, part.Slot), part.Sprite);
            if (part.Backing is not null) _reusable.TryAdd((part.Surface, part.Key, part.Slot + "-back"), part.Backing);
        }
        foreach (var plan in _plans)
        {
            plan.Surface.RemoveGroup(plan.BarGroup);
            plan.CompactLabels?.Dispose();
            foreach (var pill in plan.CompactPills.Values) pill.Dispose();
            plan.AddControl?.Dispose();
        }
        _plans.Clear();

        foreach (var surface in _surfaces())
        {
            var plan = new Plan(surface, DecorationFor(surface));
            surface.MouseMoved -= OnMouseMoved;
            surface.MouseMoved += OnMouseMoved;
            surface.Clicked -= OnClicked;
            surface.Clicked += OnClicked;
            surface.PointerPressed -= OnPointerPressed;
            surface.PointerPressed += OnPointerPressed;
            surface.PointerReleased -= OnPointerReleased;
            surface.PointerReleased += OnPointerReleased;
            surface.CaptureLost -= OnCaptureLost;
            surface.CaptureLost += OnCaptureLost;
            BuildPlan(plan, scene, defer ? _deferred : null);
            _plans.Add(plan);
        }
        foreach (var sprite in _reusable.Values) sprite.Place(Rectangle.Empty);
    }

    private void BuildPlan(Plan plan, DesktopScene scene, Queue<Action>? later)
    {
        var surface = plan.Surface;
        var screen = new Rectangle(Point.Empty, surface.Monitor.Size);
        var work = surface.WorkArea;
        work.Offset(-surface.Monitor.X, -surface.Monitor.Y);
        var s = surface.Scale;
        var margin = (int)(40 * s);

        // Desktop bar along the top of the primary monitor.
        var top = work.Top + margin;
        if (surface.IsPrimary)
        {
            var count = scene.DesktopCount;
            var bar = DesktopBarLayout.Arrange(screen.Size, work, s, count, _switcher is not null && scene.Desktops is not null);
            for (var i = 0; i < count; i++)
            {
                var label = scene.Desktops?.Label(i) ?? "Desktop";
                var tile = new Tile(i, scene.Desktops?.Ids[i] ?? Guid.Empty, bar.Tiles[i], label,
                    DesktopBarLayout.CloseButton(bar.Tiles[i], s));
                AttachTileDecoration(plan, tile);
                plan.Tiles.Add(tile);
            }
            plan.AddTile = bar.AddButton;
            CreateBarLabels(plan);
            top = bar.SpreadTop;
        }
        var area = Rectangle.FromLTRB(work.Left + margin, top, work.Right - margin, work.Bottom - margin);

        // Wallpaper stays put and dims.
        foreach (var wallpaper in scene.Wallpapers)
        {
            if (!wallpaper.Bounds.IntersectsWith(surface.Monitor) || Acquire(surface, wallpaper, "wallpaper") is not { } sprite) continue;
            plan.Parts.Add(new Part(surface, sprite, sprite.Home, sprite.Home, 255, DimmedWallpaper) { Slot = "wallpaper" });
        }

        // The current desktop's windows spread out. Its neighbours' spreads wait one screen to either
        // side, so a sideways swipe can pan to them while the bar stays put.
        // The neighbours are off-screen until a pan, so with a deferred build they come in over the first frames.
        BuildSpread(plan, scene, scene.Current, 0, area, isCurrent: true);
        foreach (var layer in new[] { -1, 1 })
        {
            var desktop = scene.Current + layer;
            if (desktop >= 0 && desktop < scene.DesktopCount) Step(() => BuildSpread(plan, scene, desktop, layer * screen.Width, area, isCurrent: false));
        }

        // Desktop miniatures: each desktop's wallpaper and on-screen windows, scaled into its tile. They slide down from above.
        foreach (var tile in plan.Tiles) Step(() => AddTile(tile));
        void AddTile(Tile tile)
        {
            var scale = tile.Bounds.Width / (double)screen.Width;
            var lift = tile.Bounds.Bottom + (int)(20 * s);
            var items = scene.Wallpapers.Concat(scene.VisibleWindowsOn(tile.Desktop).Where(item => item.IsAppWindow));
            foreach (var item in items)
            {
                var slot = $"tile:{tile.Id}";
                if (!item.Bounds.IntersectsWith(surface.Monitor)) continue;
                var backing = AcquireBacking(surface, item, slot);
                if (Acquire(surface, item, slot) is not { } sprite) continue;
                var home = sprite.Home;
                var to = new Rectangle(
                    tile.Bounds.X + (int)Math.Round(home.X * scale),
                    tile.Bounds.Y + (int)Math.Round(home.Y * scale),
                    Math.Max(1, (int)Math.Round(home.Width * scale)),
                    Math.Max(1, (int)Math.Round(home.Height * scale)));
                var from = to;
                from.Offset(0, -lift);
                var clipFrom = tile.Bounds;
                clipFrom.Offset(0, -lift);
                plan.Parts.Add(new Part(surface, sprite, from, to, 0, 255)
                {
                    Slot = slot,
                    Backing = backing,
                    ClipFrom = clipFrom,
                    ClipTo = tile.Bounds,
                    TileId = tile.Id,
                });
            }
        }

        // Highlight ring around the viewed desktop's tile. While panning it glides towards the tile being swiped to.
        if (plan.Tiles.Count > 0 && plan.Tiles.FirstOrDefault(tile => tile.Desktop == scene.Current) is { } viewed) Step(() => AddRing(viewed));
        void AddRing(Tile viewed)
        {
            var (ring, overhang) = _swatches.Ring(viewed.Bounds.Size, s, _accent);
            if (Acquire(surface, ring, "ring") is { } sprite)
            {
                Rectangle RingAround(Tile tile) => Rectangle.Inflate(tile.Bounds, overhang, overhang);
                var to = RingAround(viewed);
                var from = to;
                from.Offset(0, -(viewed.Bounds.Bottom + (int)(20 * s)));
                plan.Parts.Add(new Part(surface, sprite, from, to, 0, 255)
                {
                    Slot = "ring",
                    PanPrevious = plan.Tiles.FirstOrDefault(tile => tile.Desktop == scene.Current - 1) is { } previous ? RingAround(previous) : null,
                    PanNext = plan.Tiles.FirstOrDefault(tile => tile.Desktop == scene.Current + 1) is { } next ? RingAround(next) : null,
                });
            }
        }

        // Registered now even when the rest is deferred: it's on screen from the first frame. Deferred parts
        // registered after it never overlap it (the bar is at the top, spreads stay above the taskbar).
        foreach (var taskbar in scene.Taskbars)
        {
            if (!taskbar.Bounds.IntersectsWith(surface.Monitor) || Acquire(surface, taskbar, "taskbar") is not { } sprite) continue;
            plan.Parts.Add(new Part(surface, sprite, sprite.Home, sprite.Home, 255, 255) { Slot = "taskbar" });
        }

        void Step(Action build)
        {
            if (later is null) build();
            else later.Enqueue(build);
        }
    }

    /// <summary>
    /// Lays out one desktop's app windows, shifted <paramref name="shift"/> pixels sideways. Visible
    /// windows fly from where they are; minimized ones fade in where they land. Non-app windows fade out.
    /// </summary>
    private void BuildSpread(Plan plan, DesktopScene scene, int desktop, int shift, Rectangle area, bool isCurrent)
    {
        var surface = plan.Surface;
        var screen = new Rectangle(Point.Empty, surface.Monitor.Size);
        var spread = new List<Part>();
        foreach (var item in scene.WindowsOn(desktop))
        {
            // Minimized windows have no monitor of their own; show them on the primary one.
            var onThisMonitor = item.IsMinimized ? surface.IsPrimary : item.Bounds.IntersectsWith(surface.Monitor);
            if (!onThisMonitor) continue;
            var backing = AcquireBacking(surface, item, "spread");
            if (Acquire(surface, item, "spread") is not { } sprite) continue;
            var centre = new Point(sprite.Home.X + sprite.Home.Width / 2, sprite.Home.Y + sprite.Home.Height / 2);
            var spreads = item.IsAppWindow && (item.IsMinimized || screen.Contains(centre));
            var from = sprite.Home;
            from.Offset(shift, 0);
            var viewport = screen;
            viewport.Offset(shift, 0);
            var part = new Part(surface, sprite, from, from, item.IsMinimized ? (byte)0 : (byte)255, spreads ? (byte)255 : (byte)0)
            {
                Pans = true,
                Slot = "spread",
                Backing = backing,
                ClipFrom = viewport,
                ClipTo = viewport,
            };
            plan.Parts.Add(part);
            if (spreads) spread.Add(part);
        }

        var layout = ExposeLayout.Arrange(
            spread.Select(part => new LayoutRect(part.Sprite.Home.X, part.Sprite.Home.Y, part.Sprite.Home.Width, part.Sprite.Home.Height)).ToArray(),
            new LayoutRect(area.X, area.Y, area.Width, area.Height),
            gap: 36 * surface.Scale);
        var compactArea = area;
        if (surface.IsPrimary)
        {
            var compactTop = surface.WorkArea.Top - surface.Monitor.Y + (int)(80 * surface.Scale);
            compactArea = Rectangle.FromLTRB(area.Left, compactTop, area.Right, area.Bottom);
        }
        var compactLayout = compactArea == area ? layout : ExposeLayout.Arrange(
            spread.Select(part => new LayoutRect(part.Sprite.Home.X, part.Sprite.Home.Y, part.Sprite.Home.Width, part.Sprite.Home.Height)).ToArray(),
            new LayoutRect(compactArea.X, compactArea.Y, compactArea.Width, compactArea.Height), gap: 36 * surface.Scale);
        for (var i = 0; i < spread.Count; i++)
        {
            var to = Round(layout[i]);
            to.Offset(shift, 0);
            spread[i].To = to;
            var compact = Round(compactLayout[i]);
            compact.Offset(shift, 0);
            spread[i].CompactTo = compact != to ? compact : null;
            if (spread[i].Sprite.Item.IsMinimized) spread[i].From = Rectangle.Inflate(to, -to.Width / 12, -to.Height / 12);
            if (isCurrent) plan.Windows.Add(spread[i]);
        }
    }

    private void Render(Plan plan, double openness)
    {
        var layout = OverviewPresentation.Layout(openness);
        var expansion = BarExpansion;
        var barProgress = layout * expansion;
        EnableBarGroup(plan);
        if (plan.Tiles.Count > 0)
        {
            var dy = (float)(-BarLift(plan) * (1 - barProgress));
            if (dy != plan.BarOffset) { plan.BarGroup.SetTranslation(0, dy); plan.BarOffset = dy; }
            if ((float)barProgress != plan.BarOpacity) { plan.BarGroup.SetOpacity((float)barProgress); plan.BarOpacity = (float)barProgress; }
        }
        foreach (var part in plan.Parts)
            if (part.TileId is null) RenderPart(plan, part, openness);
        RectangleF decoration = plan.Decoration.Bounds;
        plan.Decoration.Place(decoration, (float)Math.Clamp(layout * 2 - 1, 0, 1));
        foreach (var tile in plan.Tiles)
        {
            var frame = TileLabelFrame(plan, tile, openness, 0, expansion);
            tile.Decoration!.Place(frame.Bounds, frame.Opacity);
        }
        RenderBarLabels(plan, openness, expansion);
    }

    private void EnableBarGroup(Plan plan)
    {
        if (plan.BarEnabled) return;
        plan.BarEnabled = true;
        plan.BarGroup.SetVisible(true);
        foreach (var part in plan.Parts.Where(part => part.TileId is not null)) PlaceInBarGroup(plan, part);
        plan.Surface.RaiseGroup(plan.BarGroup);
    }

    private static void PlaceInBarGroup(Plan plan, Part part)
    {
        if (part.Backing is { } backing) { backing.Place(part.To, 255, part.ClipTo); plan.Surface.Group(backing, plan.BarGroup); }
        part.Sprite.Place(part.To, 255, part.ClipTo);
        plan.Surface.Group(part.Sprite, plan.BarGroup);
    }

    private static void DisableBarGroup(Plan plan)
    {
        if (!plan.BarEnabled) return;
        plan.Surface.Ungroup(plan.BarGroup);
        plan.BarGroup.SetVisible(false);
        plan.BarEnabled = false;
    }

    private void RenderPart(Plan plan, Part part, double openness)
    {
        if (part.TileId is not null && plan.BarEnabled) { PlaceInBarGroup(plan, part); return; }
        var frame = FrameFor(plan, part, openness, _pan, 0);
        var opacity = (byte)Math.Clamp(Math.Round(frame.Opacity * 255), 0, 255);
        part.Backing?.Place(frame.Bounds, opacity, frame.Viewport);
        part.Sprite.Place(frame.Bounds, opacity, frame.Viewport);
    }

    private static RectangleF ToRectangleF(LayoutRect rect) => new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);

    public void Settle(SettleMotion motion, long begin, Func<double, double> openness, bool pan = false)
    {
        StopBarReveal();
        RunDeferred(double.PositiveInfinity);
        foreach (var plan in _plans)
        {
            DisableBarGroup(plan);
            foreach (var part in plan.Parts)
            {
                var frames = motion.Samples.Select(sample => FrameFor(plan, part, openness(sample.Value), pan ? sample.Value : _pan, sample.Time)).ToArray();
                part.Sprite.Animate(frames, begin);
                part.Backing?.Animate(frames, begin);
            }
            AnimateDecorations(plan, motion, begin, openness, pan);
        }
        _openness = openness(motion.Target);
        if (pan) _pan = motion.Target;
        ShowPending();
        Interop.Composition.Flush();
    }

    /// <summary>Labels, highlights and bar controls follow <paramref name="openness"/> along the motion.</summary>
    private void AnimateDecorations(Plan plan, SettleMotion motion, long begin, Func<double, double> openness, bool pan)
    {
        var labels = motion.Samples.Select(sample =>
        {
            var value = openness(sample.Value);
            RectangleF bounds = plan.Decoration.Bounds;
            return new SpriteFrame(sample.Time, bounds, (float)Math.Clamp(value * 2 - 1, 0, 1), null);
        }).ToArray();
        plan.Decoration.Animate(labels, begin);
        var expansion = BarExpansion;
        plan.CompactLabels?.AnimateOpacity(motion.Samples.Select(sample => new SpriteFrame(sample.Time,
            RectangleF.Empty, (float)(Math.Clamp(openness(sample.Value) * 2 - 1, 0, 1) * (1 - expansion)), null)).ToArray(), begin);
        foreach (var (desktop, pill) in plan.CompactPills)
            pill.AnimateOpacity(motion.Samples.Select(sample => new SpriteFrame(sample.Time, RectangleF.Empty,
                CompactPillOpacity(plan, desktop, openness(sample.Value), expansion, pan ? sample.Value : _pan), null)).ToArray(), begin);
        plan.AddControl?.Animate(motion.Samples.Select(sample => AddControlFrame(plan, openness(sample.Value), expansion, sample.Time)).ToArray(), begin);
        foreach (var tile in plan.Tiles)
            tile.Decoration!.Animate(motion.Samples.Select(sample => TileLabelFrame(plan, tile, openness(sample.Value), sample.Time, expansion)).ToArray(), begin);
    }

    /// <summary>
    /// Clicking a desktop in the bar zooms into it, as macOS does: its miniature (wallpaper and windows, live) grows
    /// out of its tile until it fills the screen, covering the overview, while labels fade and the taskbar stays put.
    /// Other monitors cross-fade to that desktop's windows. Once it fills the screen the real switch happens
    /// underneath (<see cref="SwitchUnderZoom"/>). False when there's no tile to zoom or no instant switching.
    /// </summary>
    /// <summary>A little gentler than a gesture's settle: nothing under the fingers to keep up with.</summary>
    private static readonly SpringPreset ZoomSpring = new(0.34, 1.0);

    private bool ZoomInto(int desktop)
    {
        if (_state != State.Open || DesktopBarBusy || _switcher is null || _scene?.Desktops is null) return false;
        if (_plans.FirstOrDefault(plan => plan.Tiles.Any(tile => tile.Desktop == desktop)) is not { } zoomPlan) return false;
        if (DeferUntilAwake(() => SwitchTo(desktop))) return true; // sharp thumbnails: wake the scaled copies a frame first
        var tile = zoomPlan.Tiles.First(tile => tile.Desktop == desktop);
        StopBarReveal();
        RunDeferred(budget: double.PositiveInfinity);
        SetInteractive(false);
        SetHover(null, default);
        UnregisterEscape();
        Unchoose();

        var motion = SettleMotion.Create(ZoomSpring, 0, 0, 1);
        var now = Clock.Now;
        var begin = (long)(now * System.Diagnostics.Stopwatch.Frequency);
        foreach (var plan in _plans)
        {
            DisableBarGroup(plan);
            AnimateDecorations(plan, motion, begin, value => 1 - value, pan: false);
        }

        // The tile's contents scale up as one picture, from the tile to the whole screen, about the one point that
        // stays put. Like a camera, the size grows by the same ratio each step (linearly, the first few percent of
        // the motion would multiply its size and feel like a jump).
        var screen = new Rectangle(Point.Empty, zoomPlan.Surface.Monitor.Size);
        var start = Math.Clamp(tile.Bounds.Width / (double)screen.Width, 0.01, 0.99);
        double Zoomed(double value) => (Math.Pow(start, 1 - value) - start) / (1 - start);
        var stack = new List<Sprite>();
        foreach (var part in zoomPlan.Parts)
        {
            if (part.TileId == tile.Id)
            {
                var frames = motion.Samples.Select(sample => new SpriteFrame(sample.Time,
                    ToRectangleF(LayoutRect.Lerp(ToLayout(part.To), ToLayout(part.Sprite.Home), Zoomed(sample.Value))), 1,
                    ToRectangleF(LayoutRect.Lerp(ToLayout(tile.Bounds), ToLayout(screen), Zoomed(sample.Value))))).ToArray();
                part.Sprite.Animate(frames, begin);
                part.Backing?.Animate(frames, begin);
                if (part.Backing is not null) stack.Add(part.Backing);
                stack.Add(part.Sprite);
            }
            else if (part.Slot == "ring")
            {
                var at = FrameFor(zoomPlan, part, 1, 0, 0);
                part.Sprite.Animate(motion.Samples.Select(sample => at with { Time = sample.Time, Opacity = (float)Math.Max(0, 1 - 3 * sample.Value) }).ToArray(), begin);
            }
        }
        stack.AddRange(zoomPlan.Parts.Where(part => part.Slot == "taskbar").Select(part => part.Sprite));
        zoomPlan.Surface.Restack(stack);

        foreach (var plan in _plans.Where(plan => plan != zoomPlan)) CrossFadeTo(plan, desktop, motion, begin);

        _state = State.Closing;
        // The zoom runs in the compositor, so the switch (which blocks this thread for a few frames) can happen
        // underneath while it finishes: the new desktop is live sooner, and the zoom doesn't stutter.
        var switchAt = motion.Samples.Where(sample => sample.Value >= 0.85).Select(sample => sample.Time).DefaultIfEmpty(motion.Duration).First();
        _zoom = (desktop, now + switchAt, now + motion.Duration, false);
        _zoomStarted = now;
        Log.Verbose($"Zoom into desktop {desktop + 1}: {stack.Count} sprites grow out of its tile over {motion.Duration * 1000:0} ms (clock {now:0.000}).");
        Interop.Composition.Flush();
        FramesNeeded?.Invoke();
        return true;
    }

    /// <summary>A monitor without the desktop's tile: its spread fades away and that desktop's windows fade in where they are.</summary>
    private void CrossFadeTo(Plan plan, int desktop, SettleMotion motion, long begin)
    {
        var surface = plan.Surface;
        foreach (var part in plan.Parts.ToList())
        {
            if (part.Slot == "taskbar") continue;
            var at = FrameFor(plan, part, _openness, _pan, 0);
            var target = part.Slot == "wallpaper" ? 1f : 0f;
            var frames = motion.Samples.Select(sample => at with
            {
                Time = sample.Time,
                Opacity = (float)(at.Opacity + (target - at.Opacity) * sample.Value),
            }).ToArray();
            part.Sprite.Animate(frames, begin);
            part.Backing?.Animate(frames, begin);
        }
        var stack = new List<Sprite>();
        foreach (var item in _scene!.VisibleWindowsOn(desktop).Where(item => item.Bounds.IntersectsWith(surface.Monitor)))
        {
            var backing = item.NeedsBacking ? surface.Add(_swatches.BackingFor(item)) : null;
            if (surface.Add(item) is not { } sprite)
            {
                if (backing is not null) surface.Remove(backing);
                continue;
            }
            var frames = motion.Samples.Select(sample => new SpriteFrame(sample.Time, sprite.Home, (float)sample.Value, null)).ToArray();
            backing?.Animate(frames, begin);
            sprite.Animate(frames, begin);
            plan.Parts.Add(new Part(surface, sprite, sprite.Home, sprite.Home, 255, 255) { Slot = "zoom", Backing = backing });
            if (backing is not null) stack.Add(backing);
            stack.Add(sprite);
        }
        stack.AddRange(plan.Parts.Where(part => part.Slot == "taskbar").Select(part => part.Sprite));
        surface.Restack(stack);
    }

    /// <summary>The zoom nearly fills the screen: switch to that desktop underneath it.</summary>
    private void SwitchUnderZoom(int desktop)
    {
        if (_scene is not { Desktops: { } desktops } scene || _switcher is not { } switcher || desktop == _realDesktop) return;
        var id = desktops.Ids[desktop];
        Log.Verbose($"Zoom nearly there; switching desktop underneath (clock {Clock.Now:0.000}).");
        Interop.Composition.Flush();
        if (!switcher.TrySwitch(id))
        {
            var steps = desktop - _realDesktop;
            Hide();
            for (var i = 0; i < Math.Abs(steps); i++)
                KeySender.Send(steps > 0 ? SwipeAction.NextDesktop : SwipeAction.PreviousDesktop);
            return;
        }
        var landing = new SwitchLanding(scene.TopWindowOn(desktop), scene.TopWindowOn(_realDesktop), DesktopScene.IsShown,
            () => switcher.CurrentDesktopId() == id, Clock.Now);
        _handoff = new WindowHandoff(id, landing, null, null, Rectangle.Empty, Clock.Now);
        FramesNeeded?.Invoke();
    }

    private SpriteFrame FrameFor(Plan plan, Part part, double openness, double pan, double time, double? barExpansion = null)
    {
        var elasticOpenness = openness;
        openness = OverviewPresentation.Layout(openness);
        var expansion = barExpansion ?? BarExpansion;
        if (part.TileId is not null || part.Slot == "ring") openness *= barExpansion ?? BarExpansion;
        var panBy = (float)(pan * plan.Surface.Monitor.Width);
        var target = part.CompactTo is { } compact ? LayoutRect.Lerp(ToLayout(compact), ToLayout(part.To), expansion) : ToLayout(part.To);
        var rect = ToRectangleF(LayoutRect.Lerp(ToLayout(part.From), target, openness));
        if (part.Pans) rect.Offset(-panBy, 0);
        if ((pan > 0 ? part.PanNext : pan < 0 ? part.PanPrevious : null) is { } panTarget)
            rect = ToRectangleF(LayoutRect.Lerp(new LayoutRect(rect.X, rect.Y, rect.Width, rect.Height), ToLayout(panTarget), Math.Min(1, Math.Abs(pan))));
        if (part.Pans) rect = ToRectangleF(OverviewPresentation.WindowBounds(new LayoutRect(rect.X, rect.Y, rect.Width, rect.Height), elasticOpenness));
        RectangleF? clip = null;
        if (part.ClipFrom is { } from && part.ClipTo is { } to)
        {
            var viewport = ToRectangleF(LayoutRect.Lerp(ToLayout(from), ToLayout(to), openness));
            if (part.Pans) viewport.Offset(-panBy, 0);
            clip = viewport;
        }
        var opacity = (float)((part.FromOpacity + (part.ToOpacity - part.FromOpacity) * openness) / 255.0);
        return new SpriteFrame(time, rect, opacity, clip);
    }

    private void OnMouseMoved(ThumbnailSurface surface, Point point)
    {
        if (_windowDrag is { } windowDrag)
        {
            if (windowDrag.Plan.Surface == surface) { windowDrag.Pointer = point; TickWindowDrag(); }
            return;
        }
        if (_desktopDrag is { } drag)
        {
            if (drag.Plan.Surface == surface) { drag.Pointer = point; TickDesktopDrag(Clock.Now); }
            return;
        }
        if (!IsOpen || PlanFor(surface) is not { } plan) return;
        UpdateBarHover(plan, point);
        var hit = HitTest(plan, point);
        SetHover(plan, hit);
        UpdateDwell(hit);
    }

    private void OnClicked(ThumbnailSurface surface, Point point)
    {
        if (!IsOpen || PlanFor(surface) is not { } plan) return;
        if (_barRevealAnimating) return;
        if (plan.Tiles.Any(tile => CompactLabelBounds(plan, tile).Contains(point)) && BarExpansion < 0.98)
        { SetBarExpanded(true); return; }
        var hit = HitTest(plan, point);
        switch (hit.Kind)
        {
            case HitKind.Window: Close(hit.Window); break;
            case HitKind.Desktop: SwitchTo(hit.Desktop); break;
            case HitKind.CloseDesktop: RemoveDesktop(hit.Desktop); break;
            case HitKind.AddDesktop: AddDesktop(); break;
            case HitKind.DisabledControl: break;
            default: Close(); break;
        }
    }

    private void OnHotKey(int id)
    {
        if (id == EscapeHotKey && _windowDrag is not null) { CancelWindowDrag(); return; }
        if (id == EscapeHotKey && _desktopDrag is not null) { CancelDesktopDrag(); return; }
        if (id == EscapeHotKey && _state == State.Open) Close();
    }

    private Hit HitTest(Plan plan, Point point)
    {
        if (_barRevealAnimating) return default;
        if (BarExpansion >= 0.98)
        {
            foreach (var tile in plan.Tiles)
                if (tile.CloseBounds.Contains(point)) return new Hit(
                    _switcher is not null && _scene?.Desktops is not null && _scene.DesktopCount > 1
                        ? HitKind.CloseDesktop : HitKind.DisabledControl, Desktop: tile.Desktop);
            foreach (var tile in plan.Tiles)
                if (tile.Bounds.Contains(point)) return new Hit(HitKind.Desktop, Desktop: tile.Desktop);
            if (plan.AddTile is { } add && add.Contains(point)) return new Hit(HitKind.AddDesktop);
        }
        for (var i = plan.Windows.Count - 1; i >= 0; i--)
            if (OverviewWindowBounds(plan, plan.Windows[i]).Contains(point)) return new Hit(HitKind.Window, plan.Windows[i]);
        return default;
    }

    private void SetHover(Plan? plan, Hit hit)
    {
        if (plan == _hoverPlan && hit == _hover) return;
        var previous = _hoverPlan;
        var previousHit = _hover;
        _hoverPlan = plan;
        _hover = hit;
        if (previous is not null && previous != plan) PaintHover(previous, previousHit, default);
        if (plan is not null) PaintHover(plan, previous == plan ? previousHit : default, hit);
    }

    private void PaintHover(Plan plan, Hit before, Hit after)
    {
        if (before.Kind == HitKind.AddDesktop || after.Kind == HitKind.AddDesktop) PaintAddControl(plan);
        if (before.Kind == HitKind.Window || after.Kind == HitKind.Window) PaintWindowHighlight(plan);
        foreach (var tile in plan.Tiles)
            if ((tile.Desktop == before.Desktop && before.Kind is HitKind.Desktop or HitKind.CloseDesktop or HitKind.DisabledControl or HitKind.DropDesktop) ||
                (tile.Desktop == after.Desktop && after.Kind is HitKind.Desktop or HitKind.CloseDesktop or HitKind.DisabledControl or HitKind.DropDesktop)) PaintTile(plan, tile);
    }

    private void Paint(Plan plan)
    {
        PaintWindowHighlight(plan);
        foreach (var tile in plan.Tiles) PaintTile(plan, tile);
    }

    private void PaintWindowHighlight(Plan plan)
    {
        Rectangle? region = null;
        if (plan == _hoverPlan && _hover.Kind == HitKind.Window && _hover.Window is { } part)
        {
            var bounds = OverviewWindowBounds(plan, part);
            var s = plan.Surface.Scale;
            region = Rectangle.FromLTRB(
                (int)Math.Floor(bounds.Left - 42 * s), (int)Math.Floor(bounds.Top - 6 * s),
                (int)Math.Ceiling(bounds.Right + 42 * s), (int)Math.Ceiling(bounds.Bottom + 54 * s));
        }
        plan.Decoration.PaintSparse(LabelsFor(plan), region);
    }

    /// <summary>
    /// Captures a plan's labels, highlights and buttons for drawing into its GPU surface on the UI thread.
    /// </summary>
    private Action<GpuCanvas> LabelsFor(Plan plan)
    {
        var s = plan.Surface.Scale;
        var hover = plan == _hoverPlan ? _hover : default;
        return g =>
        {
            var radius = 8 * s;


            if (hover.Kind == HitKind.Window && hover.Window is { } window)
            {
                var bounds = OverviewWindowBounds(plan, window);
                g.DrawRounded(_accent, 3 * s, RectangleF.Inflate(bounds, 2 * s, 2 * s), radius);
                var title = DesktopScene.TitleOf(window.Sprite.Item.Window);
                if (title.Length > 0)
                {
                    var size = g.MeasureText(title, 13 * s, true);
                    var width = Math.Min(size.Width + 24 * s, bounds.Width + 80 * s);
                    var pill = new RectangleF(bounds.X + (bounds.Width - width) / 2, bounds.Bottom + 10 * s, width, size.Height + 10 * s);
                    g.FillRounded(Color.FromArgb(215, 32, 32, 36), pill, pill.Height / 2);
                    var text = RectangleF.Inflate(pill, -12 * s, -5 * s);
                    g.DrawText(title, 13 * s, true, Color.White, text);
                }
            }
        };
    }

    private void SetInteractive(bool interactive)
    {
        foreach (var plan in _plans) plan.Surface.Interactive = interactive;
    }

    private void UnregisterEscape()
    {
        if (!_escapeRegistered) return;
        foreach (var decoration in _decorations.Values) UnregisterHotKey(decoration.Handle, EscapeHotKey);
        _escapeRegistered = false;
    }

    private GpuDecorationLayer DecorationFor(ThumbnailSurface surface)
    {
        // Surfaces are recreated when monitors change; drop decorations for ones that are gone.
        foreach (var stale in _decorations.Keys.Where(key => !_surfaces().Contains(key)).ToList())
        {
            _decorations[stale].Dispose();
            _decorations.Remove(stale);
        }
        if (!_decorations.TryGetValue(surface, out var decoration))
        {
            decoration = new GpuDecorationLayer(surface, sparse: true);
            decoration.HotKeyPressed += OnHotKey;
            _decorations[surface] = decoration;
        }
        return decoration;
    }

    private Plan? PlanFor(ThumbnailSurface surface) => _plans.FirstOrDefault(plan => plan.Surface == surface);

    private static LayoutRect ToLayout(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    private static Rectangle Round(LayoutRect r)
    {
        var left = (int)Math.Round(r.X);
        var top = (int)Math.Round(r.Y);
        return Rectangle.FromLTRB(left, top, (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
    }

    /// <summary>The Windows accent colour (stored as ABGR), for highlights.</summary>
    private static Color ReadAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        return key?.GetValue("AccentColor") is int abgr
            ? Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF)
            : Color.FromArgb(0, 120, 212);
    }

    private sealed class Plan(ThumbnailSurface surface, GpuDecorationLayer decoration)
    {
        public TrackSwipe.Interop.CompositionVisual BarGroup { get; } = surface.CreateGroup();
        public bool BarEnabled { get; set; }
        public float BarOffset { get; set; } = float.NaN;
        public float BarOpacity { get; set; } = float.NaN;
        public ThumbnailSurface Surface { get; } = surface;
        public GpuDecorationLayer Decoration { get; } = decoration;
        public GpuDecorationLayer? CompactLabels { get; set; }
        public Dictionary<int, Rectangle> CompactBounds { get; } = new();
        public Dictionary<int, GpuDecorationLayer> CompactPills { get; } = new();
        public GpuDecorationLayer? AddControl { get; set; }
        public List<Part> Parts { get; } = new();
        /// <summary>Spread-out app windows, bottom to top, for hit testing.</summary>
        public List<Part> Windows { get; } = new();
        public List<Tile> Tiles { get; } = new();
        public Rectangle? AddTile { get; set; }
    }

    /// <summary>A sprite's journey from openness 0 to 1.</summary>
    private sealed class Part(ThumbnailSurface surface, Sprite sprite, Rectangle from, Rectangle to, byte fromOpacity, byte toOpacity)
    {
        public ThumbnailSurface Surface { get; } = surface;
        public Sprite Sprite { get; } = sprite;
        public Rectangle From { get; set; } = from;
        public Rectangle To { get; set; } = to;
        public Rectangle? CompactTo { get; set; }
        public byte FromOpacity { get; set; } = fromOpacity;
        public byte ToOpacity { get; } = toOpacity;
        /// <summary>Part of a desktop's window spread, which moves when panning between desktops.</summary>
        public bool Pans { get; init; }
        /// <summary>The window's role (wallpaper, spread, a desktop tile...), so a rebuild can reuse its thumbnail.</summary>
        public string Slot { get; init; } = "";
        /// <summary>Solid Mica-coloured thumbnail kept underneath, for apps that leave their background to the system backdrop.</summary>
        public Sprite? Backing { get; init; }
        /// <summary>The desktop or tile viewport follows its own journey, independently of the window.</summary>
        public Rectangle? ClipFrom { get; init; }
        public Rectangle? ClipTo { get; init; }
        /// <summary>Where this part heads while panning towards the previous/next desktop (the highlight ring).</summary>
        public Rectangle? PanPrevious { get; init; }
        public Rectangle? PanNext { get; init; }
        public Guid? TileId { get; init; }
        public IntPtr Key => Sprite.Item.Window;
    }

    private sealed record Tile(int Desktop, Guid Id, Rectangle Bounds, string Label, Rectangle CloseBounds)
    {
        public GpuDecorationLayer? Decoration { get; set; }
    }
}
