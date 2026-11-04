using System.Runtime.InteropServices;

namespace TrackSwipe.Overlay;

/// <summary>
/// Where the dock's icons are, by app window (screen px, as the dock shows them), as the dock last said: the
/// targets the show-desktop swipe sends windows into while the dock is on, instead of their taskbar buttons. The
/// dock sends the whole map (WM_COPYDATA to TrackSwipe's tray window) whenever its icons move or the open apps
/// change; it's swapped in whole, so the gesture thread always reads a complete one.
/// </summary>
internal static class DockTargets
{
    /// <summary>Tags the dock's map ("TSKD").</summary>
    public const long Tag = 0x54534B44;

    private static volatile IReadOnlyDictionary<IntPtr, Rectangle>? _icons;

    /// <summary>The latest map, or null while the dock is off (or hasn't said yet).</summary>
    public static IReadOnlyDictionary<IntPtr, Rectangle>? Icons => _icons;

    /// <summary>The map as the dock sent it, for Explorer's copy of the taskbar helper when it (re)loads.</summary>
    public static byte[]? Raw { get; private set; }

    public static void Clear() { _icons = null; Raw = null; }

    /// <summary>Reads a map: per window, its handle then the icon's left/top and right/bottom, each pair in one 64-bit value.</summary>
    public static unsafe void Receive(IntPtr data, int bytes)
    {
        var count = bytes / 24;
        var map = new Dictionary<IntPtr, Rectangle>(count);
        var values = (long*)data;
        for (var i = 0; i < count; i++)
        {
            var window = (IntPtr)values[i * 3];
            long topLeft = values[i * 3 + 1], bottomRight = values[i * 3 + 2];
            int left = (int)(topLeft & 0xFFFFFFFF), top = (int)(topLeft >> 32);
            int right = (int)(bottomRight & 0xFFFFFFFF), bottom = (int)(bottomRight >> 32);
            if (right > left && bottom > top) map[window] = Rectangle.FromLTRB(left, top, right, bottom);
        }
        _icons = map;
        Raw = new ReadOnlySpan<byte>((void*)data, count * 24).ToArray();
        Log.Verbose($"Dock: icons for {map.Count} windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
}
