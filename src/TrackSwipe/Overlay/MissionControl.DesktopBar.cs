using System.Diagnostics;
using TrackSwipe.Core;
using TrackSwipe.Desktops;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

internal sealed partial class MissionControl
{
    private sealed record DesktopChange(Guid Desktop, Guid Viewed, Guid Fallback, bool Added,
        double ReadyAt, bool Requested, double Deadline, Guid[]? Order = null, IntPtr Window = default);
    private DesktopChange? _desktopChange;
    private double? _barTransitionUntil;
    private bool _refreshingBar;
    private readonly Dictionary<(ThumbnailSurface Surface, Guid Desktop, Size Size), GpuDecorationLayer> _spareTileDecorations = new();
    private bool DesktopBarBusy => _windowDrag is not null || _desktopDrag is not null || _desktopChange is not null || _barTransitionUntil is not null || _refreshingBar || _focusing is not null;

    private static Rectangle TileLabelBounds(Tile tile, float scale) => Rectangle.FromLTRB(
        tile.Bounds.Left - (int)(12 * scale), tile.Bounds.Top - (int)(4 * scale),
        tile.Bounds.Right + (int)(12 * scale), tile.Bounds.Bottom + (int)(34 * scale));

    private static SpriteFrame TileLabelFrame(Plan plan, Tile tile, double openness, double time, double expansion = 1)
    {
        RectangleF bounds = TileLabelBounds(tile, plan.Surface.Scale);
        var lift = tile.Bounds.Bottom + (int)(20 * plan.Surface.Scale);
        bounds.Offset(0, (float)(-lift * (1 - OverviewPresentation.Layout(openness) * expansion)));
        return new(time, bounds, (float)(Math.Clamp(openness * 2 - 1, 0, 1) * expansion), null);
    }

    private void RecycleTileDecorations()
    {
        DisposeSpareTileDecorations();
        foreach (var plan in _plans)
            foreach (var tile in plan.Tiles)
                if (tile.Decoration is { } decoration)
                    _spareTileDecorations.Add((plan.Surface, tile.Id, decoration.Bounds.Size), decoration);
    }

    private void DisposeSpareTileDecorations()
    {
        foreach (var decoration in _spareTileDecorations.Values) decoration.Dispose();
        _spareTileDecorations.Clear();
    }

    private void AttachTileDecoration(Plan plan, Tile tile)
    {
        var bounds = TileLabelBounds(tile, plan.Surface.Scale);
        tile.Decoration = _spareTileDecorations.Remove((plan.Surface, tile.Id, bounds.Size), out var previous)
            ? previous : new GpuDecorationLayer(plan.Surface, bounds, localCoordinates: true);
        PaintTile(plan, tile);
        if (IsVisible) tile.Decoration.Show();
    }

    private void PaintTile(Plan plan, Tile tile)
    {
        var s = plan.Surface.Scale;
        var origin = TileLabelBounds(tile, s).Location;
        var bounds = tile.Bounds; bounds.Offset(-origin.X, -origin.Y);
        var close = tile.CloseBounds; close.Offset(-origin.X, -origin.Y);
        var hover = plan == _hoverPlan ? _hover : default;
        var enabled = _switcher is not null && _scene?.Desktops is not null && _scene.DesktopCount > 1;
        tile.Decoration!.Paint(g =>
        {
            if (tile.Desktop != _scene?.Current && hover.Kind == HitKind.Desktop && hover.Desktop == tile.Desktop)
                g.DrawRounded(Color.FromArgb(170, 255, 255, 255), 2 * s, RectangleF.Inflate(bounds, 3 * s, 3 * s), 8 * s);
            if (hover.Kind == HitKind.DropDesktop && hover.Desktop == tile.Desktop) // a window held over it lands here
                g.DrawRounded(_accent, 3 * s, RectangleF.Inflate(bounds, 3 * s, 3 * s), 8 * s);
            var label = new RectangleF(bounds.Left - 10 * s, bounds.Bottom + 8 * s, bounds.Width + 20 * s, 20 * s);
            g.DrawText(tile.Label, 13 * s, tile.Desktop == _scene?.Current, Color.White, label);
            var showClose = hover.Desktop == tile.Desktop &&
                hover.Kind is HitKind.Desktop or HitKind.CloseDesktop or HitKind.DisabledControl;
            if (showClose)
            {
                var hovered = enabled && hover.Kind == HitKind.CloseDesktop;
                g.FillRounded(Color.FromArgb(hovered ? 64 : 26, 255, 255, 255), close, 0);
                var color = Color.FromArgb(enabled ? hovered ? 255 : 210 : 90, 255, 255, 255);
                var arm = Math.Min(3.5f * s, close.Width * 0.22f);
                var cx = close.X + close.Width / 2f;
                var cy = close.Y + close.Height / 2f;
                g.DrawLine(color, 1.6f * s, cx - arm, cy - arm, cx + arm, cy + arm);
                g.DrawLine(color, 1.6f * s, cx + arm, cy - arm, cx - arm, cy + arm);
            }
        });
    }

    public Guid? AddDesktop()
    {
        if (!IsOpen || _scene?.Desktops is not { } desktops || _switcher is null) return null;
        BeginDesktopEdit();
        var viewed = desktops.Ids[_scene.Current];
        _desktopChange = new(Guid.Empty, viewed, viewed, true, 0, true, Clock.Now + 1);
        _refreshingBar = true;
        try
        {
            if (_switcher.TryCreateDesktop() is not { } created) { CancelDesktopEdit(); return null; }
            _desktopChange = _desktopChange with { Desktop = created, Deadline = Clock.Now + 1 };
            FramesNeeded?.Invoke();
            return created;
        }
        finally { _refreshingBar = false; }
    }

    public bool RemoveDesktop(Guid desktop)
    {
        if (_scene?.Desktops is not { } desktops) return false;
        for (var i = 0; i < desktops.Count; i++) if (desktops.Ids[i] == desktop) return RemoveDesktop(i);
        return false;
    }

    public void RefreshForDesktopBarDiagnostics()
    {
        if (!IsOpen) return;
        var scene = _capture();
        _realDesktop = scene.Current;
        Rebuild(scene);
    }

    private bool RemoveDesktop(int index)
    {
        if (!IsOpen || _scene?.Desktops is not { } desktops || _switcher is null) return false;
        var fallback = DesktopBarLayout.FallbackIndex(desktops.Count, index);
        if (fallback < 0) return false;
        BeginDesktopEdit();
        var motion = SettleMotion.Create(new SpringPreset(0.20, 1), 0, 0, 1);
        var now = Clock.Now;
        var begin = (long)(now * Stopwatch.Frequency);
        var id = desktops.Ids[index];
        _desktopChange = new(id, desktops.Ids[_scene.Current], desktops.Ids[fallback], false,
            now + motion.Duration, false, now + motion.Duration + 1);
        foreach (var plan in _plans)
        {
            if (plan.Tiles.FirstOrDefault(tile => tile.Id == id) is not { } tile) continue;
            foreach (var part in plan.Parts.Where(part => part.TileId == id))
            {
                var initial = FrameFor(plan, part, 1, 0, 0);
                var frames = motion.Samples.Select(sample => Shrink(initial, tile.Bounds, sample.Time, sample.Value)).ToArray();
                part.Sprite.Animate(frames, begin);
                part.Backing?.Animate(frames, begin);
            }
            var label = new SpriteFrame(0, TileLabelBounds(tile, plan.Surface.Scale), 1, null);
            tile.Decoration!.Animate(motion.Samples.Select(sample => Shrink(label, tile.Bounds, sample.Time, sample.Value)).ToArray(), begin);
        }
        Composition.Flush();
        FramesNeeded?.Invoke();
        return true;
    }

    private static SpriteFrame Shrink(SpriteFrame frame, RectangleF group, double time, double progress) =>
        ScaleFrame(frame, group, time, (float)(1 - progress * 0.28), (float)(-progress * 12), (float)(1 - progress));

    private static SpriteFrame ScaleFrame(SpriteFrame frame, RectangleF group, double time, float scale, float dy, float opacity)
    {
        RectangleF Scale(RectangleF rect) => new(
            group.X + group.Width / 2 + (rect.X - group.X - group.Width / 2) * scale,
            group.Y + group.Height / 2 + (rect.Y - group.Y - group.Height / 2) * scale + dy,
            rect.Width * scale, rect.Height * scale);
        return new(time, Scale(frame.Bounds), frame.Opacity * opacity, frame.Viewport is { } clip ? Scale(clip) : null);
    }

    private void BeginDesktopEdit()
    {
        ExpandBarForEdit();
        RunDeferred(double.PositiveInfinity);
        foreach (var plan in _plans) DisableBarGroup(plan);
        // Consume additional clicks while the bar moves; don't pass them into apps underneath.
        SetInteractive(true);
        SetHover(null, default);
    }

    private void CancelDesktopEdit()
    {
        _desktopChange = null;
        _barTransitionUntil = null;
        _tilePositions.Clear();
        foreach (var plan in _plans) { Render(plan, 1); Paint(plan); }
        DisposeSpareTileDecorations();
        SetInteractive(_state == State.Open);
        if (_state == State.Open) ScheduleSharpen();
    }

    private void ResetDesktopBar()
    {
        _barReveal.Reset();
        _barRevealAnimating = false;
        var drag = _desktopDrag;
        _desktopDrag = null;
        drag?.Plan.Surface.ReleasePointer();
        var windowDrag = _windowDrag;
        _windowDrag = null;
        windowDrag?.Plan.Surface.ReleasePointer();
        _droppedWindow = null;
        _tilePositions.Clear();
        _desktopChange = null;
        _barTransitionUntil = null;
        DisposeSpareTileDecorations();
    }

    private void TickDesktopBar(double now)
    {
        if (_refreshingBar || _state != State.Open) return;
        if (_windowDrag is not null) { TickWindowDrag(); return; }
        if (_desktopDrag is not null) { TickDesktopDrag(now); return; }
        if (_barTransitionUntil is { } finish)
        {
            if (now < finish) return;
            CancelDesktopEdit();
            RefreshHover();
            return;
        }
        if (_desktopChange is not { } change) return;
        if (!change.Requested)
        {
            if (now < change.ReadyAt) return;
            _refreshingBar = true;
            try
            {
                if (_switcher?.TryRemoveDesktop(change.Desktop, change.Fallback) != true) { CancelDesktopEdit(); return; }
                _desktopChange = change with { Requested = true, Deadline = Clock.Now + 1 };
            }
            finally { _refreshingBar = false; }
            return;
        }
        var desktops = VirtualDesktops.Read();
        var confirmed = desktops is { Index: >= 0 } && (change.Window != IntPtr.Zero
            // A moved window: once it has flown into its tile and Explorer reports it on that desktop.
            ? now >= change.ReadyAt && desktops.Ids.Contains(change.Desktop) && _windowDesktops.GetDesktopId(change.Window) == change.Desktop
            : change.Order is { } order ? desktops.Ids.SequenceEqual(order) : desktops.Ids.Contains(change.Desktop) == change.Added);
        if (change.Window != IntPtr.Zero && now < change.ReadyAt) return;
        if (!confirmed && now < change.Deadline) return;
        _refreshingBar = true;
        try
        {
            if (!confirmed) Log.Write("Desktop list change wasn't confirmed in time; refreshing the overview.");
            RefreshDesktopBar(change);
        }
        finally { _refreshingBar = false; }
    }

    private void RefreshDesktopBar(DesktopChange change)
    {
        var captured = _capture();
        if (captured.Desktops is not { } desktops) { CancelDesktopEdit(); return; }
        var snapshotTime = Clock.Now;
        var oldFrames = _plans.SelectMany(plan => plan.Parts.Select(part => (part.Sprite, Frame: DisplayFrame(plan, part, snapshotTime))))
            .ToDictionary(item => item.Sprite, item => item.Frame);
        if (_droppedWindow is { } dropped) // it isn't at its place in the spread any more: it flew into a tile
        {
            oldFrames[dropped.Part.Sprite] = dropped.Frame;
            if (dropped.Part.Backing is { } backing) oldFrames[backing] = dropped.Frame;
            _droppedWindow = null;
        }
        var oldLabels = _plans.SelectMany(plan => plan.Tiles.Select(tile =>
            (Key: (plan.Surface, tile.Id), Bounds: DesktopReorder.Map(TileLabelBounds(tile, plan.Surface.Scale), tile.Bounds, DisplayTile(plan, tile, snapshotTime)))))
            .ToDictionary(item => item.Key, item => item.Bounds);
        _realDesktop = captured.Current;
        var viewed = DesktopBarLayout.ViewedIndex(desktops.Ids, change.Viewed, change.Fallback, captured.Current);
        Build(captured.WithCurrent(viewed));
        _tilePositions.Clear();
        _desktopChange = null;
        var motion = SettleMotion.Create(new SpringPreset(0.24, 0.95), 0, 0, 1);
        var now = Clock.Now;
        _barTransitionUntil = now + motion.Duration;
        var begin = (long)(now * Stopwatch.Frequency);
        foreach (var plan in _plans)
        {
            Render(plan, 1);
            Paint(plan);
            foreach (var part in plan.Parts)
            {
                var target = FrameFor(plan, part, 1, 0, 0);
                var from = oldFrames.TryGetValue(part.Sprite, out var previous) ? previous : target with { Opacity = 0 };
                if (change.Added && part.TileId == change.Desktop && plan.Tiles.FirstOrDefault(tile => tile.Id == change.Desktop) is { } added)
                    from = ScaleFrame(target, added.Bounds, 0, 0.76f, 16 * plan.Surface.Scale, 0);
                if (from == target) continue;
                var frames = motion.Samples.Select(sample => InterpolateFrame(from, target, sample.Time, sample.Value)).ToArray();
                part.Sprite.Animate(frames, begin);
                part.Backing?.Animate(frames, begin);
            }
            foreach (var tile in plan.Tiles)
            {
                var target = new SpriteFrame(0, TileLabelBounds(tile, plan.Surface.Scale), 1, null);
                var from = oldLabels.TryGetValue((plan.Surface, tile.Id), out var old) ? target with { Bounds = old }
                    : ScaleFrame(target, tile.Bounds, 0, 0.76f, 16 * plan.Surface.Scale, 0);
                tile.Decoration!.Animate(motion.Samples.Select(sample => InterpolateFrame(from, target, sample.Time, sample.Value)).ToArray(), begin);
            }
        }
        foreach (var decoration in _spareTileDecorations.Values) decoration.Hide();
        RemoveStale();
        Composition.Flush();
        Log.Verbose($"Desktop bar: {(change.Window != IntPtr.Zero ? "window moved to" : change.Order is not null ? "reordered" : change.Added ? "added" : "removed")} {change.Desktop}, {desktops.Count} desktops, viewed {viewed + 1}; compositor animation.");
    }

    private static SpriteFrame InterpolateFrame(SpriteFrame from, SpriteFrame to, double time, double progress)
    {
        RectangleF Lerp(RectangleF a, RectangleF b) => new(
            (float)(a.X + (b.X - a.X) * progress), (float)(a.Y + (b.Y - a.Y) * progress),
            (float)(a.Width + (b.Width - a.Width) * progress), (float)(a.Height + (b.Height - a.Height) * progress));
        return new(time, Lerp(from.Bounds, to.Bounds), (float)(from.Opacity + (to.Opacity - from.Opacity) * progress),
            from.Viewport is { } a && to.Viewport is { } b ? Lerp(a, b) : to.Viewport);
    }

    private void RefreshHover()
    {
        if (_windowDrag is not null || !Interop.NativeMethods.GetCursorPos(out var cursor)) return;
        foreach (var plan in _plans)
            if (plan.Surface.Monitor.Contains(cursor.X, cursor.Y))
                SetHover(plan, HitTest(plan, new Point(cursor.X - plan.Surface.Monitor.X, cursor.Y - plan.Surface.Monitor.Y)));
    }
}
