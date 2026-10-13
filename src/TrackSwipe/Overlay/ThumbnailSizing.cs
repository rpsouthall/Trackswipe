using System.Drawing;

namespace TrackSwipe.Overlay;

/// <summary>Keeps a source raster for the entire thumbnail lifetime; the compositor handles all scaling.</summary>
internal static class ThumbnailSizing
{
    public static Size RasterSize(Size source) => new(Math.Max(1, source.Width), Math.Max(1, source.Height));
}
