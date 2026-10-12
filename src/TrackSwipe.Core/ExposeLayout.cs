namespace TrackSwipe.Core;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public bool Overlaps(LayoutRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public static LayoutRect Lerp(LayoutRect from, LayoutRect to, double t) => new(
        from.X + (to.X - from.X) * t,
        from.Y + (to.Y - from.Y) * t,
        from.Width + (to.Width - from.Width) * t,
        from.Height + (to.Height - from.Height) * t);
}

/// <summary>
/// Mission Control's window spread: every window scaled by one common factor and arranged in
/// rows so none overlap, keeping aspect ratios and roughly their on-screen order.
/// </summary>
public static class ExposeLayout
{
    /// <summary>Returns one rectangle per window, in input order, all inside <paramref name="area"/>.</summary>
    public static LayoutRect[] Arrange(IReadOnlyList<LayoutRect> windows, LayoutRect area, double gap, double maxScale = 1)
    {
        if (windows.Count == 0) return Array.Empty<LayoutRect>();

        var byHeight = Enumerable.Range(0, windows.Count).OrderBy(i => windows[i].CenterY).ToArray();
        LayoutRect[]? best = null;
        var bestScale = double.NegativeInfinity;

        // Try every row count and keep whichever lets the windows be largest.
        for (var rowCount = 1; rowCount <= windows.Count; rowCount++)
        {
            var perRow = (int)Math.Ceiling(windows.Count / (double)rowCount);
            var rows = byHeight.Chunk(perRow)
                .Select(row => row.OrderBy(i => windows[i].CenterX).ToArray())
                .ToArray();

            var scale = maxScale;
            var tallestSum = 0.0;
            foreach (var row in rows)
            {
                scale = Math.Min(scale, (area.Width - gap * (row.Length - 1)) / row.Sum(i => windows[i].Width));
                tallestSum += row.Max(i => windows[i].Height);
            }
            scale = Math.Min(scale, (area.Height - gap * (rows.Length - 1)) / tallestSum);

            if (scale <= bestScale) continue;
            bestScale = scale;
            best = Place(windows, rows, area, gap, Math.Max(scale, 0.01));
        }
        return best!;
    }

    private static LayoutRect[] Place(IReadOnlyList<LayoutRect> windows, int[][] rows, LayoutRect area, double gap, double scale)
    {
        var result = new LayoutRect[windows.Count];
        var rowHeights = rows.Select(row => row.Max(i => windows[i].Height) * scale).ToArray();
        var y = area.Y + (area.Height - rowHeights.Sum() - gap * (rows.Length - 1)) / 2;

        for (var r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            var rowWidth = row.Sum(i => windows[i].Width) * scale + gap * (row.Length - 1);
            var x = area.X + (area.Width - rowWidth) / 2;
            foreach (var i in row)
            {
                var width = windows[i].Width * scale;
                var height = windows[i].Height * scale;
                result[i] = new LayoutRect(x, y + (rowHeights[r] - height) / 2, width, height);
                x += width + gap;
            }
            y += rowHeights[r] + gap;
        }
        return result;
    }
}
