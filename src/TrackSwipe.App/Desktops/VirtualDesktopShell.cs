using System.Runtime.InteropServices;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Desktops;

// Explorer's internal virtual desktop interfaces, Windows 11 24H2/25H2 layout. These are
// undocumented: Microsoft changes the IIDs whenever the layout changes, so a successful
// QueryService for these exact IIDs is what makes calling into the vtable safe.
// Methods we never call take IntPtr in place of HSTRING/IApplicationView to avoid marshalling.

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IntPtr view);
    Guid GetId();
    IntPtr GetName();
    IntPtr GetWallpaperPath();
    bool IsRemote();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(IntPtr view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IntPtr desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop); // instant, no animation
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int index);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
}

/// <summary>Documented shell interface: which desktop a window belongs to.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
    Guid GetWindowDesktopId(IntPtr topLevelWindow);
    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

/// <summary>Finds which virtual desktop a window is on, using the documented IVirtualDesktopManager.</summary>
internal sealed class WindowDesktopLookup
{
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private IVirtualDesktopManager? _manager;

    public Guid? GetDesktopId(IntPtr window)
    {
        try
        {
            _manager ??= (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidVirtualDesktopManager, throwOnError: true)!)!;
            return _manager.GetWindowDesktopId(window);
        }
        catch (COMException)
        {
            return null; // not an app window, or it closed meanwhile
        }
    }
}

/// <summary>
/// Switches virtual desktops instantly through Explorer, with no animation, so our own
/// slide can be the only animation. Null from <see cref="TryCreate"/> means this Windows
/// build isn't supported and the caller should fall back to the keyboard shortcut.
/// </summary>
internal sealed class DesktopSwitcher
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ClsidVirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    private IVirtualDesktopManagerInternal? _manager;

    private DesktopSwitcher(IVirtualDesktopManagerInternal manager) => _manager = manager;

    /// <summary>Connects, then checks the interface answers sensibly against the registry before trusting it.</summary>
    public static DesktopSwitcher? TryCreate(VirtualDesktopInfo? expected, out string status)
    {
        try
        {
            var manager = Connect();
            var count = manager.GetCount();
            var current = manager.GetCurrentDesktop().GetId();
            if (expected is not null && (count != expected.Count || (expected.Index >= 0 && current != expected.Ids[expected.Index])))
            {
                status = $"interface answered but disagrees with the registry ({count} desktops vs {expected.Count})";
                return null;
            }
            status = $"available, {count} desktops";
            return new DesktopSwitcher(manager);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            status = $"unavailable on this Windows build ({ex.GetType().Name} 0x{ex.HResult:X8})";
            return null;
        }
    }

    public Guid? CurrentDesktopId()
    {
        try
        {
            return Manager.GetCurrentDesktop().GetId();
        }
        catch (COMException)
        {
            _manager = null;
            return null;
        }
    }

    public bool TrySwitch(Guid desktopId)
    {
        try
        {
            var desktop = Manager.FindDesktop(ref desktopId);

            // Hand focus to an Explorer window first. Explorer then focuses the right window on the
            // new desktop, instead of keyboard focus staying on a now-hidden window with taskbar icons flashing.
            var focusHost = FindWindowW("XamlExplorerHostIslandWindow", null);
            if (focusHost != IntPtr.Zero) Foreground.Take(focusHost);
            Manager.SwitchDesktop(desktop);
            if (focusHost != IntPtr.Zero) ShowWindow(focusHost, SW_MINIMIZE);
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"Instant desktop switch failed (0x{ex.HResult:X8}); falling back to the shortcut.");
            _manager = null; // Explorer may have restarted; reconnect next time
            return false;
        }
    }

    /// <summary>Adds a desktop at the end of the list.</summary>
    public Guid? TryCreateDesktop()
    {
        try
        {
            return Manager.CreateDesktop().GetId();
        }
        catch (COMException ex)
        {
            Log.Write($"Creating a desktop failed (0x{ex.HResult:X8}).");
            _manager = null;
            return null;
        }
    }

    /// <summary>Removes a desktop and moves its windows to the surviving fallback desktop.</summary>
    public bool TryRemoveDesktop(Guid desktopId, Guid fallbackId)
    {
        if (desktopId == fallbackId) return false;
        try
        {
            var manager = Manager;
            if (manager.GetCount() <= 1) return false;
            var desktop = manager.FindDesktop(ref desktopId);
            var fallback = manager.FindDesktop(ref fallbackId);
            manager.RemoveDesktop(desktop, fallback);
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"Removing a desktop failed (0x{ex.HResult:X8}).");
            _manager = null;
            return false;
        }
    }

    private IVirtualDesktopManagerInternal Manager => _manager ??= Connect();

    private static IVirtualDesktopManagerInternal Connect()
    {
        var shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidImmersiveShell, throwOnError: true)!)!;
        var service = ClsidVirtualDesktopManagerInternal;
        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        return (IVirtualDesktopManagerInternal)shell.QueryService(ref service, ref iid);
    }

}

internal static class Foreground
{
    /// <summary>
    /// Makes <paramref name="window"/> the foreground window. A background app normally can't, so this
    /// briefly shares input state with the current foreground thread, the same way virtual desktop tools do.
    /// </summary>
    public static void Take(IntPtr window)
    {
        var targetThread = GetWindowThreadProcessId(window, out _);
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ourThread = GetCurrentThreadId();
        if (targetThread == 0 || foregroundThread == 0 || foregroundThread == ourThread)
        {
            SetForegroundWindow(window);
            return;
        }

        AttachThreadInput(targetThread, ourThread, true);
        AttachThreadInput(foregroundThread, ourThread, true);
        SetForegroundWindow(window);
        AttachThreadInput(foregroundThread, ourThread, false);
        AttachThreadInput(targetThread, ourThread, false);
    }
}
