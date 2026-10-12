using System.Numerics;
using TrackSwipe.Interop;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Color = System.Drawing.Color;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace TrackSwipe.Overlay;

/// <summary>Hardware drawing commands, with brushes and text formats scoped to one repaint.</summary>
internal sealed class GpuCanvas : IDisposable
{
    private readonly ID2D1DeviceContext _context;
    private readonly Dictionary<Color, ID2D1SolidColorBrush> _brushes = new();
    private readonly Dictionary<(float Size, bool Bold), IDWriteTextFormat> _fonts = new();
    private readonly List<IDWriteInlineObject> _ellipsis = new();
    public GpuCanvas(ID2D1DeviceContext context) => _context = context;

    private ID2D1SolidColorBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
            _brushes[color] = brush = _context.CreateSolidColorBrush(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
        return brush;
    }

    private IDWriteTextFormat Font(float size, bool bold)
    {
        if (!_fonts.TryGetValue((size, bold), out var format))
        {
            format = Composition.Gpu.Text.CreateTextFormat("Segoe UI", bold ? FontWeight.SemiBold : FontWeight.Normal, FontStyle.Normal, size);
            format.TextAlignment = TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Near;
            format.WordWrapping = WordWrapping.NoWrap;
            var sign = Composition.Gpu.Text.CreateEllipsisTrimmingSign(format);
            format.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
            _ellipsis.Add(sign);
            _fonts[(size, bold)] = format;
        }
        return format;
    }

    private static RoundedRectangle Rounded(RectangleF rect, float radius) => new()
    {
        Rect = new RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), RadiusX = radius, RadiusY = radius,
    };

    public void DrawRounded(Color color, float width, RectangleF rect, float radius) =>
        _context.DrawRoundedRectangle(Rounded(rect, radius), Brush(color), width);

    public void FillRounded(Color color, RectangleF rect, float radius) =>
        _context.FillRoundedRectangle(Rounded(rect, radius), Brush(color));

    public void DrawLine(Color color, float width, float x1, float y1, float x2, float y2) =>
        _context.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), Brush(color), width, null);

    public void DrawText(string text, float size, bool bold, Color color, RectangleF rect) =>
        _context.DrawText(text, Font(size, bold), new Rect(rect.X, rect.Y, rect.Width, rect.Height), Brush(color), DrawTextOptions.Clip);

    public SizeF MeasureText(string text, float size, bool bold)
    {
        using var layout = Composition.Gpu.Text.CreateTextLayout(text, Font(size, bold), 100000, 1000);
        var metrics = layout.Metrics;
        return new SizeF(metrics.Width, metrics.Height);
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        foreach (var sign in _ellipsis) sign.Dispose();
        foreach (var brush in _brushes.Values) brush.Dispose();
    }
}
