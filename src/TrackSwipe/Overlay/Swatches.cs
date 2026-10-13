using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>GPU-resident solid backings and highlight rings shared by every overlay.</summary>
internal sealed class Swatches : IDisposable
{
    private readonly GpuSwatch _dark = GpuSwatch.Solid(Color.FromArgb(32, 32, 32));
    private readonly GpuSwatch _light = GpuSwatch.Solid(Color.FromArgb(243, 243, 243));
    private readonly GpuSwatch _black = GpuSwatch.Solid(Color.Black);
    private readonly Dictionary<(Size, Color), GpuSwatch> _rings = new();
    private readonly Dictionary<IntPtr, GpuSwatch> _sources;

    public Swatches() => _sources = new()
    {
        [_dark.Item.Window] = _dark, [_light.Item.Window] = _light, [_black.Item.Window] = _black,
    };

    public IEnumerable<IntPtr> Handles => Array.Empty<IntPtr>();
    public SceneItem Base => _black.Item;
    public SceneItem BackingFor(SceneItem window) => (window.IsDark ? _dark : _light).Item;
    public bool TryGet(SceneItem item, out GpuSwatch swatch) => _sources.TryGetValue(item.Window, out swatch!);

    public (SceneItem Item, int Overhang) Ring(Size tile, float scale, Color accent)
    {
        var overhang = (int)Math.Ceiling(5 * scale);
        var size = new Size(tile.Width + 2 * overhang, tile.Height + 2 * overhang);
        if (!_rings.TryGetValue((size, accent), out var ring))
        {
            var width = 3 * scale;
            var inset = width / 2 + 1;
            ring = new GpuSwatch(size, context =>
            {
                using var canvas = new GpuCanvas(context);
                canvas.DrawRounded(accent, width, new RectangleF(inset, inset, size.Width - 2 * inset, size.Height - 2 * inset), 9 * scale);
            });
            _rings[(size, accent)] = ring;
            _sources[ring.Item.Window] = ring;
        }
        return (ring.Item, overhang);
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values) source.Dispose();
        _sources.Clear(); _rings.Clear();
    }
}
