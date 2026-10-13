namespace TrackSwipe.Core;

/// <summary>Keeps overview layout stable while windows shrink elastically beyond the open limit.</summary>
public static class OverviewPresentation
{
    public static double Layout(double openness) => Math.Clamp(openness, 0, 1);

    public static LayoutRect WindowBounds(LayoutRect bounds, double openness)
    {
        // Scale each window about its own centre; the desktop bar, wallpaper and
        // taskbar retain their normal layout. Resistance bounds the extra shrink.
        var excess = Math.Max(0, openness - 1);
        var scale = 1 - 0.08 * excess / (0.12 + excess);
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        return new(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }
}
