using System.Diagnostics;
using System.Numerics;
using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

internal sealed partial class MissionControl
{
    private readonly DesktopBarReveal _barReveal = new();
    private bool _barRevealAnimating;
    private double BarExpansion => _barReveal.Value(Clock.Now);

    private static Rectangle CompactLabelBounds(Plan plan, Tile tile) => plan.CompactBounds[tile.Desktop];

    private void CreateBarLabels(Plan plan)
    {
        if (plan.Tiles.Count == 0) return;
        var s = plan.Surface.Scale;
        var row = new Rectangle(0, plan.Tiles[0].Bounds.Y, plan.Surface.WindowBounds.Width, (int)Math.Ceiling(32 * s));
        var measured = plan.Tiles.Select(tile => Composition.Gpu.Drawing.Measure(tile.Label, 13 * s, false)).ToArray();
        var labels = DesktopBarSelection.Arrange(row.Width, row.Y, (int)Math.Ceiling(28 * s), measured.Select(size => size.Width).ToArray(), s);
        for (var i = 0; i < plan.Tiles.Count; i++)
        {
            var tile = plan.Tiles[i];
            var bounds = labels[i];
            plan.CompactBounds[tile.Desktop] = bounds;
            var pill = new GpuDecorationLayer(plan.Surface, bounds);
            pill.Paint(g => g.FillRounded(Color.FromArgb(48, Color.White), bounds, bounds.Height / 2f));
            plan.CompactPills[tile.Desktop] = pill;
        }
        plan.CompactLabels = new GpuDecorationLayer(plan.Surface, row);
        plan.CompactLabels.Paint(g =>
        {
            foreach (var tile in plan.Tiles)
            {
                var label = CompactLabelBounds(plan, tile);
                var size = g.MeasureText(tile.Label, 13 * s, false);
                var text = new RectangleF(label.X + 8 * s, label.Y + (label.Height - size.Height) / 2, label.Width - 16 * s, size.Height);
                g.DrawText(tile.Label, 13 * s, false, Color.White, text);
            }
        });
        if (plan.AddTile is { } add)
        {
            plan.AddControl = new GpuDecorationLayer(plan.Surface, Rectangle.Inflate(add, (int)Math.Ceiling(2 * s), (int)Math.Ceiling(2 * s)));
            PaintAddControl(plan);
        }
        if (IsVisible)
        {
            plan.CompactLabels.Show(); plan.AddControl?.Show();
            foreach (var pill in plan.CompactPills.Values) pill.Show();
        }
    }

    private void PaintAddControl(Plan plan)
    {
        if (plan.AddControl is null || plan.AddTile is not { } add) return;
        var s = plan.Surface.Scale;
        var hovered = plan == _hoverPlan && _hover.Kind == HitKind.AddDesktop;
        plan.AddControl.Paint(g =>
        {
            if (hovered) g.FillRounded(Color.FromArgb(60, Color.White), add, 8 * s);
            var cx = add.X + add.Width / 2f; var cy = add.Y + add.Height / 2f; var arm = 8 * s;
            g.DrawLine(Color.White, 2 * s, cx - arm, cy, cx + arm, cy);
            g.DrawLine(Color.White, 2 * s, cx, cy - arm, cx, cy + arm);
        });
    }

    private static SpriteFrame AddControlFrame(Plan plan, double openness, double expansion, double time)
    {
        RectangleF bounds = plan.AddControl!.Bounds;
        var p = OverviewPresentation.Layout(openness) * expansion;
        bounds.Offset(0, (float)(-BarLift(plan) * (1 - p)));
        return new(time, bounds, (float)p, null);
    }

    private static int BarLift(Plan plan) => plan.Tiles.Count == 0 ? 0 :
        plan.Tiles[0].Bounds.Bottom + (int)(20 * plan.Surface.Scale);

    private void RenderBarLabels(Plan plan, double openness, double expansion)
    {
        var alpha = (float)Math.Clamp(openness * 2 - 1, 0, 1);
        plan.CompactLabels?.SetAlpha(alpha * (float)(1 - expansion) * 255);
        foreach (var (desktop, pill) in plan.CompactPills)
            pill.SetAlpha(CompactPillOpacity(plan, desktop, openness, expansion, _pan) * 255);
        if (plan.AddControl is not null)
        {
            var frame = AddControlFrame(plan, openness, expansion, 0);
            plan.AddControl.Place(frame.Bounds, frame.Opacity);
        }
    }

    private void UpdateBarHover(Plan plan, Point point)
    {
        var primary = _plans.FirstOrDefault(p => p.Tiles.Count > 0);
        if (primary is null || DesktopBarBusy) return;
        if (_barReveal.Expanded) return; // once revealed, previews stay open for this Mission Control session
        var compactBottom = primary.Tiles[0].Bounds.Y + (int)Math.Ceiling(36 * primary.Surface.Scale);
        var expandedBottom = primary.Tiles[0].Bounds.Bottom + (int)Math.Ceiling(48 * primary.Surface.Scale);
        var expanded = plan == primary && DesktopBarReveal.Hovering(point.Y, compactBottom, expandedBottom, _barReveal.Expanded);
        SetBarExpanded(expanded);
    }

    private void SetBarExpanded(bool expanded)
    {
        if (expanded != _barReveal.Expanded && DeferUntilAwake(() => SetBarExpanded(expanded))) return;
        var now = Clock.Now;
        if (_barReveal.Set(expanded, now) is not { } motion) return;
        _barRevealAnimating = true;
        SetHover(null, default);
        var begin = (long)(now * Stopwatch.Frequency);
        foreach (var plan in _plans)
        {
            if (plan.Tiles.Count == 0) continue;
            EnableBarGroup(plan);
            var frames = motion.Samples.Select(sample => new SpriteFrame(sample.Time,
                new RectangleF(0, (float)(-BarLift(plan) * (1 - Math.Clamp(sample.Value, 0, 1))), 1, 1),
                (float)Math.Clamp(sample.Value, 0, 1), null)).ToArray();
            using var transform = Composition.Device.CreateMatrixTransform();
            var identity = Matrix3x2.Identity;
            transform.SetMatrix(ref identity).CheckError();
            using var y = CompositorMotion.Curve(frames, frame => frame.Bounds.Y, begin);
            transform.SetMatrixElement(2, 1, y).CheckError();
            plan.BarGroup.SetTransform(transform);
            using var alpha = CompositorMotion.Curve(frames, frame => frame.Opacity, begin);
            plan.BarGroup.AnimateOpacity(alpha);
            plan.BarOffset = plan.BarOpacity = float.NaN; // detach these curves when tracking resumes
            foreach (var part in plan.Parts.Where(part => part.Slot == "ring"))
                part.Sprite.Animate(motion.Samples.Select(sample => FrameFor(plan, part, _openness, _pan, sample.Time,
                    Math.Clamp(sample.Value, 0, 1))).ToArray(), begin);
            foreach (var part in plan.Parts.Where(part => part.CompactTo is not null))
            {
                var windowFrames = motion.Samples.Select(sample => FrameFor(plan, part, _openness, _pan, sample.Time,
                    Math.Clamp(sample.Value, 0, 1))).ToArray();
                part.Sprite.Animate(windowFrames, begin);
                part.Backing?.Animate(windowFrames, begin);
            }
            foreach (var tile in plan.Tiles)
                tile.Decoration!.Animate(motion.Samples.Select(sample => TileLabelFrame(plan, tile, _openness, sample.Time,
                    Math.Clamp(sample.Value, 0, 1))).ToArray(), begin);
            plan.CompactLabels?.AnimateOpacity(motion.Samples.Select(sample => new SpriteFrame(sample.Time,
                RectangleF.Empty, (float)(1 - Math.Clamp(sample.Value, 0, 1)), null)).ToArray(), begin);
            foreach (var (desktop, pill) in plan.CompactPills)
                pill.AnimateOpacity(motion.Samples.Select(sample => new SpriteFrame(sample.Time, RectangleF.Empty,
                    CompactPillOpacity(plan, desktop, _openness, Math.Clamp(sample.Value, 0, 1), _pan), null)).ToArray(), begin);
            plan.AddControl?.Animate(motion.Samples.Select(sample => AddControlFrame(plan, _openness,
                Math.Clamp(sample.Value, 0, 1), sample.Time)).ToArray(), begin);
        }
        Composition.Flush();
        FramesNeeded?.Invoke();
    }

    private void StopBarReveal()
    {
        if (!_barRevealAnimating) return;
        _barReveal.Reset(BarExpansion);
        _barRevealAnimating = false;
        foreach (var plan in _plans) Render(plan, _openness);
    }

    private RectangleF OverviewWindowBounds(Plan plan, Part part) => FrameFor(plan, part, 1, _pan, 0).Bounds;

    private float CompactPillOpacity(Plan plan, int desktop, double openness, double expansion, double pan) =>
        (float)(Math.Clamp(openness * 2 - 1, 0, 1) * (1 - expansion)) *
        DesktopBarSelection.Weight(_scene?.Current ?? 0, desktop, pan, plan.Tiles.Count);

    private void ExpandBarForEdit()
    {
        _barReveal.Reset(1);
        _barRevealAnimating = false;
        foreach (var plan in _plans) Render(plan, _openness);
    }

    private void TickBarReveal(double now)
    {
        if (!_barRevealAnimating || _barReveal.Animating(Clock.Now)) return;
        _barRevealAnimating = false;
        foreach (var plan in _plans) Render(plan, _openness);
        RefreshHover();
        ScheduleSharpen();
    }

    public void HoverDesktopBarForDiagnostics(bool expanded)
    {
        if (_plans.FirstOrDefault(plan => plan.Tiles.Count > 0) is not { } primary) return;
        OnMouseMoved(primary.Surface, new Point(primary.Tiles[0].Bounds.X + 10,
            expanded ? primary.Tiles[0].Bounds.Y + 8 : primary.Tiles[0].Bounds.Bottom + (int)(80 * primary.Surface.Scale)));
    }

    public void HoverWindowForDiagnostics(int index)
    {
        if (_plans.FirstOrDefault(plan => plan.Windows.Count > 0) is not { } primary) return;
        if (index < 0) SetHover(null, default);
        else
        {
            var candidates = primary.Windows.Where(part =>
                new RectangleF(Point.Empty, primary.Surface.WindowBounds.Size).Contains(OverviewWindowBounds(primary, part))).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("No visible window to verify highlight.");
            var window = candidates[index % candidates.Length];
            SetHover(primary, new Hit(HitKind.Window, window));
            var retained = primary.Decoration.RetainedRegion;
            if (retained.IsEmpty || !retained.Contains(Rectangle.Round(OverviewWindowBounds(primary, window))))
                throw new InvalidOperationException("Sparse highlight does not cover the window.");
            Log.Verbose($"Sparse highlight: retained {retained.Width}x{retained.Height} of {primary.Decoration.Bounds.Width}x{primary.Decoration.Bounds.Height} pixels.");
        }
        if (index < 0 && _plans.Any(plan => !plan.Decoration.RetainedRegion.IsEmpty))
            throw new InvalidOperationException("Highlight backing remained after clearing hover.");
        Composition.Flush();
    }
}
