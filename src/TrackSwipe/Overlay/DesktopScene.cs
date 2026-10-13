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
/// <see cref="NeedsBacking"/> windows leave parts of themselves to a system backdrop that thumbnails don't include.
/// </summary>
internal readonly record struct SceneItem(IntPtr Window, Rectangle Bounds, Rectangle Frame, SceneKind Kind, int Desktop, bool IsAppWindow,
    bool IsMinimized = false, bool IsDark = true, bool IsTopmost = false, bool NeedsBacking = false);

/// <summary>
/// Every desktop's windows as one bottom-to-top list. Windows on other desktops are cloaked by
/// the shell, but DWM keeps rendering them, so their thumbnails stay live.
/// </summary>
internal sealed class DesktopScene
{
    private const int DWM_CLOAKED_SHELL = 2;
    private static readonly RestoreFrameHistory RestoreFrames = new();

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

    /// <summary>
    /// A layered window faded to nothing: invisible on screen, but DWM thumbnails ignore a window's layered
    /// alpha and draw its content anyway. WebView2 apps (WhatsApp) keep one such window over their content,
    /// with no virtual desktop of its own, so it would otherwise show on every desktop.
    /// </summary>
    private static bool IsFullyTransparent(IntPtr window) =>
        (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_LAYERED) != 0
        && GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && (flags & LWA_ALPHA) != 0 && alpha == 0;

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
        int shellCloaked = 0, unknownDesktop = 0, otherCloaked = 0, transparent = 0;
        var appsDark = AppsUseDarkTheme();

        var topToBottom = TopLevelWindows();
        RestoreFrames.Prune(topToBottom);
        for (var i = topToBottom.Count - 1; i >= 0; i--)
        {
            var window = topToBottom[i];
            if (exclude.Contains(window) || !IsWindowVisible(window)) continue;
            // Windows that block screen capture (recording toolbars, protected video) only ever mirror as black boxes.
            if (GetWindowDisplayAffinity(window, out var affinity) && affinity != 0) continue;
            if (IsFullyTransparent(window))
            {
                transparent++;
                continue;
            }
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
                LearnBorders(window, bounds, frame);
                var placement = new WINDOWPLACEMENT { Length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(window, ref placement))
                {
                    var normal = placement.NormalPosition;
                    GetWindowThreadProcessId(window, out var process);
                    RestoreFrames.Remember(window, Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom), frame, process);
                }
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
                // UWP apps (Settings, Calculator, ...) are a frame (ApplicationFrameWindow) hosting a CoreWindow.
                // When the app is out of view, Windows detaches the CoreWindow into a top-level window at an
                // unrelated position, so mirroring it paints the app a second time in the wrong place. The
                // frame's thumbnail already includes the content; like Alt+Tab, mirror only the frame. This also
                // keeps out shell CoreWindows such as the full-screen touch keyboard host.
                case "Windows.UI.Core.CoreWindow":
                    continue;
            }

            var cloaked = DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloak, sizeof(int)) == 0 ? cloak : -1;
            var shellHidden = cloaked >= 0 && (cloaked & DWM_CLOAKED_SHELL) != 0;
            if (shellHidden) shellCloaked++;
            else if (cloaked != 0) otherCloaked++;
            // Ask about visible windows too: being uncloaked doesn't prove desktop ownership.
            // Owned popups can lack an ID of their own; their owner supplies the desktop instead.
            Guid? id = null;
            if (known && (cloaked == 0 || shellHidden))
            {
                id = lookup.GetDesktopId(window);
                var owner = GetWindow(window, GW_OWNER);
                for (var depth = 0; (id is null || id == Guid.Empty) && owner != IntPtr.Zero && depth < 16; depth++)
                {
                    id = lookup.GetDesktopId(owner);
                    owner = GetWindow(owner, GW_OWNER);
                }
            }
            var desktop = DesktopMembership.Resolve(cloaked, current, known ? desktops!.Ids : null, id);
            if (desktop < 0)
            {
                if (shellHidden) unknownDesktop++;
                continue;
            }

            var exStyle = GetWindowLongW(window, GWL_EXSTYLE);
            var appWindow = IsAppWindow(window);
            items.Add(new SceneItem(window, bounds, frame, SceneKind.Window, desktop, appWindow, minimized, IsDark(window, appsDark),
                IsTopmost: (exStyle & WS_EX_TOPMOST) != 0, NeedsBacking: appWindow && UsesSystemBackdrop(window, exStyle, className.ToString())));
        }

        var stats = $"shell-cloaked {shellCloaked} (desktop unknown {unknownDesktop}), otherwise cloaked {otherCloaked}, fully transparent (skipped) {transparent}";
        return new DesktopScene(items, known ? desktops : null, current, stats);
    }

    /// <summary>
    /// Whether parts of the window are left to a system backdrop (Mica, Acrylic) that DWM thumbnails don't
    /// include, so its thumbnail needs a solid backing. Ordinary GDI apps paint every pixel themselves and
    /// skip the extra thumbnail. Covers windows that ask DWM for a backdrop, windows that draw only through
    /// DirectComposition (browsers, Electron, WinUI 3), and UWP frames.
    /// </summary>
    private static bool UsesSystemBackdrop(IntPtr window, int exStyle, string className)
    {
        const int DWMSBT_MAINWINDOW = 2, DWMSBT_TABBEDWINDOW = 4;
        if (DwmGetWindowAttribute(window, DWMWA_SYSTEMBACKDROP_TYPE, out int backdrop, sizeof(int)) == 0
            && backdrop is >= DWMSBT_MAINWINDOW and <= DWMSBT_TABBEDWINDOW) return true;
        return (exStyle & WS_EX_NOREDIRECTIONBITMAP) != 0
            || className is "WinUIDesktopWin32WindowClass" or "ApplicationFrameWindow";
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
        GetWindowThreadProcessId(window, out var process);
        if (RestoreFrames.Find(window, Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom), process) is { } previous)
            return previous;
        // The placement is in workspace coordinates, which are offset by the primary work area.
        var origin = Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty;
        var restored = Rectangle.FromLTRB(normal.Left + origin.X, normal.Top + origin.Y, normal.Right + origin.X, normal.Bottom + origin.Y);
        // Minimized from maximized: it comes back filling its monitor's work area, not at its normal position.
        const int WPF_RESTORETOMAXIMIZED = 0x2;
        if ((placement.Flags & WPF_RESTORETOMAXIMIZED) != 0) return Screen.FromRectangle(restored).WorkingArea;
        // The normal position includes the invisible resize borders; the visible frame is what lands.
        (int Left, int Top, int Right, int Bottom) border = default;
        bool known;
        lock (Borders) known = Borders.TryGetValue(GetDpiForWindow(window), out border);
        if (known && Resizable(window))
            restored = Rectangle.FromLTRB(restored.Left + border.Left, restored.Top + border.Top, restored.Right - border.Right, restored.Bottom - border.Bottom);
        return restored;
    }

    /// <summary>
    /// The invisible resize borders of resizable windows, by DPI, as last seen on a window on screen. They're the same
    /// for nearly every such window at a given DPI, so a minimized window that hasn't been seen restored borrows them.
    /// </summary>
    private static readonly Dictionary<uint, (int Left, int Top, int Right, int Bottom)> Borders = new();

    private static void LearnBorders(IntPtr window, Rectangle bounds, Rectangle frame)
    {
        if (!Resizable(window) || frame == bounds || !bounds.Contains(frame)) return;
        var border = (frame.Left - bounds.Left, frame.Top - bounds.Top, bounds.Right - frame.Right, bounds.Bottom - frame.Bottom);
        if (border.Item1 > 32 || border.Item2 > 32 || border.Item3 > 32 || border.Item4 > 32) return; // a custom frame, not borders
        lock (Borders) Borders[GetDpiForWindow(window)] = border;
    }

    private static bool Resizable(IntPtr window) => (GetWindowLongW(window, -16 /* GWL_STYLE */) & 0x00040000 /* WS_THICKFRAME */) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

    /// <summary>Alt+Tab's rule of thumb: unowned and not a tool window, unless it explicitly asks for a taskbar button.</summary>
    private static bool IsAppWindow(IntPtr window)
    {
        var style = GetWindowLongW(window, GWL_EXSTYLE);
        if ((style & WS_EX_APPWINDOW) != 0) return true;
        return (style & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) == 0 && GetWindow(window, GW_OWNER) == IntPtr.Zero;
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
