using System.Text;
using TrackSwipe.Desktops;
using TrackSwipe.Interop;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

internal enum SceneKind { Wallpaper, Window, Taskbar }

/// <summary>
/// A window to mirror. <see cref="Frame"/> is its visible frame; <see cref="Bounds"/> also includes
/// the invisible resize borders. <see cref="Desktop"/> is the index of the virtual desktop it's on.
/// App windows are the ones Alt+Tab would list; the rest (tooltips, overlays, owned popups) don't
/// get a place in Mission Control. Minimized windows (app windows only) carry their restored bounds.
/// <see cref="IsDark"/> says whether the app draws in dark mode, which picks its backing colour.
/// <see cref="IsTopmost"/> windows (always on top, or some full-screen apps) stay above any window that's activated.
/// </summary>
internal readonly record struct SceneItem(IntPtr Window, Rectangle Bounds, Rectangle Frame, SceneKind Kind, int Desktop, bool IsAppWindow, bool IsMinimized = false, bool IsDark = true, bool IsTopmost = false);

/// <summary>
/// Every desktop's windows as one bottom-to-top list. Windows on other desktops are cloaked by
/// the shell, but DWM keeps rendering them, so their thumbnails stay live.
/// </summary>
internal sealed class DesktopScene
{
    private const int DWM_CLOAKED_SHELL = 2;

    private DesktopScene(IReadOnlyList<SceneItem> items, VirtualDesktopInfo? desktops, int current, string stats)
    {
        Items = items;
        Desktops = desktops;
        Current = current;
        Stats = stats;
    }

    public IReadOnlyList<SceneItem> Items { get; }

    /// <summary>Null when the desktop list couldn't be read; the scene then holds only the current desktop.</summary>
    public VirtualDesktopInfo? Desktops { get; }

    public int Current { get; }

    public int DesktopCount => Desktops?.Count ?? 1;

    /// <summary>How windows were classified, for diagnostics.</summary>
    public string Stats { get; }

    public IEnumerable<SceneItem> Wallpapers => Items.Where(item => item.Kind == SceneKind.Wallpaper);

    public IEnumerable<SceneItem> Taskbars => Items.Where(item => item.Kind == SceneKind.Taskbar);

    /// <summary>All of a desktop's windows, bottom to top, including minimized ones.</summary>
    public IEnumerable<SceneItem> WindowsOn(int desktop) =>
        Items.Where(item => item.Kind == SceneKind.Window && item.Desktop == desktop);

    /// <summary>The windows actually on screen when that desktop is showing.</summary>
    public IEnumerable<SceneItem> VisibleWindowsOn(int desktop) => WindowsOn(desktop).Where(item => !item.IsMinimized);

    /// <summary>The topmost visible app window on a desktop (or zero). When it uncloaks, a switch has really happened.</summary>
    public IntPtr TopWindowOn(int desktop) =>
        VisibleWindowsOn(desktop).LastOrDefault(item => item.IsAppWindow).Window;

    /// <summary>The same windows, seen from another desktop (after switching to it underneath the overlay).</summary>
    public DesktopScene WithCurrent(int desktop) => new(Items, Desktops, desktop, Stats);

    /// <summary>Whether DWM is showing the window (not cloaked), or null if there's no window to ask about.</summary>
    public static bool? IsShown(IntPtr window) =>
        window != IntPtr.Zero && DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloak, sizeof(int)) == 0 ? cloak == 0 : null;

    public static string TitleOf(IntPtr window)
    {
        var title = new StringBuilder(256);
        GetWindowTextW(window, title, title.Capacity);
        return title.ToString();
    }

    public static DesktopScene Capture(VirtualDesktopInfo? desktops, WindowDesktopLookup lookup, IReadOnlyCollection<IntPtr> exclude)
    {
        var known = desktops is { Index: >= 0 };
        var current = known ? desktops!.Index : 0;
        var items = new List<SceneItem>();
        var className = new StringBuilder(64);
        int shellCloaked = 0, unknownDesktop = 0, otherCloaked = 0;
        var appsDark = AppsUseDarkTheme();

        var topToBottom = TopLevelWindows();
        for (var i = topToBottom.Count - 1; i >= 0; i--)
        {
            var window = topToBottom[i];
            if (exclude.Contains(window) || !IsWindowVisible(window)) continue;
            // Windows that block screen capture (recording toolbars, protected video) only ever mirror as black boxes.
            if (GetWindowDisplayAffinity(window, out var affinity) && affinity != 0) continue;
            var minimized = IsIconic(window);
            Rectangle bounds, frame;
            if (minimized)
            {
                if (!IsAppWindow(window) || RestoredBounds(window) is not { } restored) continue;
                bounds = frame = restored;
            }
            else
            {
                if (!GetWindowRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
                bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                frame = DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT f, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0
                    ? Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom)
                    : bounds;
            }

            className.Clear();
            GetClassNameW(window, className, className.Capacity);
            switch (className.ToString())
            {
                case "Progman" or "WorkerW": // wallpaper and desktop icons, shared by every desktop
                    items.Add(new SceneItem(window, bounds, frame, SceneKind.Wallpaper, -1, false));
                    continue;
                case "Shell_TrayWnd" or "Shell_SecondaryTrayWnd":
                    items.Add(new SceneItem(window, bounds, frame, SceneKind.Taskbar, -1, false));
                    continue;
            }

            var cloaked = DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloak, sizeof(int)) == 0 ? cloak : -1;
            int desktop;
            if (cloaked == 0)
            {
                desktop = current;
            }
            else if (cloaked == DWM_CLOAKED_SHELL && known)
            {
                shellCloaked++;
                var id = lookup.GetDesktopId(window);
                desktop = id is { } found ? IndexOf(desktops!.Ids, found) : -1;
                if (desktop < 0)
                {
                    unknownDesktop++;
                    continue;
                }
            }
            else
            {
                if (cloaked != 0) otherCloaked++;
                continue;
            }

            var topmost = (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            items.Add(new SceneItem(window, bounds, frame, SceneKind.Window, desktop, IsAppWindow(window), minimized, IsDark(window, appsDark), topmost));
        }

        var stats = $"shell-cloaked {shellCloaked} (desktop unknown {unknownDesktop}), otherwise cloaked {otherCloaked}";
        return new DesktopScene(items, known ? desktops : null, current, stats);
    }

    /// <summary>Dark title bar means a dark app; otherwise fall back to the system's app theme.</summary>
    private static bool IsDark(IntPtr window, bool appsDark) =>
        DwmGetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, out int dark, sizeof(int)) == 0 && dark != 0 || appsDark;

    private static bool AppsUseDarkTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not 1;
    }

    /// <summary>Where a minimized window will reappear, in screen coordinates.</summary>
    private static Rectangle? RestoredBounds(IntPtr window)
    {
        var placement = new WINDOWPLACEMENT { Length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(window, ref placement)) return null;
        var normal = placement.NormalPosition;
        if (normal.Right <= normal.Left || normal.Bottom <= normal.Top) return null;
        // The placement is in workspace coordinates, which are offset by the primary work area.
        var origin = Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty;
        var restored = Rectangle.FromLTRB(normal.Left + origin.X, normal.Top + origin.Y, normal.Right + origin.X, normal.Bottom + origin.Y);
        // Minimized from maximized: it comes back filling its monitor's work area, not at its normal position.
        const int WPF_RESTORETOMAXIMIZED = 0x2;
        return (placement.Flags & WPF_RESTORETOMAXIMIZED) != 0 ? Screen.FromRectangle(restored).WorkingArea : restored;
    }

    /// <summary>Alt+Tab's rule of thumb: unowned and not a tool window, unless it explicitly asks for a taskbar button.</summary>
    private static bool IsAppWindow(IntPtr window)
    {
        var style = GetWindowLongW(window, GWL_EXSTYLE);
        if ((style & WS_EX_APPWINDOW) != 0) return true;
        return (style & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) == 0 && GetWindow(window, GW_OWNER) == IntPtr.Zero;
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] == id) return i;
        return -1;
    }

    private static List<IntPtr> TopLevelWindows()
    {
        var windows = new List<IntPtr>(256);
        EnumWindows((window, _) =>
        {
            windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
