using System.Drawing;

namespace TrackSwipe.Overlay;

internal static class RestoreCoverLayout
{
    // Keep a cropped, partially off-screen capture aligned with the entire restoring window.
    public static RectangleF Map(RectangleF destination, Rectangle source, Rectangle captured) => new(
        destination.X + (captured.X - source.X) * destination.Width / source.Width,
        destination.Y + (captured.Y - source.Y) * destination.Height / source.Height,
        captured.Width * destination.Width / source.Width,
        captured.Height * destination.Height / source.Height);
}
