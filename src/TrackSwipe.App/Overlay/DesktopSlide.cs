namespace TrackSwipe.Overlay;

/// <summary>
/// The sideways desktop switch: the current desktop and its neighbours side by side, each
/// layer at (layer - progress) screen widths. The taskbar stays put, as it does natively.
/// </summary>
internal sealed class DesktopSlide(Swatches swatches)
{
    private const int Fixed = int.MaxValue;

    private readonly List<(Sprite Sprite, Sprite? Backing, int Layer, int Width)> _parts = new();

    public void Begin(DesktopScene scene, IReadOnlyList<ThumbnailSurface> surfaces)
    {
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
    }

    public void Render(double progress)
    {
        foreach (var (sprite, backing, layer, width) in _parts)
        {
            var destination = sprite.Home;
            if (layer != Fixed) destination.Offset((int)Math.Round((layer - progress) * width), 0);
            backing?.Place(destination);
            sprite.Place(destination);
        }
    }

    private void Add(ThumbnailSurface surface, SceneItem item, int layer)
    {
        if (!item.Bounds.IntersectsWith(surface.Monitor)) return;
        // The backing goes in first so it sits underneath.
        var backing = item.IsAppWindow ? surface.Add(swatches.BackingFor(item)) : null;
        if (surface.Add(item) is { } sprite) _parts.Add((sprite, backing, layer, surface.Monitor.Width));
        else if (backing is not null) surface.Remove(backing);
    }
}
