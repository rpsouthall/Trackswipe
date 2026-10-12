using System.Drawing;

namespace TrackSwipe.Overlay;

/// <summary>Centred desktop previews with a compact add control anchored to the right edge.</summary>
internal sealed record DesktopBarLayout(Rectangle[] Tiles, Rectangle? AddButton, int SpreadTop)
{
    public static DesktopBarLayout Arrange(Size screen, Rectangle work, float scale, int count, bool canAdd)
    {
        count = Math.Max(1, count);
        var inset = Math.Max(1, (int)(24 * scale));
        var button = Math.Max(1, (int)(36 * scale));
        var gap = Math.Max(1, (int)(22 * scale));
        var y = work.Top + (int)(20 * scale);
        var right = work.Right - inset;
        var addX = right - button;
        var availableRight = canAdd ? addX - gap : right;
        var available = Math.Max(count, availableRight - (work.Left + inset));
        gap = Math.Min(gap, Math.Max(0, (available - count) / Math.Max(1, count - 1)));
        var width = Math.Max(1, Math.Min((int)(screen.Width * 0.105), (available - (count - 1) * gap) / count));
        var height = Math.Max(1, (int)Math.Round(width * screen.Height / (double)screen.Width));
        var total = count * width + (count - 1) * gap;
        var x = Math.Clamp((screen.Width - total) / 2, work.Left + inset, Math.Max(work.Left + inset, availableRight - total));
        var tiles = Enumerable.Range(0, count).Select(i => new Rectangle(x + i * (width + gap), y, width, height)).ToArray();
        Rectangle? add = canAdd ? new Rectangle(addX, y + Math.Max(0, (height - button) / 2), button, button) : null;
        return new(tiles, add, y + Math.Max(height, button) + (int)(70 * scale));
    }

    public static Rectangle CloseButton(Rectangle tile, float scale)
    {
        var shortest = Math.Min(tile.Width, tile.Height);
        var inset = Math.Min(Math.Max(1, (int)(4 * scale)), shortest / 4);
        var size = Math.Max(1, Math.Min((int)(22 * scale), shortest - 2 * inset));
        return new Rectangle(tile.Right - size - inset, tile.Top + inset, size, size);
    }

    public static int FallbackIndex(int count, int closing) =>
        count <= 1 || closing < 0 || closing >= count ? -1 : closing > 0 ? closing - 1 : 1;

    public static int ViewedIndex(IReadOnlyList<Guid> ids, Guid viewed, Guid fallback, int current)
    {
        for (var i = 0; i < ids.Count; i++) if (ids[i] == viewed) return i;
        for (var i = 0; i < ids.Count; i++) if (ids[i] == fallback) return i;
        return Math.Clamp(current, 0, ids.Count - 1);
    }
}
