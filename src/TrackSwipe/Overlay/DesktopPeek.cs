using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>
/// Show desktop, following the fingers: swiping down minimizes every window on the current desktop the way Windows
/// does, each shrinking into its own taskbar button and fading as it arrives, but tracked and reversible until release.
/// </summary>
/// <remarks>
/// Only app windows (the ones Alt+Tab lists, which Windows minimizes) move; other windows stay put. They are drawn
/// above the taskbar so they visibly land in their buttons. At progress 0 the overlay matches the desktop exactly.
/// Where each button is comes from <see cref="TaskbarButtons"/>, looked up in the background while the fingers land;
/// a window whose button isn't found (or isn't known yet) heads for the middle of the taskbar.
/// </remarks>
internal sealed class DesktopPeek
{
    private const double FadeFrom = 0.55;
    private readonly Swatches swatches;

    public DesktopPeek(Swatches swatches) => this.swatches = swatches;

    private sealed class Part(Sprite sprite, Sprite? backing, ThumbnailSurface surface, IntPtr window)
    {
        public Sprite Sprite { get; } = sprite;
        public Sprite? Backing { get; } = backing;
        public ThumbnailSurface Surface { get; } = surface;
        public IntPtr Window { get; } = window;
        public RectangleF Home { get; } = sprite.Home;
        /// <summary>Where it ends, in its monitor's coordinates: its taskbar button. Chosen at the gesture's first frame.</summary>
        public RectangleF? End { get; set; }
        /// <summary>The end is a stand-in (the middle of the taskbar) until the button lookup finishes.</summary>
        public bool Provisional { get; set; }
        /// <summary>The stand-in it was heading for before retargeting, and the progress it retargeted at.</summary>
        public (RectangleF End, double At)? Retargeted { get; set; }
    }

    private readonly List<Part> _moving = new();
    private readonly List<Sprite> _registered = new();
    private DesktopScene? _scene;
    private (ThumbnailSurface Surface, int Generation)[] _surfaces = Array.Empty<(ThumbnailSurface, int)>();
    private double _shown = double.NaN;
    /// <summary>A window whose button turns up mid-gesture bends towards it over this much progress, rather than jumping.</summary>
    private const double RetargetOver = 0.25;
    /// <summary>Past this the window is nearly there: a late button isn't worth swerving for.</summary>
    private const double RetargetBefore = 0.5;

    /// <summary>Whether there's anything to minimize: with no windows on screen there's nothing to show.</summary>
    public bool HasWindows => _moving.Count > 0;

    /// <summary>The windows that leave the screen, which Windows should minimize.</summary>
    public IReadOnlyCollection<IntPtr> Windows => _moving.Select(part => part.Window).Distinct().ToArray();

    public void Begin(DesktopScene scene, IReadOnlyList<ThumbnailSurface> surfaces)
    {
        _shown = double.NaN;
        foreach (var part in _moving) { part.End = null; part.Provisional = false; part.Retargeted = null; } // each gesture picks up where the buttons are now
        // A repeated swipe on an unchanged desktop reuses everything already registered and placed.
        if (_scene is { } previous && previous.Current == scene.Current && previous.Items.SequenceEqual(scene.Items)
            && _surfaces.Length == surfaces.Count && _surfaces.Select(pair => pair.Surface).SequenceEqual(surfaces)
            && _surfaces.All(pair => pair.Generation == pair.Surface.Generation))
        {
            LookUpButtons(scene);
            return;
        }
        foreach (var sprite in _registered) sprite.Surface.Remove(sprite);
        _registered.Clear();
        _moving.Clear();
        foreach (var surface in surfaces)
        {
            // Bottom to top: wallpaper, taskbar, then the windows in their z-order. The windows are drawn over the
            // taskbar so they visibly shrink into their buttons (a window overlapping the taskbar is rare).
            var order = new List<Sprite>();
            foreach (var wallpaper in scene.Wallpapers) AddStill(surface, wallpaper, order);
            foreach (var taskbar in scene.Taskbars) AddStill(surface, taskbar, order);
            foreach (var window in scene.WindowsOn(scene.Current).Where(item => !item.IsMinimized && DesktopScene.IsShown(item.Window) != false))
            {
                if (window.IsAppWindow) AddMoving(surface, window, order);
                else AddStill(surface, window, order);
            }
            surface.Restack(order);
        }
        _scene = scene;
        _surfaces = surfaces.Select(surface => (surface, surface.Generation)).ToArray();
        LookUpButtons(scene);

        void AddStill(ThumbnailSurface surface, SceneItem item, List<Sprite> order)
        {
            if (!item.Bounds.IntersectsWith(surface.Monitor) || surface.Add(item) is not { } sprite) return;
            _registered.Add(sprite);
            order.Add(sprite);
            sprite.Place(sprite.Home);
        }

        void AddMoving(ThumbnailSurface surface, SceneItem item, List<Sprite> order)
        {
            if (!item.Bounds.IntersectsWith(surface.Monitor)) return;
            var backing = item.NeedsBacking ? surface.Add(swatches.BackingFor(item)) : null;
            if (surface.Add(item) is not { } sprite)
            {
                if (backing is not null) surface.Remove(backing);
                return;
            }
            if (backing is not null) { _registered.Add(backing); order.Add(backing); }
            _registered.Add(sprite);
            order.Add(sprite);
            _moving.Add(new Part(sprite, backing, surface, item.Window));
        }
    }

    /// <summary>Brings the shared button lookup up to date if the scene's app windows changed (it's cached otherwise).</summary>
    private void LookUpButtons(DesktopScene scene)
    {
        if (_moving.Count == 0) return;
        TaskbarButtons.Refresh(scene.Items.Where(item => item.Kind == SceneKind.Window && item.IsAppWindow).Select(item => item.Window));
    }

    /// <param name="progress">0 is the desktop as it is; 1 is every window in its button. Beyond 1 (overscroll) they stay there.</param>
    public void Render(double progress)
    {
        progress = Math.Max(0, progress);
        if (progress == _shown) return;
        var arrived = !TaskbarButtons.Looking;
        if (Log.VerboseEnabled && _moving.Count > 0 && _moving[0].End is null)
        {
            var known = TaskbarButtons.Latest;
            var found = known is null ? 0 : _moving.Count(part => known.ContainsKey(part.Window));
            Log.Verbose($"Show desktop: {_moving.Count} windows, {found} heading for their own taskbar button{(arrived ? "" : " (button lookup still running)")}.");
        }
        if (Log.VerboseEnabled && _moving.Any(part => part.Provisional) && arrived && progress < RetargetBefore)
        {
            Log.Verbose($"Show desktop: button lookup arrived at progress {progress:0.00}, retargeting.");
        }
        _shown = progress;
        foreach (var part in _moving)
        {
            // The lookup finished after the first frame: head for the real button while that's still a small nudge.
            if (part.Provisional && arrived && progress < RetargetBefore && part.End is { } standIn)
            {
                part.End = null;
                part.Retargeted = (standIn, progress);
            }
            var (bounds, opacity) = FrameAt(part, progress);
            part.Backing?.Place(bounds, opacity, null);
            part.Sprite.Place(bounds, opacity, null);
        }
    }

    /// <summary>Hands the release to the compositor: each window follows the spring on its own curve.</summary>
    public void Settle(SettleMotion motion, long begin, Func<double, double> progress)
    {
        foreach (var part in _moving)
        {
            var frames = motion.Samples.Select(sample =>
            {
                var (bounds, opacity) = FrameAt(part, Math.Max(0, progress(sample.Value)));
                return new SpriteFrame(sample.Time, bounds, opacity / 255f, null);
            }).ToArray();
            part.Sprite.Animate(frames, begin);
            part.Backing?.Animate(frames, begin);
        }
        _shown = double.NaN; // the next Render replaces the curves
        Composition.Flush();
    }

    /// <summary>
    /// As Windows minimizes: the window's centre travels to its button while it shrinks (evenly to the eye, so
    /// exponentially) to icon size, fading over the last part of the way.
    /// </summary>
    private (RectangleF Bounds, byte Opacity) FrameAt(Part part, double progress)
    {
        var end = part.End ??= EndFor(part);
        var t = (float)Math.Min(1, progress);
        if (part.Retargeted is { } from)
        {
            // From the stand-in to the button, eased in from where the swap happened, so the path stays continuous.
            var blend = (float)Math.Clamp((progress - from.At) / RetargetOver, 0, 1);
            blend = blend * blend * (3 - 2 * blend);
            end = new RectangleF(from.End.X + (end.X - from.End.X) * blend, from.End.Y + (end.Y - from.End.Y) * blend,
                from.End.Width + (end.Width - from.End.Width) * blend, from.End.Height + (end.Height - from.End.Height) * blend);
        }
        var home = part.Home;
        var endScale = Math.Min(end.Width / home.Width, end.Height / home.Height);
        var scale = MathF.Pow(endScale, t);
        float homeX = home.X + home.Width / 2, homeY = home.Y + home.Height / 2;
        float endX = end.X + end.Width / 2, endY = end.Y + end.Height / 2;
        float x = homeX + (endX - homeX) * t, y = homeY + (endY - homeY) * t;
        float width = home.Width * scale, height = home.Height * scale;
        var opacity = t <= FadeFrom ? 1 : 1 - (t - FadeFrom) / (1 - FadeFrom);
        return (new RectangleF(x - width / 2, y - height / 2, width, height), (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)));
    }

    /// <summary>The window's taskbar button (icon-sized, in its monitor's coordinates), or the middle of the taskbar.</summary>
    private RectangleF EndFor(Part part)
    {
        var monitor = part.Surface.Monitor;
        var icon = Math.Max(24, (monitor.Bottom - part.Surface.WorkArea.Bottom) * 0.5f);
        // Still heading for a stand-in if the lookup that may find this window's button hasn't finished yet.
        part.Provisional = TaskbarButtons.Looking;
        if (TaskbarButtons.Latest is { } known && known.TryGetValue(part.Window, out var button))
        {
            float x = button.X - monitor.X + button.Width / 2f, y = button.Y - monitor.Y + button.Height / 2f;
            var size = Math.Min(button.Width, button.Height) * 0.6f;
            return new RectangleF(x - size / 2, y - size / 2, size, size);
        }
        float cx = monitor.Width / 2f, cy = (part.Surface.WorkArea.Bottom - monitor.Y + monitor.Height) / 2f;
        return new RectangleF(cx - icon / 2, cy - icon / 2, icon, icon);
    }
}
