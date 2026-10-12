using System.Drawing;

namespace TrackSwipe.Overlay;

/// <summary>Tracks only geometry DWM can see; hidden motion never requires a native update.</summary>
internal sealed class ThumbnailUpdateCache
{
    public Rectangle Destination { get; private set; }
    public Rectangle? Source { get; private set; }
    public byte Opacity { get; private set; }
    public bool Visible { get; private set; }

    public bool Update(Rectangle destination, byte opacity, Rectangle? source, bool visible)
    {
        if (!visible)
        {
            if (!Visible) return false;
            Visible = false;
            return true;
        }
        if (Visible && destination == Destination && opacity == Opacity && source == Source) return false;
        Destination = destination;
        Opacity = opacity;
        Source = source;
        Visible = true;
        return true;
    }

    public void Reset() => Visible = false;
}
