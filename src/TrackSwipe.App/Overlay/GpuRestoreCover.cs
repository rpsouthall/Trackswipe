using TrackSwipe.Interop;
using Vortice.DirectComposition;

namespace TrackSwipe.Overlay;

internal sealed class GpuRestoreCover : IDisposable
{
    private readonly ThumbnailSurface _surface;
    private readonly IDCompositionSurface _content;
    private readonly CompositionVisual _visual;
    private GpuRestoreCover(ThumbnailSurface surface, Rectangle region, IDCompositionSurface content)
    {
        _surface = surface;
        _content = content;
        _visual = Composition.CreateVisual();
        using var native = _visual.Borrow();
        native.SetContent(content).CheckError();
        _visual.SetTranslation(region.X, region.Y);
        surface.AddOverlay(_visual);
    }

    public static GpuRestoreCover? Capture(ThumbnailSurface surface, Rectangle region)
    {
        region.Intersect(new Rectangle(Point.Empty, surface.WindowBounds.Size));
        if (region.Width <= 0 || region.Height <= 0) return null;
        var content = Composition.Gpu.Capture.Copy(surface.Monitor, region);
        if (content is not null) Log.Write($"GPU restore cover: {region.Width}x{region.Height}, GPU texture copy, no CPU readback.");
        return content is null ? null : new GpuRestoreCover(surface, region, content);
    }

    public void Hide() => _visual.SetVisible(false);
    public void Dispose()
    {
        _surface.RemoveOverlay(_visual);
        _visual.Release(); _content.Dispose();
    }
}
