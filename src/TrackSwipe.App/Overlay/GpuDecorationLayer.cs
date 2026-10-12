using System.Numerics;
using TrackSwipe.Interop;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TrackSwipe.Overlay;

/// <summary>Labels and highlights drawn directly to a hardware composition surface.</summary>
internal sealed class GpuDecorationLayer : IDisposable
{
    private readonly ThumbnailSurface _surface;
    private readonly IDCompositionSurface _content;
    private readonly CompositionVisual _visual;
    private float _opacity = -1;
    private bool _animated;
    private RectangleF? _placedBounds;
    private readonly bool _localCoordinates;
    public Rectangle Bounds { get; }
    public GpuDecorationLayer(ThumbnailSurface surface, Rectangle? bounds = null, bool localCoordinates = false)
    {
        _surface = surface;
        Bounds = bounds ?? new Rectangle(Point.Empty, surface.WindowBounds.Size);
        _localCoordinates = localCoordinates;
        _content = Composition.Device.CreateSurface((uint)Bounds.Width, (uint)Bounds.Height, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
        _visual = Composition.CreateVisual();
        using var native = _visual.Borrow();
        native.SetContent(_content).CheckError();
        native.SetBitmapInterpolationMode(Vortice.DirectComposition.BitmapInterpolationMode.Linear).CheckError();
        _visual.SetTranslation(Bounds.X, Bounds.Y);
        _visual.SetVisible(false);
        _surface.AddOverlay(_visual);
        _surface.HotKeyPressed += ForwardHotKey;
    }

    public IntPtr Handle => _surface.Handle;
    public bool IsShown { get; private set; }
    public event Action<int>? HotKeyPressed;
    private void ForwardHotKey(int id) => HotKeyPressed?.Invoke(id);

    public void Paint(Action<GpuCanvas> draw)
    {
        using var context = _content.BeginDraw<ID2D1DeviceContext>(null, out var offset);
        try
        {
            context.SetDpi(96, 96);
            context.Transform = Matrix3x2.CreateTranslation(offset.X - (_localCoordinates ? 0 : Bounds.X), offset.Y - (_localCoordinates ? 0 : Bounds.Y));
            context.Clear(new Color4(0, 0, 0, 0));
            using var canvas = new GpuCanvas(context);
            draw(canvas);
        }
        finally { _content.EndDraw().CheckError(); }
        Composition.Changed();
    }

    public void SetAlpha(float alpha)
    {
        var opacity = Math.Clamp(alpha / 255f, 0, 1);
        if (opacity == _opacity && !_animated) return;
        _animated = false;
        _opacity = opacity;
        _visual.SetOpacity(opacity);
    }

    public void AnimateOpacity(IReadOnlyList<SpriteFrame> frames, long begin)
    {
        using var animation = CompositorMotion.Curve(frames, frame => frame.Opacity, begin);
        _visual.AnimateOpacity(animation);
        _animated = true;
    }

    public void Place(RectangleF bounds, float opacity)
    {
        if (_placedBounds != bounds || _animated)
            _visual.SetMatrix(bounds.Width / Bounds.Width, bounds.Height / Bounds.Height, bounds.X, bounds.Y);
        if (_opacity != opacity || _animated) _visual.SetOpacity(opacity);
        _placedBounds = bounds;
        _opacity = opacity;
        _animated = false;
    }

    public void Animate(IReadOnlyList<SpriteFrame> frames, long begin)
    {
        using var transform = Composition.Device.CreateMatrixTransform();
        var identity = Matrix3x2.Identity;
        transform.SetMatrix(ref identity).CheckError();
        using var x = CompositorMotion.Curve(frames, frame => frame.Bounds.X, begin);
        using var y = CompositorMotion.Curve(frames, frame => frame.Bounds.Y, begin);
        using var width = CompositorMotion.Curve(frames, frame => frame.Bounds.Width / Bounds.Width, begin);
        using var height = CompositorMotion.Curve(frames, frame => frame.Bounds.Height / Bounds.Height, begin);
        transform.SetMatrixElement(2, 0, x).CheckError(); transform.SetMatrixElement(2, 1, y).CheckError();
        transform.SetMatrixElement(0, 0, width).CheckError(); transform.SetMatrixElement(1, 1, height).CheckError();
        _visual.SetTransform(transform);
        AnimateOpacity(frames, begin);
        Show();
    }

    public void Show() { _visual.SetVisible(true); IsShown = true; }
    public void Hide() { _visual.SetVisible(false); IsShown = false; }

    public void Dispose()
    {
        _surface.HotKeyPressed -= ForwardHotKey;
        _surface.RemoveOverlay(_visual);
        _visual.Release(); _content.Dispose();
    }
}
