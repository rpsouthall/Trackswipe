using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Anchor.Interop;

// The extra Win32 surface the macOS-style running-apps section needs: telling a real app window
// from the shell's own surfaces (cloaking, virtual desktops, UWP frames), naming a window's app
// (AppUserModelIDs), hearing about windows as they come and go (WinEvents), and minimizing or
// closing an app's windows from its dock icon.
internal static partial class NativeMethods
{
    // ---- Cloaking ------------------------------------------------------------
    //
    // A cloaked window is "visible" to IsWindowVisible yet not on screen. The shell cloaks windows
    // on other virtual desktops (DWM_CLOAKED_SHELL) — those are open apps all the same — and apps
    // cloak their own background or suspended surfaces (DWM_CLOAKED_APP), which are not.

    public const int DWMWA_CLOAKED = 14;
    public const int DWM_CLOAKED_APP = 0x1;
    public const int DWM_CLOAKED_SHELL = 0x2;
    public const int DWM_CLOAKED_INHERITED = 0x4;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    /// <summary>A window's visible frame, without the invisible resize borders GetWindowRect includes.</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowRect(nint hwnd, int attribute, out RECT value, int size);

    /// <summary>The shell's virtual desktop manager (<see cref="ComInterop.Create{T}"/> it as an <see cref="IVirtualDesktopManager"/>).</summary>
    public static readonly Guid CLSID_VirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    [GeneratedComInterface]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    public partial interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(nint topLevelWindow, ref Guid desktopId);
    }

    // ---- Window relationships ---------------------------------------------------

    public const long WS_EX_APPWINDOW = 0x00040000;

    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    public const uint GA_ROOTOWNER = 3;

    /// <summary>Switches to a window as Alt+Tab does, restoring it if minimized — allowed even for a more privileged program's window.</summary>
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    // ---- Shell hook: windows asking for attention ---------------------------------
    //
    // What makes a taskbar button flash orange: a window calling FlashWindowEx (a UAC prompt for an app in the
    // background, a chat message, a finished download) is announced as HSHELL_FLASH to every window registered here.

    public const int HSHELL_FLASH = 0x8006;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterShellHookWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeregisterShellHookWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    public const int SW_MINIMIZE = 6;
    public const uint WM_CLOSE = 0x0010;

    // ---- App identity ---------------------------------------------------------

    /// <summary>The AppUserModelID of a packaged process; fails with APPMODEL_ERROR_NO_APPLICATION for an unpackaged one.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetApplicationUserModelId(nint process, ref uint length, [Out] char[]? id);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    public const uint SHGFI_DISPLAYNAME = 0x200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int max);

    /// <summary>SystemProcessIdInformation: a process's image name by ID, for any process, without opening it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESS_ID_INFORMATION
    {
        public nint ProcessId;
        public ushort Length, MaximumLength; // a UNICODE_STRING the caller provides the buffer for
        public nint Buffer;
    }

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQuerySystemInformation(int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial uint QueryDosDevice(string device, char* target, uint max);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>System.AppUserModel.ID: the identity the taskbar groups a window's button under.</summary>
    public static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    /// <summary>Enough of a PROPVARIANT to read a string out of one: the type tag, then the pointer.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public nint pointer;
    }

    public const ushort VT_LPWSTR = 31;

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    public partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>A window's property store, as a COM pointer for <see cref="ComInterop.Take{T}"/>.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, out nint store);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT value);

    // ---- WinEvents --------------------------------------------------------------
    //
    // Windows has no "an app opened its first window" notification, but it does announce every
    // window shown, hidden, destroyed, cloaked and uncloaked, and every change of foreground. Out
    // of context, these arrive as messages on the thread that set the hook — the UI thread — so
    // they need no locking and cost nothing while nothing happens.

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hook);
}
