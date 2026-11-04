using System.Runtime.InteropServices;

namespace Anchor.Interop;

/// <summary>
/// Thin Win32 / DWM interop surface. Kept small and focused on what the dock needs:
/// rounded window corners, always-on-top enforcement, tool-window styling, and
/// cursor probing for edge-reveal / auto-hide.
/// </summary>
internal static partial class NativeMethods
{
    // ---- DWM window attributes --------------------------------------------
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_BORDER_COLOR = 34;

    // DWM_WINDOW_CORNER_PREFERENCE
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;       // full ~8px Win11 window radius
    public const int DWMWCP_ROUNDSMALL = 3;  // subtle ~4px radius (menus/tooltips)

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // ---- Window Z-order / positioning -------------------------------------
    public static readonly nint HWND_TOPMOST = new(-1);
    public static readonly nint HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint hwndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    // ---- Extended window styles (tool window: off taskbar & alt-tab) ------
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    // ---- Window styles (strip the non-client frame / resize border) -------
    public const int GWL_STYLE = -16;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;
    public const long WS_BORDER = 0x00800000;
    public const long WS_DLGFRAME = 0x00400000;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    // ---- Cursor / hit testing (auto-hide reveal) --------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    // ---- DPI (DIP -> physical pixel conversion for exact placement) -------
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    // ---- Mouse button state (timer-polled dragging) -----------------------
    public const int VK_LBUTTON = 0x01;

    // Modifier keys, read the same way while capturing a hotkey: XAML's KeyDown reports the
    // pressed key but not which modifiers are held with it, and the Windows key never reaches
    // the app as a KeyDown at all.
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12; // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    // ---- Screen <-> client coordinate conversion (timer-polled dragging) --
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref POINT point);

    // ---- Shell icon extraction (SHGetFileInfo) -----------------------------
    // A fallback icon source for anything the filesystem says exists. Needed because the
    // WinRT Storage thumbnail pipeline (IconService's primary path, for higher-res icons)
    // flatly refuses to open .lnk shortcuts ("UNABLE_TO_MASK_PATH") and can deny arbitrary
    // paths for an unpackaged app; SHGetFileInfo has neither limitation and, for a .lnk,
    // naturally resolves to the shortcut target's icon with the arrow overlay, matching
    // Explorer. Classic DllImport here (not LibraryImport) since the fixed-size string
    // fields in SHFILEINFO need ByValTStr marshalling.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x100;
    public const uint SHGFI_LARGEICON = 0x0;
    public const uint SHGFI_SYSICONINDEX = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    // ---- Jumbo (256x256) shell icons (SHGetImageList) ----------------------
    // The crisp, Explorer-matching icon source for IconService: SHGetFileInfo with
    // SHGFI_SYSICONINDEX resolves a path to its index in the shell's system image list, and
    // SHGetImageList(SHIL_JUMBO, ...) hands back that list at its largest (256x256) resolution.
    // This is pure Win32 — no WinRT Storage broker involved — so it works for any path a
    // full-trust process can see, without needing the broadFileSystemAccess capability.
    public const int SHIL_EXTRALARGE = 0x2;
    public const int SHIL_JUMBO = 0x4;
    public const int ILD_TRANSPARENT = 0x1;

    public static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IImageList
    {
        [PreserveSig] int Add(nint hbmImage, nint hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, nint hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, nint hbmImage, nint hbmMask);
        [PreserveSig] int AddMasked(nint hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(nint pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out nint picon);
        [PreserveSig] int GetImageInfo(int i, nint pImageInfo);
        [PreserveSig] int Copy(int iDst, IImageList punkSrc, int iSrc, int uFlags);
        [PreserveSig] int Merge(int i1, IImageList punk2, int i2, int dx, int dy, ref Guid riid, out nint ppv);
        [PreserveSig] int Clone(ref Guid riid, out nint ppv);
        [PreserveSig] int GetImageRect(int i, nint prc);
        [PreserveSig] int GetIconSize(out int cx, out int cy);
        [PreserveSig] int SetIconSize(int cx, int cy);
        [PreserveSig] int GetImageCount(out int pi);
        [PreserveSig] int SetImageCount(int uNewCount);
        [PreserveSig] int SetBkColor(int clrBk, out int pclr);
        [PreserveSig] int GetBkColor(out int pclr);
        [PreserveSig] int BeginDrag(int iTrack, int dxHotspot, int dyHotspot);
        [PreserveSig] int EndDrag();
        [PreserveSig] int DragEnter(nint hwndLock, int x, int y);
        [PreserveSig] int DragLeave(nint hwndLock);
        [PreserveSig] int DragMove(int x, int y);
        [PreserveSig] int SetDragCursorImage(IImageList punk, int iDrag, int dxHotspot, int dyHotspot);
        [PreserveSig] int DragShowNolock(int fShow);
        [PreserveSig] int GetDragImage(nint ppt, nint pptHotspot, ref Guid riid, out nint ppv);
        [PreserveSig] int GetItemFlags(int i, out int dwFlags);
        [PreserveSig] int GetOverlayImage(int iOverlay, out int piIndex);
    }

    [DllImport("shell32.dll")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    // ---- Shell items that are not files (Store apps) -----------------------
    //
    // An app dropped from the Start menu may have no file behind it at all: a Store app is an
    // item in the virtual "AppsFolder" namespace, identified by an AppUserModelID. The dock
    // stores those as "shell:AppsFolder\<id>", which ShellExecute launches — but which no API
    // that takes a path will answer for, so the two below go through the item ID list the shell
    // parses that name into instead (see Services/ShellDrop.cs and IconService).

    /// <summary>Turns a parsing name ("shell:AppsFolder\Chrome", or a path) into an ITEMIDLIST.
    /// The caller owns what comes back: free it with <c>Marshal.FreeCoTaskMem</c>.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(
        string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    /// <summary>Pass an ITEMIDLIST rather than a path as SHGetFileInfo's first argument.</summary>
    public const uint SHGFI_PIDL = 0x8;

    /// <summary>SHGetFileInfo against an ITEMIDLIST (with <see cref="SHGFI_PIDL"/>) instead of a
    /// path — the only way to reach the icon of an item that has no path.</summary>
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfoPidl(
        nint pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // ---- Hidden helper window (tray callbacks + global hotkeys) ------------
    //
    // Both the notification-area icon and RegisterHotKey deliver their events as window messages,
    // and a WinUI 3 Window exposes no WndProc to receive them. So Anchor owns one tiny hidden
    // HWND of its own (see Services/MessageWindow.cs) purely as a message sink. It is a real
    // (never-shown) popup rather than an HWND_MESSAGE child, because TrackPopupMenuEx needs an
    // owner that can be made the foreground window or the tray menu won't light-dismiss.

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    public const long WS_POPUP = 0x80000000L;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
        int exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string message);

    public const uint WM_APP = 0x8000;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_NULL = 0x0000;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_CONTEXTMENU = 0x007B;

    // ---- Notification-area (tray) icon -------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_SHOWTIP = 0x00000080;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    // ---- Icon loading (the exe's own icon, at the shell's small-icon size) --

    public const uint IMAGE_ICON = 1;
    public const uint LR_DEFAULTCOLOR = 0x0000;
    public const uint LR_SHARED = 0x8000;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    /// <summary>Resource id the .NET SDK gives an <c>&lt;ApplicationIcon&gt;</c> (IDI_APPLICATION).</summary>
    public static readonly nint IDI_APPLICATION = 32512;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImage(nint instance, nint name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int iconIndex, out nint large, out nint small, uint icons);

    // ---- Native popup menu (the tray context menu) -------------------------
    //
    // A native menu rather than a XAML MenuFlyout: a flyout needs a XamlRoot to hang off, and the
    // tray icon's owner is a plain HWND with no XAML content at all.

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    // ---- Foreground / show (bring the dock to the front) -------------------

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hwnd, int cmdShow);

    // ---- Top-level window / process enumeration (running-app indicators) ---
    //
    // "Is this pinned app running?" is answered the same way the taskbar answers it: walk the
    // visible top-level windows, map each back to the process that owns it, and compare that
    // process's image path with the item's target. There is no cheaper API for it — a process
    // name alone is ambiguous (two "Update.exe" in different folders are different apps) and
    // Process.GetProcesses() can't tell a background service from something with a window.

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hwnd);

    /// <summary>True when the window is minimized — it must be restored before it can be focused.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hwnd);

    /// <summary>GetWindow relationship: the window's owner (zero for a true top-level window).</summary>
    public const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hwnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>Enough access to read a process's image path, and grantable without elevation.</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(
        nint process, uint flags, System.Text.StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    /// <summary>ShowWindow command: restore a minimized window to its previous size and position.</summary>
    public const int SW_RESTORE = 9;

    // ---- Global hotkeys ----------------------------------------------------

    /// <summary>Ask Windows not to auto-repeat a held-down hotkey.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hwnd, int id);

    // ---- Package identity --------------------------------------------------
    //
    // The one reliable way to ask "am I running from an MSIX package?" without throwing: the
    // WinRT alternative (Package.Current) raises an exception when there is no package, and an
    // exception is a poor answer to a question this is asked at startup. Called with a zero
    // length and no buffer it does no work at all — it only reports which of the two error codes
    // below applies.

    /// <summary>The process has no package identity — i.e. it is a plain unpackaged exe.</summary>
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>There is a package, and its name didn't fit the (deliberately empty) buffer.</summary>
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength, [Out] char[]? packageFullName);
}
