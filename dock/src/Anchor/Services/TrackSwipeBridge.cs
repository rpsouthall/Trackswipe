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
    private static readonly uint ChooseMessage = NativeMethods.RegisterWindowMessage("TrackSwipe.Choose");

    /// <summary>TrackSwipe's Mission Control is open (from its broadcast), so a click on the dock goes to it first.</summary>
    public static bool MissionControlOpen { get; set; }

    /// <summary>
    /// Asks TrackSwipe's open Mission Control to close onto <paramref name="window"/>, as clicking its thumbnail does.
    /// True if it did; false if that window isn't in view (or Mission Control isn't open). Zero just closes it.
    /// </summary>
    public static bool ChooseInMissionControl(nint window)
    {
        if (TrackSwipeWindow is var trackSwipe && trackSwipe == nint.Zero)
            return false;
        return SendMessageTimeout(trackSwipe, ChooseMessage, window, nint.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 250, out var result) != 0 && result != 0;
    }

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
            var desktops = ComInterop.Create<NativeMethods.IVirtualDesktopManager>(NativeMethods.CLSID_VirtualDesktopManager);
            try
            {
                return desktops.IsWindowOnCurrentVirtualDesktop(window, out int here) == 0 && here == 0;
            }
            finally
            {
                ComInterop.Release(desktops);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tags the icon map sent with WM_COPYDATA ("TSKD").</summary>
    private const nint IconMapTag = 0x54534B44;

    /// <summary>
    /// Tells TrackSwipe which dock icon each open app window belongs to (screen px, as shown), so its
    /// show-desktop swipe sends windows into their icons on the dock rather than the taskbar. One
    /// WM_COPYDATA of 24 bytes a window; nothing when TrackSwipe isn't running.
    /// </summary>
    public static unsafe void SendIconMap(IReadOnlyList<(nint Window, Windows.Graphics.RectInt32 Icon)> map)
    {
        if (TrackSwipeWindow is var trackSwipe && trackSwipe == nint.Zero)
            return;
        var data = new long[map.Count * 3];
        for (int i = 0; i < map.Count; i++)
        {
            var (window, icon) = map[i];
            data[i * 3] = window;
            data[i * 3 + 1] = (uint)icon.X | ((long)icon.Y << 32);
            data[i * 3 + 2] = (uint)(icon.X + icon.Width) | ((long)(icon.Y + icon.Height) << 32);
        }
        fixed (long* pointer = data)
        {
            var copy = new COPYDATASTRUCT { dwData = IconMapTag, cbData = data.Length * sizeof(long), lpData = (nint)pointer };
            // WM_COPYDATA has to be sent, not posted: the data lives only for the call.
            SendMessageTimeout(trackSwipe, 0x004A /* WM_COPYDATA */, nint.Zero, (nint)(&copy), 0x0002 /* SMTO_ABORTIFHUNG */, 200, out _);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nint dwData;
        public int cbData;
        public nint lpData;
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
