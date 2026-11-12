namespace TrackSwipe.Core;

/// <summary>How the overview is laid out: as macOS's Mission Control, or as GNOME's Activities overview.</summary>
public enum OverviewStyle
{
    /// <summary>Windows spread over the dimmed wallpaper; desktops named along the top, previewed when pointed at.</summary>
    Mac,
    /// <summary>Each desktop shrinks to a card with its windows spread inside, its neighbours showing at the sides.</summary>
    Gnome,
}

/// <summary>
/// The GNOME-style overview's desktops: the whole screen scaled down into a card, with the neighbouring desktops'
/// cards in a row either side of it. Opening it is one zoom out of that row, so at openness 0 the row is the
/// desktops at full size, a screen (and a scaled-up gap) apart.
/// </summary>
/// <param name="Card">The current desktop's card, in the monitor's coordinates.</param>
/// <param name="Scale">The card's size as a fraction of the screen's.</param>
/// <param name="Pitch">From one card's left edge to the next's, when open.</param>
/// <param name="FarPitch">The same at openness 0, where each card is a whole screen.</param>
/// <param name="Radius">The cards' corner radius when open.</param>
public readonly record struct OverviewCards(LayoutRect Card, double Scale, double Pitch, double FarPitch, double Radius)
{
    /// <summary>The widest a card may be, as a fraction of the screen, so its neighbours always show beside it.</summary>
    public const double WidestScale = 0.84;

    /// <param name="area">Where a card may go: below the desktop previews, above a dock or the taskbar.</param>
    /// <param name="dpiScale">The monitor's scale factor (1 at 96 dpi).</param>
    public static OverviewCards Arrange(double screenWidth, double screenHeight, LayoutRect area, double dpiScale)
    {
        screenWidth = Math.Max(1, screenWidth);
        screenHeight = Math.Max(1, screenHeight);
        var scale = Math.Clamp(Math.Min(area.Height / screenHeight, Math.Min(area.Width / screenWidth, WidestScale)), 0.05, 1);
        double width = screenWidth * scale, height = screenHeight * scale;
        var card = new LayoutRect(area.CenterX - width / 2, area.CenterY - height / 2, width, height);
        // Enough of a gap to read as separate desktops, small enough that a neighbour still shows.
        var gap = Math.Max(24 * dpiScale, Math.Min(screenWidth * 0.04, (screenWidth - width) / 4));
        var pitch = width + gap;
        return new(card, scale, pitch, pitch / scale, 18 * dpiScale);
    }

    /// <summary>Where a rectangle of the screen is drawn inside the card <paramref name="layer"/> desktops along.</summary>
    public LayoutRect Map(LayoutRect onScreen, int layer = 0) => new(
        Card.X + onScreen.X * Scale + layer * Pitch, Card.Y + onScreen.Y * Scale, onScreen.Width * Scale, onScreen.Height * Scale);

    /// <summary>The card <paramref name="layer"/> desktops along (0 is the current desktop's).</summary>
    public LayoutRect CardAt(int layer) => new(Card.X + layer * Pitch, Card.Y, Card.Width, Card.Height);

    /// <summary>The space inside a card that its windows are spread over.</summary>
    public LayoutRect SpreadArea(int layer = 0)
    {
        var pad = Math.Min(Card.Width, Card.Height) * 0.06;
        var card = CardAt(layer);
        return new(card.X + pad, card.Y + pad, Math.Max(1, card.Width - 2 * pad), Math.Max(1, card.Height - 2 * pad));
    }

    /// <summary>How far apart the cards are part-way open: a screen (and more) at 0, <see cref="Pitch"/> at 1.</summary>
    public double PitchAt(double openness) => FarPitch + (Pitch - FarPitch) * Math.Clamp(openness, 0, 1);

    /// <summary>Which card a point is over when open: 0 the current desktop's, ±1 its neighbours'. Null between or beyond them.</summary>
    public int? LayerAt(double x, double y)
    {
        if (y < Card.Y || y >= Card.Bottom) return null;
        var layer = (int)Math.Floor((x - Card.X) / Pitch);
        var within = x - Card.X - layer * Pitch;
        return within < Card.Width ? layer : null;
    }
}
