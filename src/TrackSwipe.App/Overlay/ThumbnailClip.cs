using System.Drawing;

namespace TrackSwipe.Overlay;

/// <summary>Crops a thumbnail without stretching the surviving pixels.</summary>
internal static class ThumbnailClip
{
    public static bool TryClip(Rectangle destination, Rectangle viewport, Rectangle source,
        out Rectangle clippedDestination, out Rectangle clippedSource)
    {
        clippedDestination = Rectangle.Intersect(destination, viewport);
        clippedSource = Rectangle.Empty;
        if (destination.Width <= 0 || destination.Height <= 0 || source.Width <= 0 || source.Height <= 0
            || clippedDestination.Width <= 0 || clippedDestination.Height <= 0) return false;

        // Round both endpoints, rather than rounding a size separately, so adjacent clips agree.
        int X(int x) => source.Left + (int)Math.Round((x - destination.Left) * (double)source.Width / destination.Width);
        int Y(int y) => source.Top + (int)Math.Round((y - destination.Top) * (double)source.Height / destination.Height);
        clippedSource = Rectangle.FromLTRB(X(clippedDestination.Left), Y(clippedDestination.Top),
            X(clippedDestination.Right), Y(clippedDestination.Bottom));
        return clippedSource.Width > 0 && clippedSource.Height > 0;
    }
}
