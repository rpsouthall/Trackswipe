using System.Drawing;

namespace TrackSwipe.Overlay;

internal static class DesktopReorder
{
    public static Guid[] Move(IReadOnlyList<Guid> ids, Guid desktop, int target)
    {
        if (target < 0 || target >= ids.Count || !ids.Contains(desktop)) return ids.ToArray();
        var result = ids.Where(id => id != desktop).ToList();
        result.Insert(target, desktop);
        return result.ToArray();
    }

    public static int Target(IReadOnlyList<Rectangle> slots, float centre)
    {
        if (slots.Count == 0) return -1;
        var nearest = 0;
        var distance = float.MaxValue;
        for (var i = 0; i < slots.Count; i++)
        {
            var candidate = Math.Abs(centre - (slots[i].X + slots[i].Width / 2f));
            if (candidate < distance) { nearest = i; distance = candidate; }
        }
        return nearest;
    }

    public static bool Starts(Point press, Point pointer, float scale, double held) =>
        held >= 0.25 || Math.Abs(pointer.X - press.X) >= 5 * scale || Math.Abs(pointer.Y - press.Y) >= 5 * scale;

    public static string Label(int index, string? name)
    {
        const string prefix = "Desktop ";
        return string.IsNullOrWhiteSpace(name) ||
            (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), out _))
            ? $"Desktop {index + 1}" : name;
    }

    public static RectangleF Map(RectangleF rect, RectangleF from, RectangleF to) => new(
        to.X + (rect.X - from.X) * to.Width / from.Width,
        to.Y + (rect.Y - from.Y) * to.Height / from.Height,
        rect.Width * to.Width / from.Width, rect.Height * to.Height / from.Height);
}
