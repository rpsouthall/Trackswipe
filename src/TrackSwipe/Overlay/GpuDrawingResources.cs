using TrackSwipe.Interop;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Color = System.Drawing.Color;

namespace TrackSwipe.Overlay;

/// <summary>Bounded drawing resources shared by surfaces on the same Direct2D device.</summary>
internal sealed class GpuDrawingResources : IDisposable
{
    private readonly ID2D1DeviceContext _context;
    private readonly IDWriteFactory _text;
    private readonly Dictionary<Color, ID2D1SolidColorBrush> _brushes = new();
    private readonly Dictionary<(float Size, bool Bold), (IDWriteTextFormat Format, IDWriteInlineObject Sign)> _fonts = new();
    private readonly Dictionary<(string Text, float Size, bool Bold), SizeF> _measurements = new();

    public GpuDrawingResources(GpuDevice gpu)
    {
        _context = gpu.DrawingDevice.CreateDeviceContext(DeviceContextOptions.None);
        _text = gpu.Text;
    }

    public ID2D1SolidColorBrush Brush(Color color)
    {
        if (_brushes.TryGetValue(color, out var brush)) return brush;
        if (_brushes.Count >= 64) { foreach (var old in _brushes.Values) old.Dispose(); _brushes.Clear(); }
        return _brushes[color] = _context.CreateSolidColorBrush(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
    }

    public IDWriteTextFormat Font(float size, bool bold)
    {
        if (_fonts.TryGetValue((size, bold), out var previous)) return previous.Format;
        if (_fonts.Count >= 32) ClearFonts();
        var format = _text.CreateTextFormat("Segoe UI", bold ? FontWeight.SemiBold : FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, size);
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Near;
        format.WordWrapping = WordWrapping.NoWrap;
        var sign = _text.CreateEllipsisTrimmingSign(format);
        format.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        _fonts[(size, bold)] = (format, sign);
        return format;
    }

    public SizeF Measure(string text, float size, bool bold)
    {
        var key = (text, size, bold);
        if (_measurements.TryGetValue(key, out var result)) return result;
        using var layout = _text.CreateTextLayout(text, Font(size, bold), 100000, 1000);
        var metrics = layout.Metrics;
        if (_measurements.Count >= 128) _measurements.Clear();
        return _measurements[key] = new SizeF(metrics.Width, metrics.Height);
    }

    private void ClearFonts()
    {
        foreach (var font in _fonts.Values) { font.Format.Dispose(); font.Sign.Dispose(); }
        _fonts.Clear(); _measurements.Clear();
    }

    public void Dispose()
    {
        ClearFonts();
        foreach (var brush in _brushes.Values) brush.Dispose();
        _brushes.Clear(); _context.Dispose();
    }
}
