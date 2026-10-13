using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TrackSwipe.Win32;

/// <summary>A display monitor, in physical pixels (the process is per-monitor DPI aware).</summary>
internal sealed class Screen
{
    private Screen(Rectangle bounds, Rectangle workingArea, bool primary)
    {
        Bounds = bounds;
        WorkingArea = workingArea;
        Primary = primary;
    }

    public Rectangle Bounds { get; }
    public Rectangle WorkingArea { get; }
    public bool Primary { get; }

    /// <summary>Every monitor, in Windows' enumeration order. Queried fresh each time (a few microseconds).</summary>
    public static unsafe Screen[] AllScreens
    {
        get
        {
            var screens = new List<Screen>(4);
            var handle = GCHandle.Alloc(screens);
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &Collect, GCHandle.ToIntPtr(handle));
            }
            finally { handle.Free(); }
            return screens.ToArray();
        }
    }

    public static Screen? PrimaryScreen => AllScreens.FirstOrDefault(screen => screen.Primary);

    /// <summary>The monitor that a rectangle mostly overlaps (or the nearest one).</summary>
    public static Screen FromRectangle(Rectangle rect)
    {
        var all = AllScreens;
        Screen? best = null;
        var bestArea = -1L;
        foreach (var screen in all)
        {
            var overlap = Rectangle.Intersect(screen.Bounds, rect);
            var area = overlap.IsEmpty ? 0 : (long)overlap.Width * overlap.Height;
            if (area > bestArea) { best = screen; bestArea = area; }
        }
        return bestArea > 0 ? best! : all.FirstOrDefault(screen => screen.Primary) ?? all[0];
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int Collect(IntPtr monitor, IntPtr dc, RECT* clip, IntPtr data)
    {
        var info = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (GetMonitorInfoW(monitor, ref info))
        {
            var screens = (List<Screen>)GCHandle.FromIntPtr(data).Target!;
            screens.Add(new Screen(info.rcMonitor.ToRectangle(), info.rcWork.ToRectangle(), (info.dwFlags & 1) != 0));
        }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern unsafe bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RECT*, IntPtr, int> callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
}
