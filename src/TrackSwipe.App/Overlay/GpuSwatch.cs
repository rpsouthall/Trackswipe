using System.Numerics;
using TrackSwipe.Interop;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using Color = System.Drawing.Color;

namespace TrackSwipe.Overlay;

/// <summary>A cached GPU bitmap for a solid backing or desktop highlight, with no hidden source window.</summary>
internal sealed class GpuSwatch : IDisposable
{
    private static long _identity = -1;
    public IDCompositionSurface Content { get; }
    public Size Size { get; }
    public SceneItem Item { get; }

    public GpuSwatch(Size size, Action<ID2D1DeviceContext> draw)
    {
        Size = size;
        Item = new SceneItem(new IntPtr(Interlocked.Decrement(ref _identity)), new Rectangle(Point.Empty, size),
            new Rectangle(Point.Empty, size), SceneKind.Window, -1, false);
        Content = Composition.Device.CreateSurface((uint)size.Width, (uint)size.Height, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
        using var context = Content.BeginDraw<ID2D1DeviceContext>(null, out var offset);
        try
        {
            context.SetDpi(96, 96);
            context.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            context.Clear(new Color4(0, 0, 0, 0));
            draw(context);
        }
        finally { Content.EndDraw().CheckError(); }
        Composition.Changed();
    }

    public static GpuSwatch Solid(Color color) => new(new Size(1, 1), context =>
        context.Clear(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f)));

    public void Dispose() => Content.Dispose();
}
