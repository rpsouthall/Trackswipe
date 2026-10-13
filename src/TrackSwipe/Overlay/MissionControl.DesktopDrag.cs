using System.Diagnostics;
using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

internal sealed partial class MissionControl
{
    private sealed class DesktopDrag(Plan plan, Tile tile, Point press, double started)
    {
        public Plan Plan { get; } = plan;
        public Tile Tile { get; } = tile;
        public Point Press { get; } = press;
        public Point Pointer { get; set; } = press;
        public double Started { get; } = started;
        public bool Lifted { get; set; }
        public int Target { get; set; } = tile.Desktop;
    }

    private sealed record TilePosition(RectangleF From, RectangleF To, SettleMotion? Motion, double Started);

    private DesktopDrag? _desktopDrag;
    private bool _consumePointerRelease;
    private readonly Dictionary<(ThumbnailSurface Surface, Guid Desktop), TilePosition> _tilePositions = new();

    private RectangleF DisplayTile(Plan plan, Tile tile, double now)
    {
        if (!_tilePositions.TryGetValue((plan.Surface, tile.Id), out var position)) return tile.Bounds;
        if (position.Motion is null) return position.To;
        var p = position.Motion.At(now - position.Started);
        return InterpolateFrame(new(0, position.From, 1, null), new(0, position.To, 1, null), 0, p).Bounds;
    }

    private static SpriteFrame MapTileFrame(SpriteFrame frame, Tile tile, RectangleF destination) => frame with
    {
        Bounds = DesktopReorder.Map(frame.Bounds, tile.Bounds, destination),
        Viewport = frame.Viewport is { } viewport ? DesktopReorder.Map(viewport, tile.Bounds, destination) : null,
    };

    private SpriteFrame DisplayFrame(Plan plan, Part part, double now)
    {
        var frame = FrameFor(plan, part, 1, 0, 0);
        return part.TileId is { } id && plan.Tiles.FirstOrDefault(tile => tile.Id == id) is { } tile
            ? MapTileFrame(frame, tile, DisplayTile(plan, tile, now)) : frame;
    }

    private void OnPointerPressed(ThumbnailSurface surface, Point point)
    {
        _consumePointerRelease = false;
        if (IsOpen) WakeScaledCopies(); // a click or drag may follow: compose the scaled copies before they're needed
        if (!IsOpen || PlanFor(surface) is not { } plan) return;
        if (BeginWindowDrag(plan, point)) return;
        if (_barRevealAnimating || BarExpansion < 0.98) return;
        if (_switcher is null || _scene?.Desktops is null || _scene.DesktopCount < 2) return;
        var hit = HitTest(plan, point);
        if (hit.Kind != HitKind.Desktop || plan.Tiles.FirstOrDefault(tile => tile.Desktop == hit.Desktop) is not { } tile) return;
        DisableBarGroup(plan);
        _desktopDrag = new(plan, tile, point, Clock.Now);
        surface.CapturePointer();
        FramesNeeded?.Invoke(); // holding still also lifts the preview after the hold threshold
    }

    private void TickDesktopDrag(double now)
    {
        if (_desktopDrag is not { } drag) return;
        var plan = drag.Plan;
        if (!drag.Lifted)
        {
            if (!DesktopReorder.Starts(drag.Press, drag.Pointer, plan.Surface.Scale, now - drag.Started)) return;
            drag.Lifted = true;
            SetHover(null, default);
            var stack = plan.Parts.Where(part => part.TileId == drag.Tile.Id)
                .SelectMany(part => part.Backing is { } backing ? new[] { backing, part.Sprite } : new[] { part.Sprite }).ToArray();
            plan.Surface.Restack(stack);
            drag.Tile.Decoration!.Raise();
        }

        var original = drag.Tile.Bounds;
        var scale = plan.Surface.Scale;
        var destination = RectangleF.Inflate(original, original.Width * 0.02f, original.Height * 0.02f);
        destination.Offset(drag.Pointer.X - drag.Press.X, drag.Pointer.Y - drag.Press.Y - 4 * scale);
        destination.X = Math.Clamp(destination.X, 0, Math.Max(0, plan.Surface.Monitor.Width - destination.Width));
        destination.Y = Math.Clamp(destination.Y, 0, Math.Max(0, plan.Surface.Monitor.Height - destination.Height));
        _tilePositions[(plan.Surface, drag.Tile.Id)] = new(destination, destination, null, now);
        PlaceTile(plan, drag.Tile, destination);

        var target = DesktopReorder.Target(plan.Tiles.Select(tile => tile.Bounds).ToArray(), destination.X + destination.Width / 2);
        if (target != drag.Target)
        {
            drag.Target = target;
            var order = DesktopReorder.Move(plan.Tiles.Select(tile => tile.Id).ToArray(), drag.Tile.Id, target);
            var motion = SettleMotion.Create(new SpringPreset(0.18, 1), 0, 0, 1);
            foreach (var tile in plan.Tiles.Where(tile => tile.Id != drag.Tile.Id))
                AnimateTile(plan, tile, DisplayTile(plan, tile, now), plan.Tiles[Array.IndexOf(order, tile.Id)].Bounds, motion, now);
        }
    }

    private void PlaceTile(Plan plan, Tile tile, RectangleF destination)
    {
        foreach (var part in plan.Parts.Where(part => part.TileId == tile.Id))
        {
            var frame = MapTileFrame(FrameFor(plan, part, 1, 0, 0), tile, destination);
            part.Sprite.Place(frame.Bounds, (byte)(frame.Opacity * 255), frame.Viewport);
            part.Backing?.Place(frame.Bounds, (byte)(frame.Opacity * 255), frame.Viewport);
        }
        tile.Decoration!.Place(DesktopReorder.Map(TileLabelBounds(tile, plan.Surface.Scale), tile.Bounds, destination), 1);
    }

    private void AnimateTile(Plan plan, Tile tile, RectangleF from, RectangleF to, SettleMotion motion, double now)
    {
        _tilePositions[(plan.Surface, tile.Id)] = new(from, to, motion, now);
        var begin = (long)(now * Stopwatch.Frequency);
        foreach (var part in plan.Parts.Where(part => part.TileId == tile.Id))
        {
            var frame = FrameFor(plan, part, 1, 0, 0);
            var frames = motion.Samples.Select(sample => InterpolateFrame(
                MapTileFrame(frame, tile, from), MapTileFrame(frame, tile, to), sample.Time, sample.Value)).ToArray();
            part.Sprite.Animate(frames, begin);
            part.Backing?.Animate(frames, begin);
        }
        var label = new SpriteFrame(0, TileLabelBounds(tile, plan.Surface.Scale), 1, null);
        tile.Decoration!.Animate(motion.Samples.Select(sample => InterpolateFrame(
            MapTileFrame(label, tile, from), MapTileFrame(label, tile, to), sample.Time, sample.Value)).ToArray(), begin);
    }

    private bool OnPointerReleased(ThumbnailSurface surface, Point point)
    {
        if (_consumePointerRelease) { _consumePointerRelease = false; return true; }
        if (_windowDrag?.Plan.Surface == surface) return EndWindowDrag(point);
        if (_desktopDrag is not { } drag || drag.Plan.Surface != surface) return false;
        drag.Pointer = point;
        TickDesktopDrag(Clock.Now);
        _desktopDrag = null; // clear before releasing capture, which synchronously reports capture loss
        surface.ReleasePointer();
        if (!drag.Lifted) return false; // an ordinary click still selects this desktop
        var bar = drag.Plan.Tiles[0].Bounds;
        var valid = point.Y >= bar.Top - 20 * surface.Scale && point.Y <= bar.Bottom + 60 * surface.Scale;
        if (!valid || drag.Target == drag.Tile.Desktop || !MoveDesktop(drag.Tile.Id, drag.Target)) SnapDesktopTilesBack();
        return true;
    }

    private void OnCaptureLost(ThumbnailSurface surface)
    {
        if (_windowDrag?.Plan.Surface == surface) CancelWindowDrag();
        if (_desktopDrag?.Plan.Surface == surface) CancelDesktopDrag();
    }

    private void CancelDesktopDrag()
    {
        if (_desktopDrag is not { } drag) return;
        _desktopDrag = null;
        _consumePointerRelease = true; // Escape/capture loss must not turn the later button-up into a click
        drag.Plan.Surface.ReleasePointer();
        if (drag.Lifted) SnapDesktopTilesBack();
    }

    private void SnapDesktopTilesBack()
    {
        var now = Clock.Now;
        var motion = SettleMotion.Create(new SpringPreset(0.20, 1), 0, 0, 1);
        foreach (var plan in _plans)
            foreach (var tile in plan.Tiles)
                if (_tilePositions.ContainsKey((plan.Surface, tile.Id)))
                    AnimateTile(plan, tile, DisplayTile(plan, tile, now), tile.Bounds, motion, now);
        _barTransitionUntil = now + motion.Duration;
        Composition.Flush();
        FramesNeeded?.Invoke();
    }

    /// <summary>Persists a drop in Explorer; the viewed desktop stays selected by identity.</summary>
    public bool MoveDesktop(Guid desktop, int index)
    {
        if (!IsOpen || _scene?.Desktops is not { } desktops || _switcher is null || index < 0 || index >= desktops.Count || !desktops.Ids.Contains(desktop)) return false;
        BeginDesktopEdit();
        var expected = DesktopReorder.Move(desktops.Ids, desktop, index);
        var viewed = desktops.Ids[_scene.Current];
        if (!_switcher.TryMoveDesktop(desktop, index)) return false;
        _desktopChange = new(desktop, viewed, viewed, false, 0, true, Clock.Now + 1, expected);
        FramesNeeded?.Invoke();
        return true;
    }

    /// <summary>Exercises the same pointer/hold/drop path on temporary desktops.</summary>
    public bool DragDesktopForDiagnostics(Guid desktop, int index, bool cancel = false)
    {
        ExpandBarForEdit();
        if (!IsOpen || _plans.FirstOrDefault(plan => plan.Tiles.Any(tile => tile.Id == desktop)) is not { } plan ||
            index < 0 || index >= plan.Tiles.Count) return false;
        var tile = plan.Tiles.First(tile => tile.Id == desktop);
        var press = new Point(tile.Bounds.X + tile.Bounds.Width / 2, tile.Bounds.Y + tile.Bounds.Height / 2);
        OnPointerPressed(plan.Surface, press);
        if (_desktopDrag is not { } drag) return false;
        TickDesktopDrag(drag.Started + 0.3);
        var destination = plan.Tiles[index].Bounds;
        var pointer = new Point(destination.X + destination.Width / 2, press.Y);
        OnMouseMoved(plan.Surface, pointer);
        if (cancel) { CancelDesktopDrag(); return OnPointerReleased(plan.Surface, pointer); }
        OnPointerReleased(plan.Surface, pointer);
        return true;
    }
}
