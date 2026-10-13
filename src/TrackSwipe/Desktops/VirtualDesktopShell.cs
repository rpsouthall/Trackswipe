using System.Runtime.InteropServices;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Desktops;

// Explorer's internal virtual desktop interfaces, Windows 11 24H2/25H2 layout. These are undocumented:
// Microsoft changes the IIDs whenever the layout changes, so a successful QueryService for these exact IIDs is
// what makes calling into the vtable safe. Called through raw vtables (no runtime COM interop, so ahead-of-time
// compiled). Slots count IUnknown's three methods first; failures throw COMException as before.

/// <summary>A COM interface pointer this code owns one reference to.</summary>
internal sealed unsafe class ComObject : IDisposable
{
    public ComObject(IntPtr pointer) => Pointer = pointer;
    public IntPtr Pointer { get; private set; }
    public IntPtr Slot(int index) => (*(IntPtr**)Pointer)[index];

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero) Marshal.Release(Pointer);
        Pointer = IntPtr.Zero;
    }

    public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    /// <summary>Creates an out-of-process or in-process COM object, as Activator did.</summary>
    public static ComObject Create(Guid clsid, Guid iid)
    {
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 0x1 | 0x4 /* CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER */, ref iid, out var pointer));
        return new ComObject(pointer);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr pointer);
}

/// <summary>Documented shell interface IVirtualDesktopManager: which desktop a window belongs to.</summary>
internal sealed unsafe class WindowDesktopLookup
{
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid IidVirtualDesktopManager = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
    private ComObject? _manager;

    public Guid? GetDesktopId(IntPtr window)
    {
        try
        {
            var manager = _manager ??= ComObject.Create(ClsidVirtualDesktopManager, IidVirtualDesktopManager);
            Guid id;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int>)manager.Slot(4))(manager.Pointer, window, &id);
            return hr < 0 ? null : id; // not an app window, or it closed meanwhile
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Moves a top-level window to another desktop (diagnostics).</summary>
    public void MoveWindowToDesktop(IntPtr window, Guid desktop)
    {
        var manager = _manager ??= ComObject.Create(ClsidVirtualDesktopManager, IidVirtualDesktopManager);
        ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int>)manager.Slot(5))(manager.Pointer, window, &desktop));
    }
}

/// <summary>
/// Switches virtual desktops instantly through Explorer, with no animation, so our own
/// slide can be the only animation. Null from <see cref="TryCreate"/> means this Windows
/// build isn't supported and the caller should fall back to the keyboard shortcut.
/// </summary>
internal sealed unsafe class DesktopSwitcher
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid ClsidVirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid IidVirtualDesktopManagerInternal = new("53F5CA0B-158F-4124-900C-057158060B27");

    // Explorer's per-window views; the service and interface share this IID.
    private static readonly Guid IidApplicationViewCollection = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    // IVirtualDesktopManagerInternal
    private const int GetCountSlot = 3, MoveViewToDesktopSlot = 4, GetCurrentDesktopSlot = 6, SwitchDesktopSlot = 9, CreateDesktopSlot = 11,
        MoveDesktopSlot = 12, RemoveDesktopSlot = 13, FindDesktopSlot = 14;
    // IVirtualDesktop
    private const int GetIdSlot = 4;
    // IApplicationViewCollection
    private const int GetViewForHwndSlot = 6;

    private ComObject? _manager;
    private ComObject? _views;

    private DesktopSwitcher(ComObject manager) => _manager = manager;

    /// <summary>Connects, then checks the interface answers sensibly against the registry before trusting it.</summary>
    public static DesktopSwitcher? TryCreate(VirtualDesktopInfo? expected, out string status)
    {
        try
        {
            var manager = Connect();
            var count = Count(manager);
            Guid current;
            using (var desktop = Current(manager)) current = IdOf(desktop);
            if (expected is not null && (count != expected.Count || (expected.Index >= 0 && current != expected.Ids[expected.Index])))
            {
                manager.Dispose();
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
            using var desktop = Current(Manager);
            return IdOf(desktop);
        }
        catch (COMException)
        {
            Reset();
            return null;
        }
    }

    public bool TrySwitch(Guid desktopId)
    {
        try
        {
            var manager = Manager;
            using var desktop = Find(manager, desktopId);

            // Hand focus to an Explorer window first. Explorer then focuses the right window on the
            // new desktop, instead of keyboard focus staying on a now-hidden window with taskbar icons flashing.
            var focusHost = FindWindowW("XamlExplorerHostIslandWindow", null);
            if (focusHost != IntPtr.Zero) Foreground.Take(focusHost);
            ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)manager.Slot(SwitchDesktopSlot))(manager.Pointer, desktop.Pointer));
            if (focusHost != IntPtr.Zero) ShowWindow(focusHost, SW_MINIMIZE);
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"Instant desktop switch failed (0x{ex.HResult:X8}); falling back to the shortcut.");
            Reset(); // Explorer may have restarted; reconnect next time
            return false;
        }
    }

    /// <summary>Adds a desktop at the end of the list.</summary>
    public Guid? TryCreateDesktop()
    {
        try
        {
            var manager = Manager;
            IntPtr created;
            ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)manager.Slot(CreateDesktopSlot))(manager.Pointer, &created));
            using var desktop = new ComObject(created);
            return IdOf(desktop);
        }
        catch (COMException ex)
        {
            Log.Write($"Creating a desktop failed (0x{ex.HResult:X8}).");
            Reset();
            return null;
        }
    }

    /// <summary>Moves a desktop to a zero-based position in Explorer's desktop list.</summary>
    public bool TryMoveDesktop(Guid desktopId, int index)
    {
        try
        {
            var manager = Manager;
            if (index < 0 || index >= Count(manager)) return false;
            using var desktop = Find(manager, desktopId);
            ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)manager.Slot(MoveDesktopSlot))(manager.Pointer, desktop.Pointer, index));
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"Reordering a desktop failed (0x{ex.HResult:X8}).");
            Reset();
            return false;
        }
    }

    /// <summary>Removes a desktop and moves its windows to the surviving fallback desktop.</summary>
    public bool TryRemoveDesktop(Guid desktopId, Guid fallbackId)
    {
        if (desktopId == fallbackId) return false;
        try
        {
            var manager = Manager;
            if (Count(manager) <= 1) return false;
            using var desktop = Find(manager, desktopId);
            using var fallback = Find(manager, fallbackId);
            ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)manager.Slot(RemoveDesktopSlot))(manager.Pointer, desktop.Pointer, fallback.Pointer));
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"Removing a desktop failed (0x{ex.HResult:X8}).");
            Reset();
            return false;
        }
    }

    /// <summary>
    /// Moves any app's top-level window to another desktop. The documented IVirtualDesktopManager only moves
    /// windows the calling process owns; Explorer's own view collection moves anyone's.
    /// </summary>
    public bool TryMoveWindow(IntPtr window, Guid desktopId)
    {
        try
        {
            var manager = Manager;
            var views = _views ??= Connect(IidApplicationViewCollection, IidApplicationViewCollection);
            IntPtr view;
            ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)views.Slot(GetViewForHwndSlot))(views.Pointer, window, &view));
            using var applicationView = new ComObject(view);
            using var desktop = Find(manager, desktopId);
            ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)manager.Slot(MoveViewToDesktopSlot))(manager.Pointer, applicationView.Pointer, desktop.Pointer));
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"Moving a window to another desktop failed (0x{ex.HResult:X8}).");
            Reset();
            return false;
        }
    }

    private ComObject Manager => _manager ??= Connect();

    private void Reset()
    {
        _manager?.Dispose();
        _manager = null;
        _views?.Dispose();
        _views = null;
    }

    private static int Count(ComObject manager)
    {
        int count;
        ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)manager.Slot(GetCountSlot))(manager.Pointer, &count));
        return count;
    }

    private static ComObject Current(ComObject manager)
    {
        IntPtr desktop;
        ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)manager.Slot(GetCurrentDesktopSlot))(manager.Pointer, &desktop));
        return new ComObject(desktop);
    }

    private static ComObject Find(ComObject manager, Guid id)
    {
        IntPtr desktop;
        ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)manager.Slot(FindDesktopSlot))(manager.Pointer, &id, &desktop));
        return new ComObject(desktop);
    }

    private static Guid IdOf(ComObject desktop)
    {
        Guid id;
        ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)desktop.Slot(GetIdSlot))(desktop.Pointer, &id));
        return id;
    }

    private static ComObject Connect() => Connect(ClsidVirtualDesktopManagerInternal, IidVirtualDesktopManagerInternal);

    private static ComObject Connect(Guid service, Guid iid)
    {
        using var shell = ComObject.Create(ClsidImmersiveShell, IidServiceProvider);
        IntPtr manager;
        // IServiceProvider::QueryService
        ComObject.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)shell.Slot(3))(shell.Pointer, &service, &iid, &manager));
        return new ComObject(manager);
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
