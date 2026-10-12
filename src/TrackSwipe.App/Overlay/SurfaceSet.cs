namespace TrackSwipe.Overlay;

/// <summary>
/// One thumbnail surface per monitor, recreated whenever the monitor layout changes. The slide and
/// Mission Control each own a set, so each can keep its thumbnails registered between gestures (and be
/// prepared, hidden, as fingers land) without disturbing the other.
/// </summary>
internal sealed class SurfaceSet : IDisposable
{
    private readonly List<ThumbnailSurface> _surfaces = new();
    private readonly Swatches _swatches;
    private (Rectangle Bounds, Rectangle WorkArea)[] _layout = Array.Empty<(Rectangle, Rectangle)>();

    /// <param name="baseItem">What fills each surface beneath its thumbnails.</param>
    public SurfaceSet(Swatches swatches) => _swatches = swatches;

    /// <summary>The surfaces, created or recreated to match the current monitors.</summary>
    public IReadOnlyList<ThumbnailSurface> Prepare()
    {
        var screens = Screen.AllScreens;
        var layout = screens.Select(screen => (screen.Bounds, screen.WorkingArea)).ToArray();
        if (_surfaces.Count == 0 || !layout.SequenceEqual(_layout))
        {
            foreach (var surface in _surfaces) surface.Dispose();
            _surfaces.Clear();
            _surfaces.AddRange(screens.Select(screen => new ThumbnailSurface(screen, _swatches.Base, _swatches)));
            _layout = layout;
        }
        return _surfaces;
    }

    public IEnumerable<IntPtr> Handles => _surfaces.Select(surface => surface.Handle);

    public void Hide()
    {
        foreach (var surface in _surfaces) surface.Hide();
    }

    public void Dispose()
    {
        foreach (var surface in _surfaces) surface.Dispose();
        _surfaces.Clear();
    }
}
