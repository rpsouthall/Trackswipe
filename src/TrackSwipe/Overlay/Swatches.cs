using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>GPU-resident solid backings and highlight rings shared by every overlay.</summary>
internal sealed class Swatches : IDisposable
{
    private readonly GpuSwatch _dark = GpuSwatch.Solid(Color.FromArgb(32, 32, 32));
    private readonly GpuSwatch _light = GpuSwatch.Solid(Color.FromArgb(243, 243, 243));
    private readonly GpuSwatch _black = GpuSwatch.Solid(Color.Black);
    private readonly GpuSwatch _overview = GpuSwatch.Solid(OverviewGrey);
    private readonly Dictionary<(Size, Color, bool), GpuSwatch> _rings = new();
    private readonly Dictionary<IntPtr, GpuSwatch> _sources;

    /// <summary>The plain dark grey behind the desktops' cards in the GNOME-style overview.</summary>
    public static readonly Color OverviewGrey = Color.FromArgb(46, 46, 50);

    public Swatches() => _sources = new()
    {
        [_dark.Item.Window] = _dark, [_light.Item.Window] = _light, [_black.Item.Window] = _black, [_overview.Item.Window] = _overview,
    };

    /// <summary>A fill of <see cref="OverviewGrey"/>, stretched over the screen behind the cards.</summary>
    public SceneItem OverviewBackdrop => _overview.Item;

    public IEnumerable<IntPtr> Handles => Array.Empty<IntPtr>();
    public SceneItem Base => _black.Item;
    public SceneItem BackingFor(SceneItem window) => (window.IsDark ? _dark : _light).Item;
    public bool TryGet(SceneItem item, out GpuSwatch swatch) => _sources.TryGetValue(item.Window, out swatch!);

    /// <param name="onTop">
    /// Drawn on the tile itself, a border over its edge (the card layout's previews), rather than a frame standing
    /// off around it.
    /// </param>
    public (SceneItem Item, int Overhang) Ring(Size tile, float scale, Color accent, bool onTop = false)
    {
        var overhang = onTop ? 0 : (int)Math.Ceiling(5 * scale);
        var size = new Size(tile.Width + 2 * overhang, tile.Height + 2 * overhang);
        if (!_rings.TryGetValue((size, accent, onTop), out var ring))
        {
            var width = 3 * scale;
            // On the tile: flush with its edge, corners barely rounded so the tile's own square corners don't show past them.
            var inset = onTop ? width / 2 : width / 2 + 1;
            var radius = onTop ? 2 * scale : 9 * scale;
            ring = new GpuSwatch(size, context =>
            {
                using var canvas = new GpuCanvas(context);
                canvas.DrawRounded(accent, width, new RectangleF(inset, inset, size.Width - 2 * inset, size.Height - 2 * inset), radius);
            });
            _rings[(size, accent, onTop)] = ring;
            _sources[ring.Item.Window] = ring;
        }
        return (ring.Item, overhang);
    }

    private readonly Dictionary<(int Radius, int Corner), GpuSwatch> _corners = new();

    /// <summary>
    /// A square of the overview's grey backdrop with a quarter circle cut out of it: laid over a corner of a desktop's card,
    /// it rounds that corner, whatever is drawn in the card. <paramref name="corner"/> is 0 top-left, 1 top-right,
    /// 2 bottom-left, 3 bottom-right.
    /// </summary>
    public SceneItem CornerMask(int radius, int corner)
    {
        radius = Math.Max(1, radius);
        if (!_corners.TryGetValue((radius, corner), out var mask))
        {
            // The circle's centre is the square's corner furthest into the card.
            float cx = corner is 0 or 2 ? radius : 0, cy = corner is 0 or 1 ? radius : 0;
            mask = new GpuSwatch(new Size(radius, radius), context =>
            {
                using var canvas = new GpuCanvas(context);
                // A ring from the circle outwards, wide enough to cover the rest of the square.
                canvas.DrawRounded(OverviewGrey, radius, new RectangleF(cx - 1.5f * radius, cy - 1.5f * radius, 3f * radius, 3f * radius), 1.5f * radius);
            });
            _corners[(radius, corner)] = mask;
            _sources[mask.Item.Window] = mask;
        }
        return mask.Item;
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values) source.Dispose();
        _sources.Clear(); _rings.Clear(); _corners.Clear();
    }
}
