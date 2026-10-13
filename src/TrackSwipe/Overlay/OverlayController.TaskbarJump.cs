using System.Diagnostics;
using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>
/// Clicking an app on the taskbar whose window is on another desktop makes Windows cut straight to that desktop.
/// This turns the cut into a slide, so you can see where you went: the press prepares a slide from this desktop
/// to that one, the release puts it on screen (matching the screen exactly) before Explorer acts, and once Windows
/// switches underneath, it slides across.
/// </summary>
internal sealed partial class OverlayController
{
    private const double JumpWait = 0.6; // after the release, how long to wait for Windows to switch

    private sealed class TaskbarJump(DesktopScene scene, int target, Guid targetId, IntPtr[] windows, Point point, double pressedAt)
    {
        public DesktopScene Scene { get; } = scene;
        public int Target { get; } = target;
        public Guid TargetId { get; } = targetId;
        public IntPtr[] Windows { get; } = windows;
        public Point Point { get; } = point;
        public double PressedAt { get; } = pressedAt;
        public double? ReleasedAt { get; set; }
    }

    private TaskbarJump? _jump;
    private Task<Dictionary<IntPtr, Rectangle>>? _taskbarButtons;
    private DesktopScene? _taskbarButtonsScene;
    private double _taskbarButtonsAt = double.NegativeInfinity;

    private bool Busy => _mode != Mode.None || _engine.Phase is GesturePhase.Tracking or GesturePhase.Settling ||
        _mission.IsVisible || _switchVisible is not null || _peekLanded is not null;

    /// <summary>Looks up where the taskbar buttons are in the background, so the first click already knows (at startup).</summary>
    public void WarmTaskbarButtons()
    {
        if (_switcher is null || Busy || _jump is not null) return;
        RefreshTaskbarButtons(IdleScene());
    }

    /// <summary>The release of a press was missed: whatever was prepared for it would come too late now.</summary>
    public void TaskbarAbandoned()
    {
        _pendingPress = null;
        if (_jump is { ReleasedAt: null }) _jump = null;
    }

    /// <summary>A press whose buttons were still being looked up: it's judged at the release instead.</summary>
    private (Point Point, double At)? _pendingPress;

    /// <returns>Whether the release matters: something was prepared, or will be judged then.</returns>
    public bool TaskbarPressed(Point point)
    {
        _pendingPress = null;
        _jump = null;
        if (_switcher is null || Busy) return false;
        var scene = IdleScene();
        // Buttons only move when apps open, close or get pinned, so the last lookup is nearly always right; a
        // fresh one runs meanwhile for next time. Only with none yet is the press judged at the release.
        var known = _taskbarButtons is { IsCompletedSuccessfully: true } done ? done.Result : null;
        RefreshTaskbarButtons(scene);
        if (known is null)
        {
            _pendingPress = (point, Clock.Now);
            return true;
        }
        Arm(point, scene, known);
        return _jump is not null;
    }

    /// <summary>Prepares the slide if the app under <paramref name="point"/> is entirely on one other desktop.</summary>
    private void Arm(Point point, DesktopScene scene, Dictionary<IntPtr, Rectangle> lookup)
    {
        var started = Clock.Now;
        if (scene.Desktops is not { } desktops) return;
        var clicked = lookup.Where(pair => Rectangle.Inflate(pair.Value, 1, 1).Contains(point)).Select(pair => pair.Key).ToHashSet();
        var items = scene.Items.Where(item => item.Kind == SceneKind.Window && clicked.Contains(item.Window)).ToList();
        if (items.Count == 0) { Log.Verbose($"Taskbar press at {point.X},{point.Y}: no known app window there."); return; }
        // Only an app that's entirely on one other desktop jumps there; otherwise Windows activates it here, or offers a choice.
        var target = items[0].Desktop;
        if (target < 0 || target >= desktops.Count || target == scene.Current || items.Any(item => item.Desktop != target)) return;
        _slide.Begin(scene, _slideSurfaces.Prepare(), target, clicked);
        _slide.Render(0);
        _jump = new TaskbarJump(scene, target, desktops.Ids[target], clicked.ToArray(), point, Clock.Now);
        Log.Verbose($"Taskbar press on an app on desktop {target + 1} (from {scene.Current + 1}): slide ready in {(Clock.Now - started) * 1000:0} ms.");
    }

    /// <summary>The click is complete; Explorer acts on it once this returns.</summary>
    public void TaskbarReleased(Point point)
    {
        if (_pendingPress is { } pending && _taskbarButtons is { } lookup)
        {
            _pendingPress = null;
            // Explorer is held for this, so only briefly; thumbnails registered now may miss the first frame, so this is the fallback.
            if (lookup.Wait(20) && lookup.IsCompletedSuccessfully && _idleScene is { } scene && !Busy) Arm(pending.Point, scene, lookup.Result);
            if (_jump is null) Log.Verbose("Taskbar press: button positions weren't known in time.");
        }
        if (_jump is not { ReleasedAt: null } jump) return;
        var width = Screen.AllScreens.FirstOrDefault(screen => screen.Bounds.Contains(point))?.Bounds.Width ?? 1920;
        var drag = (int)Math.Ceiling(8 * width / 1280.0);
        if (Math.Abs(point.X - jump.Point.X) > drag || Math.Abs(point.Y - jump.Point.Y) > drag || Clock.Now - jump.PressedAt > 2)
        {
            _jump = null; // dragged (reordering buttons) or held: not a click that switches
            return;
        }
        foreach (var surface in _slideSurfaces.Prepare()) surface.Show();
        Composition.Flush();
        jump.ReleasedAt = Clock.Now;
        FramesNeeded?.Invoke();
    }

    /// <summary>Each frame after the release: slide once Windows switches to the target; stand down if it doesn't.</summary>
    private void PollJump(double now)
    {
        var jump = _jump!;
        var current = _switcher?.CurrentDesktopId();
        if (current == jump.TargetId)
        {
            StartJumpSlide(jump, now);
            return;
        }
        var from = jump.Scene.Desktops!.Ids[jump.Scene.Current];
        if ((current is { } id && id != from) || now - jump.ReleasedAt!.Value > JumpWait)
        {
            // Went somewhere else, or nowhere: the slide still shows this desktop exactly as it is, so just remove it.
            Log.Verbose($"Taskbar click: {(current is { } other && other != from ? "Windows went to a different desktop" : "no desktop switch followed")}; slide removed.");
            _jump = null;
            HideSlide();
        }
    }

    private void StartJumpSlide(TaskbarJump jump, double now)
    {
        _jump = null;
        var scene = jump.Scene;
        var motion = SettleMotion.Create(_engine.Options.Spring, 0, 0, Math.Sign(jump.Target - scene.Current));
        _slide.Settle(motion, (long)(now * Stopwatch.Frequency));
        var end = now + motion.Duration;
        var switcher = _switcher!;
        var targetId = jump.TargetId;
        var arriving = jump.Windows.FirstOrDefault(window => scene.Items.Any(item => item.Window == window && !item.IsMinimized));
        var landing = new SwitchLanding(arriving != IntPtr.Zero ? arriving : scene.TopWindowOn(jump.Target), scene.TopWindowOn(scene.Current),
            DesktopScene.IsShown, () => switcher.CurrentDesktopId() == targetId, now);
        // Remove the overlay once the slide has finished and the real desktop is on screen beneath it.
        _switchVisible = () => Clock.Now >= end && landing.HasLanded(Clock.Now);
        _switchStarted = end;
        _switchSeen = false;
        Log.Verbose($"Taskbar jump to desktop {jump.Target + 1}: Windows switched {(now - jump.ReleasedAt!.Value) * 1000:0} ms after the release; sliding over {motion.Duration * 1000:0} ms.");
    }

    /// <summary>Starts finding each window's taskbar button in the background, unless a recent lookup still fits.</summary>
    private void RefreshTaskbarButtons(DesktopScene scene)
    {
        if (_taskbarButtons is { IsCompleted: false }) return;
        if (_taskbarButtonsScene == scene && Clock.Now - _taskbarButtonsAt < 3) return;
        var windows = scene.Items.Where(item => item.Kind == SceneKind.Window && item.IsAppWindow).Select(item => item.Window).Distinct().ToArray();
        _taskbarButtonsScene = scene;
        _taskbarButtonsAt = Clock.Now;
        _taskbarButtons = Task.Run(() => TaskbarButtons.Find(windows));
    }
}
