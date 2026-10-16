using System.Drawing;

namespace TrackSwipe.Overlay;

/// <summary>Where a window shrinks to on the taskbar when it's minimized: its app icon, or the taskbar itself.</summary>
internal static class TaskbarTarget
{
    /// <summary>
    /// The app icon within a taskbar button, as a square in its monitor's coordinates. Icon-only buttons are the icon;
    /// labelled ones ("never combine" taskbars) carry it at their leading edge, ahead of the window's title.
    /// </summary>
    public static RectangleF Icon(Rectangle button, Point monitorOrigin)
    {
        var x = button.Width > button.Height * 1.3f ? button.X + button.Height / 2f : button.X + button.Width / 2f;
        var y = button.Y + button.Height / 2f;
        var size = Math.Min(button.Width, button.Height) * 0.6f;
        return new RectangleF(x - monitorOrigin.X - size / 2, y - monitorOrigin.Y - size / 2, size, size);
    }

    /// <summary>
    /// For a window without a button: the middle of the taskbar, on whichever edge it's docked (the side where the
    /// work area stops short of the monitor). An auto-hidden taskbar leaves no gap, so the bottom edge stands in.
    /// </summary>
    public static RectangleF Fallback(Rectangle monitor, Rectangle workArea)
    {
        int top = workArea.Top - monitor.Top, bottom = monitor.Bottom - workArea.Bottom;
        int left = workArea.Left - monitor.Left, right = monitor.Right - workArea.Right;
        var thickest = Math.Max(Math.Max(top, bottom), Math.Max(left, right));
        float cx = monitor.Width / 2f, cy = monitor.Height / 2f;
        var strip = Math.Max(thickest, 48);
        if (thickest <= 0 || thickest == bottom) cy = monitor.Height - strip / 2f;
        else if (thickest == top) cy = top / 2f;
        else if (thickest == left) cx = left / 2f;
        else cx = monitor.Width - right / 2f;
        var icon = Math.Max(24, strip * 0.5f);
        return new RectangleF(cx - icon / 2, cy - icon / 2, icon, icon);
    }
}
