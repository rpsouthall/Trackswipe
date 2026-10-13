using System.Drawing;

namespace TrackSwipe.Overlay;

internal static class DesktopBarSelection
{
    public static float Weight(int current, int desktop, double pan, int count)
    {
        var target = current + Math.Sign(pan);
        if (target < 0 || target >= count) return desktop == current ? 1 : 0;
        var progress = (float)Math.Clamp(Math.Abs(pan), 0, 1);
        return desktop == current ? 1 - progress : desktop == target ? progress : 0;
    }

    public static Rectangle[] Arrange(int width, int top, int height, IReadOnlyList<float> textWidths, float scale)
    {
        var inset = 24 * scale;
        var gap = 10 * scale;
        var widths = textWidths.Select(text => text + 26 * scale).ToArray();
        var total = widths.Sum() + gap * Math.Max(0, widths.Length - 1);
        var factor = Math.Min(1, Math.Max(1, width - 2 * inset) / Math.Max(1, total));
        var x = (width - total * factor) / 2;
        var result = new Rectangle[widths.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            result[i] = new Rectangle((int)Math.Round(x), top, Math.Max(1, (int)Math.Round(widths[i] * factor)), height);
            x += (widths[i] + gap) * factor;
        }
        return result;
    }
}
