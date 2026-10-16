using System.Runtime.InteropServices;

namespace TrackSwipe.Interop;

/// <summary>
/// The optional taskbar tweaks (hide the Start button, hide the taskbar's edge line, larger tray icons, hide the
/// running-app indicators), done by TrackSwipeTap.dll inside Explorer: the Start button is a XAML control, not a
/// window, so nothing outside Explorer can hide it. The DLL is embedded in this executable and only written out and loaded, through XAML Diagnostics (the interface Visual
/// Studio's Live Visual Tree uses), once an option is turned on. It stays in Explorer until Explorer restarts, so it's
/// asked to put the taskbar back when the options go off or TrackSwipe quits. Values mirror src/TrackSwipeTap/Protocol.h.
/// </summary>
internal static unsafe class TaskbarTap
{
    private const int Version = 22;
    private const string WindowClass = "TrackSwipe.Tap.22"; // "TrackSwipe.Tap.{Version}"
    /// <summary>Earlier versions' windows (the first unnumbered): still in Explorer until it restarts, so they're asked to stand down first.</summary>
    private static readonly string[] OlderWindowClasses = Enumerable.Range(1, Version - 1).Select(v => v == 1 ? "TrackSwipe.Tap" : $"TrackSwipe.Tap.{v}").ToArray();

    /// <summary>
    /// Space before the first app button while Start is hidden, in hundredths of an effective pixel, on a left-aligned
    /// taskbar: the first icon then sits as far from the left edge as the clock's text does from the right (16: the
    /// 12 px show-desktop strip and the clock's 4 px inset). A centred taskbar is balanced already.
    /// </summary>
    private const int LeftAlignedPadding = 850;

    /// <summary>
    /// Larger tray icons, in hundredths of an effective pixel: the glyph size (Windows draws them at 12; 18 is about
    /// the size a Mac's menu bar icons appear beside them), and the space added beside each so the row is spaced like
    /// a Mac's. The row grows to the left; the clock stays put.
    /// </summary>
    private const int TrayIconSize = 1800, TrayIconSpace = 800;

    /// <summary>
    /// Taskbar text with larger icons (the clock, battery percentage and app titles), in hundredths of an effective
    /// pixel: its size (on a Mac, text capitals stand about 0.83 of the Wi-Fi icon's height: 14 here, from 12) and how
    /// far it's moved down; at 14 its capitals already centre on the icons' line (measured on screen).
    /// </summary>
    private const int TrayTextSize = 1400, TrayTextOffset = 0;

    /// <summary>
    /// App buttons with larger tray icons, in hundredths of an effective pixel: the icons' size (Windows: 16), drawn
    /// sharp from the shell's own icons, and the space added between buttons. With Start hidden, the space before the
    /// first icon changes by half of each (icons grow from their centre; the first button's margin adds to the space).
    /// </summary>
    private const int AppIconSize = 2000, AppIconSpacing = 600;
    private const int StartButtonMessage = 0x8000 + 1, EdgeLineMessage = 0x8000 + 2, QueryMessage = 0x8000 + 3, DumpMessage = 0x8000 + 4, TrayIconsMessage = 0x8000 + 5, IndicatorsMessage = 0x8000 + 6, TrayTextMessage = 0x8000 + 7, AppIconsMessage = 0x8000 + 8;
    private static readonly Guid Clsid = new("5c36979c-4b69-4ae1-9b76-9d4b249e5659");
    private const string Resource = "TrackSwipeTap.dll";

    /// <summary>Whether this build carries the DLL (release builds do; a build without the C++ tools may not).</summary>
    public static bool Available => typeof(TaskbarTap).Assembly.GetManifestResourceInfo(Resource) is not null;

    /// <summary>Explorer's copy of the DLL is loaded and listening.</summary>
    public static bool Loaded => Window != IntPtr.Zero;

    private static IntPtr Window => FindWindowExW((IntPtr)(-3) /* HWND_MESSAGE */, IntPtr.Zero, WindowClass, null);

    /// <summary>
    /// Sends the options to Explorer's copy. False if it isn't loaded: call <see cref="Load"/>, then try again once
    /// it has started (it appears a moment later, on the taskbar's thread).
    /// </summary>
    public static bool Send(bool hideStart, bool hideEdge, bool largeTrayIcons, bool hideIndicators)
    {
        var window = Window;
        if (window == IntPtr.Zero) return false;
        SendMessageTimeoutW(window, StartButtonMessage, hideStart ? 1 : 0, (IntPtr)(hideStart && LeftAligned ? LeftAlignedPadding + (largeTrayIcons ? (AppIconSize - 1600 - AppIconSpacing) / 2 : 0) : 0), 0x0002 /* ABORTIFHUNG */, 2000, out _);
        SendMessageTimeoutW(window, EdgeLineMessage, hideEdge ? 1 : 0, IntPtr.Zero, 0x0002, 2000, out _);
        SendMessageTimeoutW(window, TrayIconsMessage, largeTrayIcons ? TrayIconSize : 0, largeTrayIcons ? TrayIconSpace : 0, 0x0002, 2000, out _);
        SendMessageTimeoutW(window, AppIconsMessage, largeTrayIcons ? AppIconSize : 0, largeTrayIcons ? AppIconSpacing : 0, 0x0002, 2000, out _);
        SendMessageTimeoutW(window, TrayTextMessage, largeTrayIcons ? TrayTextSize : 0, largeTrayIcons ? TrayTextOffset : 0, 0x0002, 2000, out _);
        SendMessageTimeoutW(window, IndicatorsMessage, hideIndicators ? 1 : 0, IntPtr.Zero, 0x0002, 2000, out _);
        return true;
    }

    /// <summary>Puts the taskbar back as Explorer draws it, if the DLL (this version or an earlier one) is there to ask.</summary>
    public static void Restore()
    {
        Send(false, false, false, false);
        StandDownOlder();
    }

    /// <summary>Tells an earlier version's copy in Explorer to give back what it hid, so two copies never compete.</summary>
    private static void StandDownOlder()
    {
        foreach (var name in OlderWindowClasses)
        {
            var window = FindWindowExW((IntPtr)(-3), IntPtr.Zero, name, null);
            if (window == IntPtr.Zero) continue;
            // Every option any version has had; a version that doesn't know one ignores it.
            foreach (var message in new[] { StartButtonMessage, EdgeLineMessage, TrayIconsMessage, IndicatorsMessage, TrayTextMessage, AppIconsMessage })
                SendMessageTimeoutW(window, message, 0, IntPtr.Zero, 0x0002, 2000, out _);
        }
    }

    /// <summary>Taskbar icons aligned left (Settings, Taskbar behaviours) rather than centred, Windows' default.</summary>
    private static bool LeftAligned
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int alignment && alignment == 0;
        }
    }

    /// <summary>Status from Explorer's copy (see Protocol.h), or -1 if it isn't loaded.</summary>
    public static long Query()
    {
        var window = Window;
        if (window == IntPtr.Zero) return -1;
        SendMessageTimeoutW(window, QueryMessage, 0, IntPtr.Zero, 0x0002, 2000, out var result);
        return (long)result;
    }

    /// <summary>Development aid: Explorer's copy writes the taskbar's XAML tree to %LOCALAPPDATA%\TrackSwipe\taskbar-tree.txt.</summary>
    public static void Dump()
    {
        var window = Window;
        if (window != IntPtr.Zero) SendMessageTimeoutW(window, DumpMessage, 0, IntPtr.Zero, 0x0002, 5000, out _);
    }

    /// <summary>Writes the DLL out (once per version) and loads it into the Explorer process that owns the taskbar.</summary>
    public static bool Load()
    {
        if (Loaded) return true;
        StandDownOlder();
        var taskbar = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        GetWindowThreadProcessId(taskbar, out var pid);
        if (Extract() is not { } dll) return false;
        var xaml = LoadLibraryExW("Windows.UI.Xaml.dll", IntPtr.Zero, 0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
        var initialize = xaml == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(xaml, "InitializeXamlDiagnosticsEx");
        if (initialize == IntPtr.Zero) { Log.Write("Taskbar options unavailable: XAML Diagnostics isn't on this Windows."); return false; }
        var hr = 0;
        fixed (char* path = dll)
        fixed (char* empty = "")
        {
            // XAML only accepts endpoints named VisualDiagConnection<n>, one connection per number: "not found" means
            // that number is taken (or not offered), so try the next.
            for (var i = 1; i <= 10000; i++)
            {
                fixed (char* endpoint = $"VisualDiagConnection{i}")
                    hr = ((delegate* unmanaged[Stdcall]<char*, uint, char*, char*, Guid, char*, int>)initialize)(endpoint, pid, empty, path, Clsid, null);
                if (hr != unchecked((int)0x80070490) /* HRESULT_FROM_WIN32(ERROR_NOT_FOUND) */) break;
            }
        }
        Log.Write($"Taskbar options: loaded into Explorer (process {pid}): 0x{hr:X8}.");
        return hr >= 0;
    }

    /// <summary>The DLL on disk, written from this executable if it isn't there yet. Named by content, so an update
    /// never needs to replace a copy Explorer has open.</summary>
    private static string? Extract()
    {
        try
        {
            using var stream = typeof(TaskbarTap).Assembly.GetManifestResourceStream(Resource);
            if (stream is null) { Log.Write("Taskbar options unavailable: this build doesn't include TrackSwipeTap.dll."); return null; }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var folder = Setup.Installer.RunningInstalled ? Setup.Installer.InstallDirectory : Path.GetDirectoryName(Log.FilePath)!;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"TrackSwipeTap-{hash}.dll");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) File.WriteAllBytes(path, bytes);
            // Earlier versions' copies: gone once Explorer has let go of them (it keeps the loaded one open).
            foreach (var old in Directory.EnumerateFiles(folder, "TrackSwipeTap-*.dll"))
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Taskbar options: couldn't write TrackSwipeTap.dll: {ex.Message}");
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr window, int message, nint wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
