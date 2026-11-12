using TrackSwipe.Core;

namespace TrackSwipe.Tests;

public class OverviewCardsTests
{
    // A 2880x1800 screen at 200%: desktop previews above, a dock below.
    private static readonly LayoutRect Area = new(48, 260, 2784, 1260);
    private static OverviewCards Cards => OverviewCards.Arrange(2880, 1800, Area, 2);

    [Fact]
    public void The_card_keeps_the_screens_shape_and_fits_the_area()
    {
        var cards = Cards;
        Assert.Equal(2880.0 / 1800, cards.Card.Width / cards.Card.Height, 6);
        Assert.Equal(1260, cards.Card.Height, 6);
        Assert.Equal(Area.CenterX, cards.Card.CenterX, 6);
        Assert.True(cards.Card.Y >= Area.Y - 1e-6 && cards.Card.Bottom <= Area.Bottom + 1e-6);
    }

    [Fact]
    public void Neighbouring_cards_show_at_the_sides_but_never_touch()
    {
        var cards = Cards;
        var next = cards.CardAt(1);
        var previous = cards.CardAt(-1);
        Assert.True(next.X > cards.Card.Right, "a gap between cards");
        Assert.True(next.X < 2880, "the next desktop's card shows at the right edge");
        Assert.True(previous.Right > 0, "the previous desktop's card shows at the left edge");
        Assert.True(cards.CardAt(2).X >= 2880, "the one after is out of view until a pan");
        Assert.True(cards.CardAt(-2).Right <= 0);
    }

    [Fact]
    public void A_wide_area_still_leaves_room_for_the_neighbours()
    {
        // A portrait monitor: height isn't what limits the card.
        var cards = OverviewCards.Arrange(1200, 1920, new LayoutRect(24, 200, 1152, 1600), 1);
        Assert.True(cards.Scale <= OverviewCards.WidestScale + 1e-9);
        Assert.True(cards.CardAt(1).X < 1200);
    }

    [Fact]
    public void Closed_the_row_is_whole_screens_apart()
    {
        var cards = Cards;
        Assert.True(cards.FarPitch > 2880, "at openness 0 the neighbours are off screen");
        Assert.Equal(cards.FarPitch, cards.PitchAt(0), 6);
        Assert.Equal(cards.Pitch, cards.PitchAt(1), 6);
        Assert.Equal(cards.Pitch, cards.PitchAt(1.3), 6); // pulled past open: no further
        // One zoom: the gap grows by the same factor as the cards.
        Assert.Equal(cards.Pitch / cards.Scale, cards.FarPitch, 6);
    }

    [Fact]
    public void The_screen_maps_onto_the_card()
    {
        var cards = Cards;
        var whole = cards.Map(new LayoutRect(0, 0, 2880, 1800));
        Assert.Equal(cards.Card.X, whole.X, 6);
        Assert.Equal(cards.Card.Width, whole.Width, 6);
        var window = cards.Map(new LayoutRect(1440, 900, 720, 450), layer: 1);
        Assert.Equal(cards.CardAt(1).CenterX, window.X, 6);
        Assert.Equal(cards.Card.CenterY, window.Y, 6);
        Assert.Equal(720 * cards.Scale, window.Width, 6);
    }

    [Fact]
    public void Windows_spread_inside_the_card_with_a_margin()
    {
        var cards = Cards;
        var spread = cards.SpreadArea();
        Assert.True(spread.X > cards.Card.X && spread.Right < cards.Card.Right);
        Assert.True(spread.Y > cards.Card.Y && spread.Bottom < cards.Card.Bottom);
        Assert.Equal(cards.Pitch, cards.SpreadArea(1).X - spread.X, 6);
    }

    [Fact]
    public void A_point_finds_its_card()
    {
        var cards = Cards;
        Assert.Equal(0, cards.LayerAt(cards.Card.CenterX, cards.Card.CenterY));
        Assert.Equal(1, cards.LayerAt(cards.CardAt(1).X + 5, cards.Card.CenterY));
        Assert.Equal(-1, cards.LayerAt(cards.CardAt(-1).Right - 5, cards.Card.CenterY));
        Assert.Null(cards.LayerAt(cards.Card.Right + 2, cards.Card.CenterY)); // the gap between cards
        Assert.Null(cards.LayerAt(cards.Card.CenterX, cards.Card.Y - 5)); // above them
    }

    [Fact]
    public void A_tiny_area_never_gives_a_degenerate_card()
    {
        var cards = OverviewCards.Arrange(1920, 1080, new LayoutRect(0, 0, 1, 1), 1);
        Assert.True(cards.Card.Width > 0 && cards.Card.Height > 0 && cards.Pitch > cards.Card.Width);
    }
}
