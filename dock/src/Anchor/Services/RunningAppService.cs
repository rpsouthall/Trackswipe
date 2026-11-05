using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Anchor.Interop;
using Anchor.Models;

namespace Anchor.Services;

/// <summary>
/// One app with at least one open window, as the taskbar would show it.
/// </summary>
/// <param name="Key">What makes two windows "the same app": the AppUserModelID of a packaged app, otherwise the full path of its executable.</param>
/// <param name="ExePath">The executable behind it, or null when only its AppUserModelID is known (a minimized UWP app).</param>
/// <param name="AppId">The AppUserModelID of a packaged app; null for an ordinary desktop app.</param>
/// <param name="Name">What to call it: the Start menu's name for a packaged app, the file description for an exe.</param>
/// <param name="Windows">Its app windows, most recently used first.</param>
/// <param name="Started">When its process started (a FILETIME), so apps already open at startup keep their launch order.</param>
/// <param name="ProcessIds">The processes behind its windows — a Store app's own, never the ApplicationFrameHost that draws its frame.</param>
public sealed record RunningApp(string Key, string? ExePath, string? AppId, string Name, IReadOnlyList<nint> Windows, long Started,
    IReadOnlyList<uint>? ProcessIds = null)
{
    /// <summary>The window a click brings forward: the one used most recently.</summary>
    public nint Window => Windows[0];

    /// <summary>What a dock item for this app launches: the Start menu entry of a packaged app, otherwise the exe.</summary>
    public string LaunchTarget => AppId is not null ? DockItemFactory.AppsFolderPrefix + AppId : ExePath ?? "";
}

/// <summary>
/// Answers "which apps are open, and which windows are theirs?" — the data behind the running-app
/// indicators, click-to-focus, and the dock's section of open apps that aren't pinned.
/// <para>
/// Works the way the taskbar does: enumerate the top-level windows a user would call app windows,
/// map each to the app that owns it, and group them. A desktop app is identified by the full path
/// of its executable — matching on the file name alone would confuse two apps that both ship an
/// <c>Update.exe</c>. A packaged (Store) app is identified by its AppUserModelID, since its
/// windows may be hosted by <c>ApplicationFrameHost.exe</c> rather than by its own process.
/// </para>
/// </summary>
public static class RunningAppService
{
    /// <summary>
    /// Shell-owned windows that are always present (or appear with Start, Search and the like) and
    /// would otherwise make File Explorer read as permanently running, or flash shell components
    /// into the dock.
    /// </summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman",       // the desktop
        "WorkerW",       // the desktop's wallpaper/icon layers
        "Shell_TrayWnd", // the taskbar
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", // top-level ones are shell surfaces; real UWP apps live in an ApplicationFrameWindow
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "Shell_InputSwitchTopLevelWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
    };

    /// <summary>Shell components that put up ordinary-looking windows but aren't apps anyone opened.</summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost.exe",
        "SearchApp.exe",
        "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe",
        "ShellHost.exe",
        "TextInputHost.exe",
        "LockApp.exe",
        "ApplicationFrameHost.exe", // only ever a host; its windows are attributed to the app inside
    };

    private const string FrameClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Names already worked out, by app key: the lookup reads version resources or the shell namespace.</summary>
    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Restricted">Found without opening the process (it runs as SYSTEM or another user).</param>
    private sealed record ProcessInfo(string? Path, string? AppId, long Started, bool Restricted = false);

    /// <summary>
    /// What is open now, one entry per app, ordered by its most recently used window. Safe to call
    /// from a background thread; never throws.
    /// </summary>
    public static List<RunningApp> Snapshot()
    {
        var apps = new Dictionary<string, (string? Exe, string? AppId, long Started, List<nint> Windows)>(StringComparer.OrdinalIgnoreCase);
        var processIds = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        // Several windows normally belong to one process, and opening it is the expensive part.
        var processes = new Dictionary<uint, ProcessInfo>();
        NativeMethods.IVirtualDesktopManager? desktops = null;
        try
        {
            desktops = (NativeMethods.IVirtualDesktopManager)new NativeMethods.VirtualDesktopManager();
        }
        catch (Exception ex)
        {
            Diag.Log("RunningAppService: no virtual desktop manager: " + ex.Message);
        }

        try
        {
            var className = new StringBuilder(256);
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsAppWindow(hwnd, className, desktops))
                        return true;

                    string? exe, appId;
                    long started;
                    nint processWindow = hwnd; // the window whose process is the app's own
                    if (className.ToString() == FrameClass)
                    {
                        // A UWP app's frame belongs to ApplicationFrameHost; the app itself owns the
                        // CoreWindow inside it. Minimized, the CoreWindow is detached, and all that
                        // is left to go on is the AppUserModelID the frame carries for the taskbar.
                        nint core = NativeMethods.FindWindowEx(hwnd, nint.Zero, CoreWindowClass, null);
                        processWindow = core;
                        if (core != nint.Zero && OwnerOf(core, processes) is { Path: not null } inside)
                            (exe, appId, started) = (inside.Path, inside.AppId ?? WindowAppId(hwnd), inside.Started);
                        else if (WindowAppId(hwnd) is { } frameAppId)
                            (exe, appId, started) = (null, frameAppId, 0);
                        else
                            return true;
                    }
                    else
                    {
                        if (OwnerOf(hwnd, processes) is not { Path: not null } owner)
                            return true; // our own, or one whose program can't be found at all
                        if (ShellProcesses.Contains(Path.GetFileName(owner.Path)))
                            return true;
                        (exe, appId, started) = (owner.Path, owner.AppId, owner.Started);
                        // A program running as another user (the UAC prompt's consent.exe runs as SYSTEM) has no
                        // name of its own worth showing ("Consent UI for administrative applications"); the
                        // taskbar shows its window's title ("User Account Control"), and so does the dock.
                        if (owner.Restricted && !Names.ContainsKey(exe) && WindowTitle(hwnd) is { Length: > 0 } title)
                            Names.TryAdd(exe, title);
                    }

                    if (appId is null && exe is null)
                        return true;
                    string key = appId ?? exe!;
                    if (!apps.TryGetValue(key, out var app))
                    {
                        app = (exe, appId, started, new List<nint>());
                        apps[key] = app;
                        order.Add(key);
                    }
                    else if (app.Exe is null && exe is not null)
                    {
                        // The minimized frame came first; a live window has since told us the exe.
                        apps[key] = app = (exe, app.AppId, started, app.Windows);
                    }
                    // Enumeration runs front to back, so the first window is the most recently used.
                    app.Windows.Add(hwnd);
                    if (processWindow != nint.Zero && NativeMethods.GetWindowThreadProcessId(processWindow, out uint pid) != 0 && pid != 0)
                    {
                        if (!processIds.TryGetValue(key, out var set))
                            processIds[key] = set = new HashSet<uint>();
                        set.Add(pid);
                    }
                }
                catch
                {
                    // A window can die mid-enumeration; skip it rather than abandoning the walk.
                }
                return true;
            }, nint.Zero);
        }
        catch (Exception ex)
        {
            Diag.Log("RunningAppService: enumeration failed: " + ex.Message);
        }
        finally
        {
            if (desktops is not null)
                Marshal.ReleaseComObject(desktops);
        }

        var result = new List<RunningApp>(order.Count);
        foreach (var key in order)
        {
            var (exe, appId, started, windows) = apps[key];
            result.Add(new RunningApp(key, exe, appId, NameOf(key, exe, appId), windows, started,
                processIds.TryGetValue(key, out var ids) ? ids.ToArray() : Array.Empty<uint>()));
        }
        return result;
    }

    /// <summary>
    /// True for the windows the taskbar gives a button to: visible and on screen (or on another
    /// virtual desktop), titled, not a tool window or an owned dialog unless it asks to be on the
    /// taskbar, and not one of the shell's own fixtures.
    /// </summary>
    private static bool IsAppWindow(nint hwnd, StringBuilder className, NativeMethods.IVirtualDesktopManager? desktops)
    {
        className.Clear();
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != nint.Zero)
                return false; // a dialog or palette belonging to another window
            if ((ex & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
                return false; // deliberately off the taskbar — including Anchor's own dock
        }
        if (NativeMethods.GetWindowTextLength(hwnd) == 0)
            return false; // untitled helper surfaces

        if (NativeMethods.GetClassName(hwnd, className, className.Capacity) > 0)
        {
            var cls = className.ToString();
            if (ShellWindowClasses.Contains(cls) || cls.StartsWith("XamlExplorerHostIslandWindow", StringComparison.Ordinal))
                return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
        {
            // Hidden by the shell because it is on another virtual desktop is still open; hidden
            // by its own app (a suspended Store app, a prelaunched one) is not.
            if (cloaked != NativeMethods.DWM_CLOAKED_SHELL || desktops is null)
                return false;
            if (desktops.GetWindowDesktopId(hwnd, out var desktop) != 0 || desktop == Guid.Empty)
                return false;
        }
        return true;
    }

    /// <summary>Who owns a window: its process's image path, AppUserModelID if packaged, and start time. Null for our own process.</summary>
    private static ProcessInfo? OwnerOf(nint hwnd, Dictionary<uint, ProcessInfo> processes)
    {
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0 || pid == OwnProcessId)
            return null;
        if (processes.TryGetValue(pid, out var info))
            return info;

        info = new ProcessInfo(null, null, 0);
        nint handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != nint.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                string? path = NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
                long started = NativeMethods.GetProcessTimes(handle, out long creation, out _, out _, out _) ? creation : 0;
                info = new ProcessInfo(path, PackagedAppId(handle), started);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        else if (ImagePathWithoutOpening(pid) is { } path)
        {
            // Not ours to open — a program running as SYSTEM or another user. The taskbar lists its windows all the
            // same, and one of them matters a great deal: a UAC prompt for an app in the background waits as a
            // flashing taskbar button (consent.exe's), and with the taskbar's buttons hidden the dock is where it shows.
            info = new ProcessInfo(path, null, 0, Restricted: true);
        }
        processes[pid] = info; // unreadable (protected) is remembered too
        return info;
    }

    /// <summary>
    /// A process's image path without opening it, the way Task Manager reads it for processes it may not open: by
    /// process ID from the system's process list, then from a device path (\Device\HarddiskVolume3\…) to a drive letter.
    /// </summary>
    private static unsafe string? ImagePathWithoutOpening(uint pid)
    {
        const int SystemProcessIdInformation = 0x58;
        const int Capacity = 1024;
        char* buffer = stackalloc char[Capacity];
        var query = new NativeMethods.SYSTEM_PROCESS_ID_INFORMATION
        {
            ProcessId = (nint)pid,
            MaximumLength = Capacity * sizeof(char),
            Buffer = (nint)buffer,
        };
        if (NativeMethods.NtQuerySystemInformation(SystemProcessIdInformation, &query,
                (uint)sizeof(NativeMethods.SYSTEM_PROCESS_ID_INFORMATION), out _) != 0 || query.Length == 0)
            return null;
        var devicePath = new string(buffer, 0, query.Length / sizeof(char));
        foreach (var (device, drive) in DriveDevices())
            if (devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase) && devicePath.Length > device.Length && devicePath[device.Length] == '\\')
                return drive + devicePath[device.Length..];
        return null;
    }

    private static volatile (string Device, string Drive)[]? _driveDevices;

    /// <summary>Each drive letter's device ("C:" is \Device\HarddiskVolume3), read once.</summary>
    private static unsafe (string Device, string Drive)[] DriveDevices()
    {
        if (_driveDevices is { } known)
            return known;
        var list = new List<(string, string)>();
        char* target = stackalloc char[512];
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd('\\');
            if (NativeMethods.QueryDosDevice(drive, target, 512) > 0 && new string(target) is { Length: > 0 } device)
                list.Add((device, drive));
        }
        return _driveDevices = list.ToArray();
    }

    private static string? WindowTitle(nint hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
            return null;
        var text = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : null;
    }

    /// <summary>The AppUserModelID of a packaged process, or null for an ordinary desktop one.</summary>
    private static string? PackagedAppId(nint process)
    {
        uint length = 0;
        if (NativeMethods.GetApplicationUserModelId(process, ref length, null) != NativeMethods.ERROR_INSUFFICIENT_BUFFER || length == 0)
            return null;
        var buffer = new char[length];
        return NativeMethods.GetApplicationUserModelId(process, ref length, buffer) == 0
            ? new string(buffer, 0, (int)length - 1)
            : null;
    }

    /// <summary>The AppUserModelID set on a window for the taskbar, if any.</summary>
    public static string? WindowAppId(nint hwnd)
    {
        var iid = NativeMethods.IID_IPropertyStore;
        if (NativeMethods.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null)
            return null;
        try
        {
            var key = NativeMethods.PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out var value) != 0)
                return null;
            try
            {
                return value.vt == NativeMethods.VT_LPWSTR && value.pointer != nint.Zero
                    ? Marshal.PtrToStringUni(value.pointer) is { Length: > 0 } id ? id : null
                    : null;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>A friendly name: Start's own for a packaged app, the exe's file description otherwise.</summary>
    private static string NameOf(string key, string? exe, string? appId) => Names.GetOrAdd(key, _ =>
    {
        try
        {
            if (appId is not null && ShellDisplayName(DockItemFactory.AppsFolderPrefix + appId) is { } shellName)
                return shellName;
            if (exe is not null)
            {
                var version = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(version.FileDescription))
                    return version.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(version.ProductName))
                    return version.ProductName.Trim();
                return Path.GetFileNameWithoutExtension(exe);
            }
        }
        catch (Exception ex)
        {
            Diag.Log("RunningAppService: naming failed: " + ex.Message);
        }
        return appId ?? Path.GetFileNameWithoutExtension(exe) ?? key;
    });

    private static string? ShellDisplayName(string parsingName)
    {
        if (NativeMethods.SHParseDisplayName(parsingName, nint.Zero, out nint pidl, 0, out _) != 0 || pidl == nint.Zero)
            return null;
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            return NativeMethods.SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                       NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_DISPLAYNAME) != 0 &&
                   !string.IsNullOrWhiteSpace(info.szDisplayName)
                ? info.szDisplayName
                : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// The executable a dock item would start, for comparison against a snapshot: the target
    /// itself for an <c>.exe</c>, or a shortcut's resolved target. Null for anything that has no
    /// single executable behind it (folders, web links, separators, shell commands, Store apps).
    /// </summary>
    public static string? ExecutablePathOf(DockItem item)
    {
        if (item.Kind != DockItemKind.Application || string.IsNullOrWhiteSpace(item.Target))
            return null;

        var target = item.Target;
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            target = ShortcutResolver.Resolve(target) ?? string.Empty;

        if (target.Length == 0 ||
            !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // Normalize so "C:\Windows\..\Windows\notepad.exe" and the snapshot's path compare
            // equal; the lookups are already case-insensitive.
            return Path.GetFullPath(target);
        }
        catch
        {
            return target;
        }
    }

    /// <summary>The AppUserModelID of a Start-menu item (<c>shell:AppsFolder\…</c>), or null.</summary>
    public static string? AppIdOf(DockItem item) =>
        item.Kind == DockItemKind.Application && DockItemFactory.IsAppsFolderTarget(item.Target)
            ? item.Target[DockItemFactory.AppsFolderPrefix.Length..] is { Length: > 0 } id ? id : null
            : null;

    /// <summary>
    /// Brings an existing window to the front, restoring it first if it is minimized. Returns
    /// false when the window has gone away or Windows refused the activation, so the caller can
    /// fall back to launching a fresh instance.
    /// </summary>
    public static bool Activate(nint hwnd)
    {
        if (hwnd == nint.Zero || !NativeMethods.IsWindow(hwnd))
            return false;
        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                // Windows refuses that for a window of a program running with more rights than the dock — the UAC
                // prompt waiting for an app in the background is one, minimized off-screen — but switches to it the
                // way Alt+Tab and the taskbar do, which is what brings that prompt up.
                if (NativeMethods.IsIconic(hwnd))
                {
                    NativeMethods.SwitchToThisWindow(hwnd, true);
                    return true;
                }
            }
            // Allowed without the usual foreground restrictions because Anchor is the foreground
            // process at this point: the click that got us here activated the dock.
            return NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Diag.Log("RunningAppService: activation failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Minimizes every one of an app's windows that is on screen now.</summary>
    public static void Minimize(RunningApp app)
    {
        foreach (var hwnd in app.Windows)
        {
            if (NativeMethods.IsWindow(hwnd) && !NativeMethods.IsIconic(hwnd) && IsOnScreen(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        }
    }

    /// <summary>Asks each of an app's windows to close, as its own close button would — it can still ask to save.</summary>
    public static void Quit(RunningApp app)
    {
        foreach (var hwnd in app.Windows)
        {
            if (NativeMethods.IsWindow(hwnd))
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, nint.Zero, nint.Zero);
        }
    }

    /// <summary>
    /// Ends the app's processes outright, as Task Manager does — unsaved work is lost, which is
    /// the point of the command: an app that won't close. Each process with its children, so a
    /// browser's helpers go with it. One that refuses (elevated, protected) is logged and skipped.
    /// </summary>
    public static void EndTask(RunningApp app)
    {
        foreach (var pid in app.ProcessIds ?? Array.Empty<uint>())
        {
            if (pid == OwnProcessId)
                continue;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (ShellProcesses.Contains(Path.GetFileName(process.MainModule?.FileName ?? "")) ||
                    string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                    continue; // never the shell: ending Explorer takes the taskbar and desktop with it
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Diag.Log($"RunningAppService: ending process {pid} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Not cloaked: on this virtual desktop rather than another.</summary>
    private static bool IsOnScreen(nint hwnd) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
}
