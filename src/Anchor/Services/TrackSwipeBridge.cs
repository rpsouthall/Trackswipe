using System.Runtime.InteropServices;
using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// Working with TrackSwipe (the touchpad gestures app) when it is running, so the dock behaves as
/// part of the same desktop: the same slide when a click lands on another virtual desktop, a dock
/// that stays in view through Mission Control, and windows there that keep clear of it.
/// <para>
/// Everything goes through registered window messages to and from TrackSwipe's hidden window, and
/// a property on each dock window — no shared memory, no service, nothing at all when TrackSwipe
/// isn't running.
/// </para>
/// </summary>
public static class TrackSwipeBridge
{
    /// <summary>Sent to TrackSwipe, wParam the window about to be brought forward: "a jump to its desktop is coming".</summary>
    private static readonly uint JumpMessage = NativeMethods.RegisterWindowMessage("TrackSwipe.JumpToWindow");

    /// <summary>Broadcast by TrackSwipe as Mission Control opens (wParam 1) and once it has closed (0).</summary>
    public static readonly uint MissionControlMessage = NativeMethods.RegisterWindowMessage("TrackSwipe.MissionControl");

    /// <summary>The property on a dock window holding where it sits when shown, for Mission Control to keep clear.</summary>
    private const string DockRectProperty = "TrackSwipe.DockRect";

    private static nint TrackSwipeWindow => NativeMethods.FindWindowEx(nint.Zero, nint.Zero, "TrackSwipe.Window", "TrackSwipe tray");

    /// <summary>
    /// When <paramref name="window"/> is on another virtual desktop and TrackSwipe is running, asks it
    /// to put its slide on screen now, so the switch that bringing the window forward makes Windows
    /// do arrives as a slide rather than a cut. Waits at most a quarter of a second for the answer.
    /// </summary>
    public static void PrepareJumpTo(nint window)
    {
        if (!IsOnAnotherDesktop(window) || TrackSwipeWindow is var trackSwipe && trackSwipe == nint.Zero)
            return;
        // Synchronous on purpose: the window must not be activated until the slide is up, or Windows
        // cuts to the other desktop before there is anything to slide.
        SendMessageTimeout(trackSwipe, JumpMessage, window, nint.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 250, out _);
    }

    private static bool IsOnAnotherDesktop(nint window)
    {
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 ||
            cloaked != NativeMethods.DWM_CLOAKED_SHELL)
            return false;
        try
        {
            var desktops = (NativeMethods.IVirtualDesktopManager)new NativeMethods.VirtualDesktopManager();
            try
            {
                return desktops.IsWindowOnCurrentVirtualDesktop(window, out int here) == 0 && here == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(desktops);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records where a dock sits when shown (physical px), whether or not it is tucked away right now.</summary>
    public static void PublishDockRect(nint dock, int left, int top, int right, int bottom)
    {
        // Four signed 16-bit coordinates in one pointer-sized value: a window property holds a handle.
        long packed = (ushort)(short)left | ((long)(ushort)(short)top << 16) |
                      ((long)(ushort)(short)right << 32) | ((long)(ushort)(short)bottom << 48);
        SetPropW(dock, DockRectProperty, (nint)packed);
    }

    public static void RemoveDockRect(nint dock) => RemovePropW(dock, DockRectProperty);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetPropW(nint hwnd, string name, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemovePropW(nint hwnd, string name);
}
