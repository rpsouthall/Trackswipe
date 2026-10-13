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
    private readonly GpuDrawingResources _resources = Composition.Gpu.Drawing;
    public GpuCanvas(ID2D1DeviceContext context) => _context = context;

    private ID2D1SolidColorBrush Brush(Color color)
    {
        return _resources.Brush(color);
    }

    private IDWriteTextFormat Font(float size, bool bold)
    {
        return _resources.Font(size, bold);
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
        return _resources.Measure(text, size, bold);
    }

    public void Dispose()
    {
        // Resources live with the GPU device, rather than with one repaint.
    }
}
