using TrackSwipe.Core;

namespace TrackSwipe.Overlay;

/// <summary>
/// The sideways desktop switch: the current desktop and its neighbours side by side, each
/// layer at (layer - progress) screen widths. The taskbar stays put, as it does natively.
/// </summary>
internal sealed class DesktopSlide(Swatches swatches)
{
    private const int Fixed = int.MaxValue;

    private readonly List<(Sprite Sprite, Sprite? Backing, int Layer, int Width)> _parts = new();
    private DesktopScene? _scene;
    private (ThumbnailSurface Surface, int Generation)[] _surfaces = Array.Empty<(ThumbnailSurface, int)>();

    public void Begin(DesktopScene scene, IReadOnlyList<ThumbnailSurface> surfaces)
    {
        // A repeated swipe on an unchanged desktop can reuse all registrations and their z-order.
        // Any moved/closed/reordered window, desktop change, monitor change or shared-surface clear
        // invalidates the entire cache, so stale thumbnails never get shown.
        if (_scene is { } previous && previous.Current == scene.Current && previous.Items.SequenceEqual(scene.Items)
            && (previous.Desktops?.Ids ?? Array.Empty<Guid>()).SequenceEqual(scene.Desktops?.Ids ?? Array.Empty<Guid>())
            && _surfaces.Length == surfaces.Count
            && _surfaces.Select(pair => pair.Surface).SequenceEqual(surfaces)
            && _surfaces.All(pair => pair.Generation == pair.Surface.Generation)) return;

        foreach (var surface in surfaces) surface.Clear();
        _parts.Clear();
        foreach (var surface in surfaces)
        {
            for (var layer = -1; layer <= 1; layer++)
            {
                foreach (var wallpaper in scene.Wallpapers) Add(surface, wallpaper, layer);
            }
            foreach (var window in scene.Items.Where(item => item.Kind == SceneKind.Window && !item.IsMinimized && Math.Abs(item.Desktop - scene.Current) <= 1))
                Add(surface, window, window.Desktop - scene.Current);
            foreach (var taskbar in scene.Taskbars) Add(surface, taskbar, Fixed);
        }
        _scene = scene;
        _surfaces = surfaces.Select(surface => (surface, surface.Generation)).ToArray();
    }

    public void Render(double progress)
    {
        foreach (var (sprite, backing, layer, width) in _parts)
        {
            var destination = sprite.Home;
            Rectangle? clip = null;
            if (layer != Fixed)
            {
                var offset = (int)Math.Round((layer - progress) * width);
                destination.Offset(offset, 0);
                clip = new Rectangle(offset, 0, width, sprite.Surface.Monitor.Height);
            }
            backing?.Place(destination, clip: clip);
            sprite.Place(destination, clip: clip);
        }
    }

    private void Add(ThumbnailSurface surface, SceneItem item, int layer)
    {
        if (!item.Bounds.IntersectsWith(surface.Monitor)) return;
        // The backing goes in first so it sits underneath.
        var backing = item.NeedsBacking ? surface.Add(swatches.BackingFor(item)) : null;
        if (surface.Add(item) is { } sprite) _parts.Add((sprite, backing, layer, surface.Monitor.Width));
        else if (backing is not null) surface.Remove(backing);
    }

    public void Settle(SettleMotion motion, long begin)
    {
        foreach (var (sprite, backing, layer, width) in _parts)
        {
            var frames = motion.Samples.Select(sample =>
            {
                var rect = (RectangleF)sprite.Home;
                RectangleF? viewport = null;
                if (layer != Fixed)
                {
                    var offset = (float)((layer - sample.Value) * width);
                    rect.Offset(offset, 0);
                    viewport = new RectangleF(offset, 0, width, sprite.Surface.Monitor.Height);
                }
                return new SpriteFrame(sample.Time, rect, 1, viewport);
            }).ToArray();
            sprite.Animate(frames, begin);
            backing?.Animate(frames, begin);
        }
        Interop.Composition.Flush();
    }
}
