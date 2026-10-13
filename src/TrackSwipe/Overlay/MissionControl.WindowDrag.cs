using System.Diagnostics;
using TrackSwipe.Core;
using TrackSwipe.Desktops;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>
/// Dragging a window from the spread onto a desktop in the bar moves it there, as in macOS Mission Control;
/// dropping it on + makes a new desktop for it. A press that doesn't move is still a click.
/// </summary>
internal sealed partial class MissionControl
{
    private sealed class WindowDrag(Plan plan, Part part, Point press, RectangleF bounds)
    {
        public Plan Plan { get; } = plan;
        public Part Part { get; } = part;
        public Point Press { get; } = press;
        public Point Pointer { get; set; } = press;
        /// <summary>Where the window was in the spread when pressed.</summary>
        public RectangleF Bounds { get; } = bounds;
        public bool Lifted { get; set; }
        public bool StackedOverBar { get; set; }
        public RectangleF Shown { get; set; } = bounds;
    }

    private readonly WindowDesktopLookup _windowDesktops = new();
    private WindowDrag? _windowDrag;
    /// <summary>A dropped window's last frame, which the overview refresh animates on from.</summary>
    private (Part Part, SpriteFrame Frame)? _droppedWindow;

    private bool BeginWindowDrag(Plan plan, Point point)
    {
        if (_switcher is null || _scene?.Desktops is null || _barRevealAnimating) return false;
        var hit = HitTest(plan, point);
        if (hit.Kind != HitKind.Window || hit.Window is not { } part) return false;
        _windowDrag = new(plan, part, point, OverviewWindowBounds(plan, part));
        plan.Surface.CapturePointer();
        return true;
    }

    private void TickWindowDrag()
    {
        if (_windowDrag is not { } drag) return;
        var plan = drag.Plan;
        var s = plan.Surface.Scale;
        if (!drag.Lifted)
        {
            // Movement alone lifts it: holding still and letting go is an ordinary click.
            if (Math.Abs(drag.Pointer.X - drag.Press.X) < 5 * s && Math.Abs(drag.Pointer.Y - drag.Press.Y) < 5 * s) return;
            drag.Lifted = true;
            SetHover(null, default);
            if (!_barReveal.Expanded) SetBarExpanded(true); // the desktops to drop on
            FramesNeeded?.Invoke();
        }
        // Above everything else on its monitor, the desktop bar included (which a deferred reveal may re-raise).
        if (!drag.StackedOverBar || !plan.BarEnabled)
        {
            RaiseDragged(drag);
            drag.StackedOverBar = plan.BarEnabled;
        }

        // Follows the pointer from where it was grabbed, shrinking towards miniature size near the bar.
        var start = drag.Bounds;
        var factor = 1f;
        if (plan.Tiles.Count > 0)
        {
            var tile = plan.Tiles[0].Bounds;
            var small = Math.Min(1f, tile.Width * 0.9f / start.Width);
            var near = Math.Clamp((tile.Bottom + 220 * s - drag.Pointer.Y) / (220 * s), 0, 1);
            factor = 1 + (small - 1) * near;
        }
        float gx = (drag.Press.X - start.X) / start.Width, gy = (drag.Press.Y - start.Y) / start.Height;
        var shown = new RectangleF(0, 0, start.Width * factor, start.Height * factor);
        shown.X = drag.Pointer.X - gx * shown.Width;
        shown.Y = drag.Pointer.Y - gy * shown.Height;
        drag.Shown = shown;
        var viewport = new RectangleF(PointF.Empty, plan.Surface.Monitor.Size);
        drag.Part.Backing?.Place(shown, 255, viewport);
        drag.Part.Sprite.Place(shown, 255, viewport);

        var target = DropTarget(drag);
        SetHover(target.Plan, target.Hit);
    }

    private static void RaiseDragged(WindowDrag drag) => drag.Plan.Surface.Restack(drag.Part.Backing is { } backing
        ? new[] { backing, drag.Part.Sprite } : new[] { drag.Part.Sprite });

    /// <summary>The desktop tile, or the add button, under the pointer: any monitor's bar, once fully revealed.</summary>
    private (Plan? Plan, Hit Hit) DropTarget(WindowDrag drag)
    {
        if (!drag.Lifted || _barRevealAnimating || BarExpansion < 0.98) return (null, default);
        var screen = new Point(drag.Plan.Surface.Monitor.X + drag.Pointer.X, drag.Plan.Surface.Monitor.Y + drag.Pointer.Y);
        foreach (var plan in _plans)
        {
            var point = new Point(screen.X - plan.Surface.Monitor.X, screen.Y - plan.Surface.Monitor.Y);
            foreach (var tile in plan.Tiles)
                if (Rectangle.Inflate(tile.Bounds, (int)(6 * plan.Surface.Scale), (int)(6 * plan.Surface.Scale)).Contains(point))
                    return tile.Desktop == _scene?.Current ? (null, default) : (plan, new Hit(HitKind.DropDesktop, Desktop: tile.Desktop));
            if (plan.AddTile is { } add && add.Contains(point)) return (plan, new Hit(HitKind.AddDesktop));
        }
        return (null, default);
    }

    /// <returns>Whether the button-up was used by the drag (a press that never lifted is a click).</returns>
    private bool EndWindowDrag(Point point)
    {
        if (_windowDrag is not { } drag) return false;
        drag.Pointer = point;
        if (drag.Lifted) TickWindowDrag();
        var target = DropTarget(drag);
        _windowDrag = null; // clear before releasing capture, which synchronously reports capture loss
        drag.Plan.Surface.ReleasePointer();
        if (!drag.Lifted) return false;
        SetHover(null, default);
        if (!DropWindow(drag, target.Plan, target.Hit)) SnapWindowBack(drag);
        return true;
    }

    private void CancelWindowDrag()
    {
        if (_windowDrag is not { } drag) return;
        _windowDrag = null;
        _consumePointerRelease = true; // Escape/capture loss must not turn the later button-up into a click
        drag.Plan.Surface.ReleasePointer();
        SetHover(null, default);
        if (drag.Lifted) SnapWindowBack(drag);
    }

    private bool DropWindow(WindowDrag drag, Plan? targetPlan, Hit hit)
    {
        if (targetPlan is null || _switcher is null || _scene?.Desktops is not { } desktops) return false;
        var window = drag.Part.Sprite.Item.Window;
        var viewed = desktops.Ids[_scene.Current];
        RectangleF landing;
        Guid desktop;
        bool added;
        if (hit.Kind == HitKind.DropDesktop && targetPlan.Tiles.FirstOrDefault(tile => tile.Desktop == hit.Desktop) is { } tile)
        {
            desktop = tile.Id;
            added = false;
            if (!_switcher.TryMoveWindow(window, desktop)) return false;
            // Into the miniature, where the window will appear in that desktop's tile.
            var scale = tile.Bounds.Width / (float)targetPlan.Surface.Monitor.Width;
            var home = drag.Part.Sprite.Home;
            landing = new RectangleF(tile.Bounds.X + home.X * scale, tile.Bounds.Y + home.Y * scale,
                Math.Max(1, home.Width * scale), Math.Max(1, home.Height * scale));
        }
        else if (hit.Kind == HitKind.AddDesktop && targetPlan.AddTile is { } add)
        {
            _refreshingBar = true; // creating a desktop makes Explorer call back; don't tick the bar meanwhile
            try
            {
                if (_switcher.TryCreateDesktop() is not { } created) return false;
                desktop = created;
            }
            finally { _refreshingBar = false; }
            added = true;
            if (!_switcher.TryMoveWindow(window, desktop))
            {
                // The empty desktop stays; show it, as adding one would.
                BeginDesktopEdit();
                _desktopChange = new(desktop, viewed, viewed, true, 0, true, Clock.Now + 1);
                FramesNeeded?.Invoke();
                return false;
            }
            landing = new RectangleF(add.X + add.Width * 0.3f, add.Y + add.Height * 0.3f, add.Width * 0.4f, add.Height * 0.4f);
        }
        else return false;

        // The window is gone from this desktop: fly what's left of it into its new place, then refresh.
        if (drag.Plan != targetPlan) landing = drag.Shown with { Width = 1, Height = 1 }; // another monitor's bar
        BeginDesktopEdit();
        RaiseDragged(drag); // ungrouping the bar put the miniatures above it
        var motion = SettleMotion.Create(new SpringPreset(0.22, 1), 0, 0, 1);
        var now = Clock.Now;
        var from = new SpriteFrame(0, drag.Shown, 1, new RectangleF(PointF.Empty, drag.Plan.Surface.Monitor.Size));
        var to = new SpriteFrame(0, landing, 0, from.Viewport);
        var frames = motion.Samples.Select(sample => InterpolateFrame(from, to, sample.Time, sample.Value) with
            { Opacity = (float)Math.Clamp(1 - Math.Max(0, sample.Value - 0.6) / 0.4, 0, 1) }).ToArray();
        var begin = (long)(now * Stopwatch.Frequency);
        drag.Part.Sprite.Animate(frames, begin);
        drag.Part.Backing?.Animate(frames, begin);
        _droppedWindow = (drag.Part, to);
        _desktopChange = new(desktop, viewed, viewed, added, now + motion.Duration, true, now + motion.Duration + 1, Window: window);
        Composition.Flush();
        FramesNeeded?.Invoke();
        Log.Write($"Moved a window to {(added ? "a new desktop" : $"desktop {hit.Desktop + 1}")} by dragging it in Mission Control.");
        return true;
    }

    private void SnapWindowBack(WindowDrag drag)
    {
        var now = Clock.Now;
        var motion = SettleMotion.Create(new SpringPreset(0.24, 0.9), 0, 0, 1);
        var target = FrameFor(drag.Plan, drag.Part, 1, _pan, 0);
        var from = new SpriteFrame(0, drag.Shown, 1, target.Viewport);
        var begin = (long)(now * Stopwatch.Frequency);
        var frames = motion.Samples.Select(sample => InterpolateFrame(from, target, sample.Time, sample.Value)).ToArray();
        drag.Part.Sprite.Animate(frames, begin);
        drag.Part.Backing?.Animate(frames, begin);
        _barTransitionUntil = now + motion.Duration; // then re-placed at rest, and clickable again
        Composition.Flush();
        FramesNeeded?.Invoke();
    }

    private ThumbnailSurface? _demoSurface;

    /// <summary>
    /// For a recorded demo: presses on <paramref name="window"/> in the spread and reports where its drag should go
    /// (desktop <paramref name="desktopIndex"/>'s tile). Movement and release follow through the demo methods below.
    /// </summary>
    public bool PressWindowForDemo(IntPtr window, int desktopIndex, out Point from, out Point to)
    {
        from = to = default;
        if (!IsOpen) return false;
        foreach (var plan in _plans)
        {
            if (plan.Windows.FirstOrDefault(part => part.Sprite.Item.Window == window) is not { } part) continue;
            if (plan.Tiles.FirstOrDefault(tile => tile.Desktop == desktopIndex) is not { } tile) return false;
            var bounds = OverviewWindowBounds(plan, part);
            from = new Point((int)(bounds.X + bounds.Width * 0.45f), (int)(bounds.Y + bounds.Height * 0.35f));
            to = new Point(tile.Bounds.X + tile.Bounds.Width / 2, tile.Bounds.Y + tile.Bounds.Height / 2);
            _demoSurface = plan.Surface;
            OnPointerPressed(plan.Surface, from);
            return _windowDrag is not null;
        }
        return false;
    }

    public void MovePointerForDemo(Point point) { if (_demoSurface is { } surface) OnMouseMoved(surface, point); }

    public bool ReleasePointerForDemo(Point point) => _demoSurface is { } surface && OnPointerReleased(surface, point);

    /// <summary>
    /// Exercises the real press, move and release path: drags <paramref name="window"/> from the spread onto
    /// desktop <paramref name="desktopIndex"/>'s tile, or onto + when it's -1. False if either isn't there.
    /// </summary>
    public bool DragWindowForDiagnostics(IntPtr window, int desktopIndex)
    {
        ExpandBarForEdit();
        if (!IsOpen) return false;
        foreach (var plan in _plans)
        {
            if (plan.Windows.FirstOrDefault(part => part.Sprite.Item.Window == window) is not { } part) continue;
            var bounds = OverviewWindowBounds(plan, part);
            var press = new Point((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
            Rectangle? target = desktopIndex < 0 ? plan.AddTile : plan.Tiles.FirstOrDefault(tile => tile.Desktop == desktopIndex)?.Bounds;
            if (target is not { } drop) return false;
            OnPointerPressed(plan.Surface, press);
            if (_windowDrag is null) return false;
            // Several moves, as a real drag sends: lift, travel, arrive.
            var end = new Point(drop.X + drop.Width / 2, drop.Y + drop.Height / 2);
            for (var i = 1; i <= 8; i++)
                OnMouseMoved(plan.Surface, new Point(press.X + (end.X - press.X) * i / 8, press.Y + (end.Y - press.Y) * i / 8));
            return OnPointerReleased(plan.Surface, end);
        }
        return false;
    }
}
