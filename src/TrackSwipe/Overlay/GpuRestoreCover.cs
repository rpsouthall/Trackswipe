using TrackSwipe.Interop;
using Vortice.DirectComposition;
using System.Numerics;

namespace TrackSwipe.Overlay;

internal sealed class GpuRestoreCover : IDisposable
{
    private readonly ThumbnailSurface _surface;
    private readonly IDCompositionSurface _content;
    private readonly CompositionVisual _visual;
    private readonly Rectangle _windowRegion, _captureRegion;
    private GpuRestoreCover(ThumbnailSurface surface, Rectangle windowRegion, Rectangle region, IDCompositionSurface content)
    {
        _surface = surface;
        _content = content;
        _windowRegion = windowRegion;
        _captureRegion = region;
        _visual = Composition.CreateVisual();
        using var native = _visual.Borrow();
        native.SetContent(content).CheckError();
        native.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear).CheckError();
        _visual.SetTranslation(region.X, region.Y);
        _visual.SetOpacity(1);
        _visual.SetVisible(true);
        surface.AddOverlay(_visual);
    }

    public static GpuRestoreCover? Capture(ThumbnailSurface surface, Rectangle region, long notBefore = 0)
    {
        var windowRegion = region;
        region.Intersect(new Rectangle(Point.Empty, surface.WindowBounds.Size));
        if (region.Width <= 0 || region.Height <= 0) return null;
        var content = Composition.Gpu.Capture.Copy(surface.Monitor, region, notBefore);
        if (content is not null) Log.Verbose($"GPU restore cover: {region.Width}x{region.Height}, GPU texture copy, no CPU readback.");
        return content is null ? null : new GpuRestoreCover(surface, windowRegion, region, content);
    }

    public void Animate(IReadOnlyList<SpriteFrame> frames, long begin)
    {
        RectangleF Bounds(SpriteFrame frame) => RestoreCoverLayout.Map(frame.Bounds, _windowRegion, _captureRegion);
        using var transform = Composition.Device.CreateMatrixTransform();
        var identity = Matrix3x2.Identity;
        transform.SetMatrix(ref identity).CheckError();
        using var x = CompositorMotion.Curve(frames, frame => Bounds(frame).X, begin);
        using var y = CompositorMotion.Curve(frames, frame => Bounds(frame).Y, begin);
        using var width = CompositorMotion.Curve(frames, frame => Bounds(frame).Width / _captureRegion.Width, begin);
        using var height = CompositorMotion.Curve(frames, frame => Bounds(frame).Height / _captureRegion.Height, begin);
        transform.SetMatrixElement(2, 0, x).CheckError(); transform.SetMatrixElement(2, 1, y).CheckError();
        transform.SetMatrixElement(0, 0, width).CheckError(); transform.SetMatrixElement(1, 1, height).CheckError();
        _visual.SetTransform(transform);
    }

    public void Hide() => _visual.SetVisible(false);
    public void Dispose()
    {
        _surface.RemoveOverlay(_visual);
        _visual.Release(); _content.Dispose();
    }
}
