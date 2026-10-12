using System.Drawing;

namespace TrackSwipe.Overlay;

[Flags]
internal enum SpriteChanges
{
    None = 0,
    /// <summary>The drawn size changed; it can be applied as a compositor scale during motion.</summary>
    Size = 1,
    Location = 2,
    Clip = 4,
    Opacity = 8,
    Visibility = 16,
}

/// <summary>
/// What one sprite's visuals currently show, and what a new placement changes. A window is drawn whole at
/// its destination size and clipped, never cropped and rescaled, so the surviving pixels stay put.
/// Hidden motion changes nothing, so it's never sent.
/// </summary>
internal sealed class SpriteState
{
    /// <summary>The drawn size rounded out to pixels; the source raster can stay fixed during motion.</summary>
    public Size Size => Size.Ceiling(DrawSize);

    public SizeF DrawSize { get; private set; } = new(1, 1);

    /// <summary>The destination's top-left corner, in surface coordinates.</summary>
    public Point Location => Point.Round(Offset);

    public PointF Offset { get; private set; }

    /// <summary>The visible part, relative to <see cref="Location"/>. Empty until first placed.</summary>
    public RectangleF Clip { get; private set; } = RectangleF.Empty;

    public byte Opacity { get; private set; } = 255;

    public bool Visible { get; private set; }

    /// <param name="viewport">The desktop or tile the window belongs to; nothing outside it may show.</param>
    /// <param name="surface">The surface's size; nothing outside it can show.</param>
    public SpriteChanges Place(RectangleF destination, byte opacity, RectangleF? viewport, Size surface)
    {
        var shown = RectangleF.Intersect(destination, new RectangleF(PointF.Empty, surface));
        if (viewport is { } v) shown.Intersect(v);
        if (opacity == 0 || destination.Width <= 0 || destination.Height <= 0 || shown.Width <= 0 || shown.Height <= 0)
            return Hide();

        var changes = SpriteChanges.None;
        if (destination.Size != DrawSize)
        {
            DrawSize = destination.Size;
            changes |= SpriteChanges.Size;
        }
        if (destination.Location != Offset)
        {
            Offset = destination.Location;
            changes |= SpriteChanges.Location;
        }
        var clip = new RectangleF(shown.X - destination.X, shown.Y - destination.Y, shown.Width, shown.Height);
        if (clip != Clip)
        {
            Clip = clip;
            changes |= SpriteChanges.Clip;
        }
        if (opacity != Opacity)
        {
            Opacity = opacity;
            changes |= SpriteChanges.Opacity;
        }
        if (!Visible)
        {
            Visible = true;
            changes |= SpriteChanges.Visibility;
        }
        return changes;
    }

    public SpriteChanges Hide()
    {
        if (!Visible) return SpriteChanges.None;
        Visible = false;
        return SpriteChanges.Visibility;
    }
}
