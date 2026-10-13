using System.Runtime.InteropServices;

namespace TrackSwipe.Interop;

/// <summary>
/// Where each window's taskbar button is, so a window can shrink into it the way Windows minimizes it. The buttons
/// come from UI Automation (Explorer exposes each one with "Appid: ..." as its automation ID); a window is matched
/// by its app ID (its own, or its package's), then by program name against the button's name. Called through raw
/// COM vtables, off the UI thread.
/// </summary>
internal static unsafe class TaskbarButtons
{
    private sealed record Button(string AppId, string Name, Rectangle Bounds);

    /// <summary>Screen rectangles of the taskbar buttons for these windows; windows with no match are left out.</summary>
    public static Dictionary<IntPtr, Rectangle> Find(IReadOnlyCollection<IntPtr> windows)
    {
        var started = Clock.Now;
        var result = new Dictionary<IntPtr, Rectangle>();
        var buttons = ReadButtons();
        foreach (var window in windows)
        {
            if (Match(window, buttons) is { } button) result[window] = button.Bounds;
        }
        Log.Verbose($"Taskbar buttons: {buttons.Count} read, {result.Count} of {windows.Count} windows matched, {(Clock.Now - started) * 1000:0} ms.");
        return result;
    }

    private static Button? Match(IntPtr window, List<Button> buttons)
    {
        // Prefer a button on the same monitor as the window (secondary taskbars can show their own buttons).
        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        Button? Pick(Func<Button, bool> test)
        {
            Button? any = null;
            foreach (var button in buttons)
            {
                if (!test(button)) continue;
                var center = new POINT { X = button.Bounds.X + button.Bounds.Width / 2, Y = button.Bounds.Y + button.Bounds.Height / 2 };
                if (MonitorFromPoint(center, 2) == monitor) return button;
                any ??= button;
            }
            return any;
        }

        if ((WindowAppId(window) ?? ProcessAppId(window)) is { } appId &&
            Pick(button => string.Equals(button.AppId, appId, StringComparison.OrdinalIgnoreCase)) is { } byId) return byId;

        if (ProcessName(window) is { Length: >= 3 } exe)
        {
            // "Figma pinned", "File Explorer pinned", "Microsoft Edge - 2 running windows": compare the app's name part.
            static string AppName(string name)
            {
                var cut = name.IndexOf(" - ", StringComparison.Ordinal);
                if (cut >= 0) name = name[..cut];
                return name.EndsWith(" pinned", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;
            }
            return Pick(button =>
            {
                var app = AppName(button.Name).Replace(" ", "");
                return app.Length >= 3 && (app.Contains(exe, StringComparison.OrdinalIgnoreCase) || exe.Contains(app, StringComparison.OrdinalIgnoreCase));
            });
        }
        return null;
    }

    private static List<Button> ReadButtons()
    {
        var buttons = new List<Button>();
        CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */); // this thread's; harmless if already initialized
        var clsid = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
        var iid = new Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
        if (CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* INPROC_SERVER */, ref iid, out var automation) < 0) return buttons;
        IntPtr condition = IntPtr.Zero;
        try
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(automation, 21))(automation, &condition) < 0) return buttons; // CreateTrueCondition
            if (FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) is var primary && primary != IntPtr.Zero)
                ReadTaskbar(automation, condition, primary, buttons);
            for (var secondary = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); secondary != IntPtr.Zero;
                 secondary = FindWindowExW(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null))
                ReadTaskbar(automation, condition, secondary, buttons);
        }
        finally
        {
            if (condition != IntPtr.Zero) Marshal.Release(condition);
            Marshal.Release(automation);
        }
        return buttons;
    }


    private static void ReadTaskbar(IntPtr automation, IntPtr condition, IntPtr taskbar, List<Button> buttons)
    {
        IntPtr element = IntPtr.Zero, array = IntPtr.Zero;
        try
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(automation, 6))(automation, taskbar, &element) < 0 || element == IntPtr.Zero) return; // ElementFromHandle
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr*, int>)Slot(element, 6))(element, 4 /* TreeScope_Descendants */, condition, &array) < 0 || array == IntPtr.Zero) return; // FindAll
            int length;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(array, 3))(array, &length) < 0) return;
            for (var i = 0; i < length; i++)
            {
                IntPtr item;
                if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(array, 4))(array, i, &item) < 0 || item == IntPtr.Zero) continue;
                try
                {
                    var id = ReadString(item, 29); // get_CurrentAutomationId
                    if (id is null || !id.StartsWith("Appid: ", StringComparison.Ordinal)) continue;
                    RECT rect;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, RECT*, int>)Slot(item, 43))(item, &rect) < 0) continue; // get_CurrentBoundingRectangle
                    if (rect.Right <= rect.Left) continue;
                    buttons.Add(new Button(id[7..], ReadString(item, 23) ?? "", Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom))); // get_CurrentName
                }
                finally { Marshal.Release(item); }
            }
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.Release(array);
            if (element != IntPtr.Zero) Marshal.Release(element);
        }
    }

    private static string? ReadString(IntPtr element, int slot)
    {
        IntPtr bstr;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(element, slot))(element, &bstr) < 0 || bstr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { SysFreeString(bstr); }
    }


    private static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

    /// <summary>The app ID a window declares for itself (System.AppUserModel.ID), if any.</summary>
    private static string? WindowAppId(IntPtr window)
    {
        var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"); // IPropertyStore
        if (SHGetPropertyStoreForWindow(window, ref iid, out var store) < 0 || store == IntPtr.Zero) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            var value = stackalloc byte[24]; // PROPVARIANT
            if (((delegate* unmanaged[Stdcall]<IntPtr, PROPERTYKEY*, byte*, int>)Slot(store, 5))(store, &key, value) < 0) return null; // GetValue
            try { return *(ushort*)value == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(*(IntPtr*)(value + 8)) : null; }
            finally { PropVariantClear(value); }
        }
        finally { Marshal.Release(store); }
    }

    /// <summary>The app ID of the package the window's process runs from, if it's a packaged app.</summary>
    private static string? ProcessAppId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var length = 256u;
            var buffer = stackalloc char[256];
            return GetApplicationUserModelId(process, ref length, buffer) == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)) : null;
        }
        finally { CloseHandle(process); }
    }

    private static string? ProcessName(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var length = 1024u;
            var buffer = stackalloc char[1024];
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref length)) return null;
            return Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)length));
        }
        finally { CloseHandle(process); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint init);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(byte* value);
    [DllImport("oleaut32.dll")] private static extern void SysFreeString(IntPtr bstr);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IntPtr store);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, char* id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref uint length);
}
