using System.Drawing;
using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class DesktopBarTests
{
    [Theory]
    [InlineData(2496, 1664, 1.5f, 4)]
    [InlineData(1920, 1080, 1f, 12)]
    [InlineData(1280, 800, 1.25f, 16)]
    public void Previews_fit_without_overlap_and_leave_room_for_compact_right_button(int width, int height, float scale, int count)
    {
        var work = new Rectangle(0, 0, width, height - (int)(48 * scale));
        var bar = DesktopBarLayout.Arrange(new Size(width, height), work, scale, count, true);
        var add = Assert.IsType<Rectangle>(bar.AddButton);
        Assert.Equal(width - (int)(24 * scale), add.Right);
        Assert.Equal((int)(36 * scale), add.Width);
        Assert.Equal(add.Width, add.Height);
        Assert.Equal(count, bar.Tiles.Length);
        for (var i = 0; i < count; i++)
        {
            var tile = bar.Tiles[i];
            Assert.True(work.Contains(tile));
            Assert.True(tile.Right < add.Left);
            Assert.True(tile.Contains(DesktopBarLayout.CloseButton(tile, scale)));
            Assert.InRange(Math.Abs(tile.Height - tile.Width * height / (double)width), 0, 1);
            if (i > 0) Assert.True(bar.Tiles[i - 1].Right <= tile.Left);
        }
    }

    [Fact]
    public void Close_target_is_inside_the_top_right_corner_and_does_not_reach_the_next_preview()
    {
        var tile = new Rectangle(100, 40, 260, 174);
        var close = DesktopBarLayout.CloseButton(tile, 1.5f);
        Assert.True(tile.Contains(close));
        Assert.True(close.Left > tile.Left + tile.Width / 2);
        Assert.True(close.Bottom < tile.Top + tile.Height / 2);
    }

    [Theory]
    [InlineData(1, 0, -1)]
    [InlineData(4, 0, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(4, 3, 2)]
    [InlineData(4, 4, -1)]
    public void Closing_keeps_a_surviving_neighbour_and_protects_the_last_desktop(int count, int index, int fallback) =>
        Assert.Equal(fallback, DesktopBarLayout.FallbackIndex(count, index));

    [Fact]
    public void Viewed_desktop_is_preserved_by_identity_when_indices_shift()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(1, DesktopBarLayout.ViewedIndex(new[] { ids[0], ids[2], ids[3] }, ids[2], ids[0], 0));
        Assert.Equal(0, DesktopBarLayout.ViewedIndex(new[] { ids[0], ids[2], ids[3] }, ids[1], ids[0], 2));
    }

    [Fact]
    public void Unsupported_shell_omits_add_control()
    {
        var bar = DesktopBarLayout.Arrange(new Size(1920, 1080), new Rectangle(0, 0, 1920, 1032), 1, 1, false);
        Assert.Null(bar.AddButton);
        Assert.Single(bar.Tiles);
    }

    [Theory]
    [InlineData(2496, 1664, 2f, 3)]
    [InlineData(1920, 1080, 1f, 8)]
    public void The_card_layouts_previews_are_smaller_and_leave_the_desktops_more_room(int width, int height, float scale, int count)
    {
        var work = new Rectangle(0, 0, width, height - (int)(48 * scale));
        var named = DesktopBarLayout.Arrange(new Size(width, height), work, scale, count, true);
        var small = DesktopBarLayout.Arrange(new Size(width, height), work, scale, count, true, small: true);
        Assert.Equal(count, small.Tiles.Length);
        Assert.True(small.Tiles[0].Width < named.Tiles[0].Width);
        Assert.True(small.SpreadTop < named.SpreadTop);
        Assert.True(small.SpreadTop > small.Tiles[0].Bottom, "the desktops start below the previews");
        foreach (var tile in small.Tiles)
        {
            Assert.True(work.Contains(tile));
            Assert.InRange(Math.Abs(tile.Height - tile.Width * height / (double)width), 0, 1);
        }
        // As much room above the previews as below them, and they sit closer together than the named ones.
        Assert.Equal(small.Tiles[0].Top - work.Top, small.SpreadTop - small.Tiles[0].Bottom);
        if (count > 1) Assert.True(small.Tiles[1].Left - small.Tiles[0].Right < named.Tiles[1].Left - named.Tiles[0].Right);
        // Lowered (to sit midway between the top and a card that leaves room to spare), everything moves down together.
        var lowered = DesktopBarLayout.Arrange(new Size(width, height), work, scale, count, true, small: true, lower: 30);
        Assert.Equal(small.Tiles[0].Top + 30, lowered.Tiles[0].Top);
        Assert.Equal(small.Tiles[0].Left, lowered.Tiles[0].Left);
        Assert.Equal(small.SpreadTop + 30, lowered.SpreadTop);
        // A window dragged within a card isn't over a preview's drop zone: the zones stop above where the cards start.
        var (zones, _) = DesktopBarLayout.DropZones(small.Tiles, small.AddButton, width, scale, small: true);
        Assert.All(zones, zone => Assert.True(zone.Bottom <= small.SpreadTop));
    }
}
