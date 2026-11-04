using System.Drawing;
using System.Runtime.InteropServices;

namespace Anchor.UITests;

/// <summary>
/// Mouse movement for the hover tests, injected through <c>SendInput</c>.
/// <para>
/// FlaUI's own <c>Mouse.MoveTo</c> moves the cursor with <c>SetCursorPos</c>, which places the
/// pointer but does not necessarily put a move into the input queue — on a session that is not
/// the active input desktop it does not, and an app that tracks hover by watching pointer moves
/// sees nothing at all while the cursor sits visibly on its icon. Injected input is real input
/// everywhere, so a hover test built on it says something about the app rather than about the
/// machine it ran on. Clicks still go through FlaUI, which injects those.
/// </para>
/// </summary>
internal static class TestMouse
{
    /// <summary>
    /// Walks the cursor to a point in small steps. The steps matter as much as the destination:
    /// hover is a reaction to movement <em>over</em> the strip, and one jump straight to the
    /// target is a single event that a control can legitimately miss.
    /// </summary>
    public static void GlideTo(Point to, int steps = 10)
    {
        var from = Cursor;
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(new Point(
                from.X + (to.X - from.X) * i / steps,
                from.Y + (to.Y - from.Y) * i / steps));
            Thread.Sleep(40);
        }
        MoveTo(to);
    }

    public static void MoveTo(Point p)
    {
        // MOUSEEVENTF_ABSOLUTE coordinates are 0..65535 across the primary display, whatever its
        // pixel size.
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        mouse_event(
            MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
            (int)(p.X * 65535L / Math.Max(1, width)),
            (int)(p.Y * 65535L / Math.Max(1, height)),
            0, UIntPtr.Zero);
    }

    public static Point Cursor => GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
