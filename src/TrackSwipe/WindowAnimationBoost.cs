using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TrackSwipe.Interop;

namespace TrackSwipe;

/// <summary>
/// The display's top refresh rate, asked of the compositor for as long as anyone holds it. With a dynamic refresh rate
/// the display rests at 60 Hz and Windows raises it for scrolling and the pointer; whoever animates asks for more while
/// it does. Counted, because TrackSwipe's own animations and Windows' window animations can overlap, and Windows keeps
/// one switch per process: either letting go must not cut the other short.
/// </summary>
internal static class CompositorBoost
{
    private static readonly object Gate = new();
    private static int _holds;
    private static bool _unavailable, _failureLogged;

    /// <summary>False on a Windows without the compositor clock API (before Windows 11): nothing to hold.</summary>
    public static bool Available => !_unavailable;

    public static void Acquire()
    {
        lock (Gate)
            if (_holds++ == 0) Set(true);
    }

    public static void Release()
    {
        lock (Gate)
            if (_holds > 0 && --_holds == 0) Set(false);
    }

    private static void Set(bool enable)
    {
        if (_unavailable) return;
        try
        {
            var hr = NativeMethods.DCompositionBoostCompositorClock(enable);
            if (hr < 0 && !_failureLogged)
            {
                Log.Write($"Compositor refresh boost failed: 0x{hr:X8}.");
                _failureLogged = true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            _unavailable = true;
            Log.Write("Compositor refresh boost unavailable.");
        }
    }
}

/// <summary>
/// "Smooth window animations": Windows' own window animations — minimize and restore, maximize, snapping, a window
/// opening or closing — run at whatever rate the display is at, which with a dynamic refresh rate is 60 Hz whenever
/// nothing else is moving (measured: 58–62 frames a second through a maximize). While one plays, this holds the
/// display at its top rate (measured: 109–115), as TrackSwipe's own animations do, and lets go half a second after.
/// <para>
/// Only what starts such an animation is listened for: minimizing and restoring, the end of a drag (where a snap
/// lands), app windows appearing and going, and — for maximizing and keyboard snaps, which say nothing else — the
/// moves of the app in front only, taken as an animation when a window's size jumps. Nothing runs in between.
/// </para>
/// </summary>
internal sealed unsafe class WindowAnimationBoost : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B,
        EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x2;

    /// <summary>How long the top rate is held after the last animation began: Windows' run about a quarter of a second.</summary>
    private const int HoldMs = 450;

    /// <summary>A change in a window's width or height bigger than this share of it is a maximize or a snap, not a drag.</summary>
    private const double JumpShare = 0.15;

    private static WindowAnimationBoost? _current;
    private readonly List<IntPtr> _hooks = new();
    private readonly Dictionary<IntPtr, (int Width, int Height)> _sizes = new();
    private readonly Win32.UiTimer _release = new() { Interval = HoldMs };
    private IntPtr _moveHook;
    private uint _moveProcess;
    private bool _holding;

    /// <summary>Starts listening; on a thread with a message loop, which the events arrive through.</summary>
    public WindowAnimationBoost()
    {
        _current = this;
        _release.Tick += (_, _) => LetGo();
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE);
        WatchMovesOf(GetForegroundWindow());
    }

    private void Hook(uint first, uint last)
    {
        var hook = SetWinEventHook(first, last, IntPtr.Zero, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void>)&OnEvent, 0, 0, WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int child, uint thread, uint time)
    {
        try { _current?.Handle(kind, window, objectId, child); }
        catch (Exception ex) { Log.Write($"Smooth window animations: {ex.Message}"); }
    }

    private void Handle(uint kind, IntPtr window, int objectId, int child)
    {
        if (window == IntPtr.Zero || objectId != 0 /* OBJID_WINDOW */ || child != 0) return; // carets and the like
        switch (kind)
        {
            case EVENT_SYSTEM_FOREGROUND:
                WatchMovesOf(window);
                return;
            case EVENT_SYSTEM_MINIMIZESTART or EVENT_SYSTEM_MINIMIZEEND or EVENT_SYSTEM_MOVESIZEEND:
                Hold();
                return;
            case EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE:
                if (IsAppWindow(window)) Hold(); // menus, tooltips and the like don't animate open
                return;
            case EVENT_OBJECT_LOCATIONCHANGE:
                if (SizeJumped(window)) Hold();
                return;
        }
    }

    /// <summary>A window with a caption and no tool-window style at the top of its tree: what Windows animates.</summary>
    private static bool IsAppWindow(IntPtr window)
    {
        if (GetAncestor(window, 2 /* GA_ROOT */) != window) return false;
        const int WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x80;
        return (GetWindowLongW(window, -16) & WS_CAPTION) == WS_CAPTION && (GetWindowLongW(window, -20) & WS_EX_TOOLWINDOW) == 0;
    }

    /// <summary>Whether the window's size jumped (a maximize, a restore, a snap) rather than crept (a drag of its edge).</summary>
    private bool SizeJumped(IntPtr window)
    {
        if (GetAncestor(window, 2 /* GA_ROOT */) != window || !GetWindowRect(window, out var rect)) return false;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var known = _sizes.TryGetValue(window, out var last);
        _sizes[window] = (width, height);
        if (!known) return false;
        return Math.Abs(width - last.Width) > JumpShare * Math.Max(last.Width, width) ||
               Math.Abs(height - last.Height) > JumpShare * Math.Max(last.Height, height);
    }

    /// <summary>Listens for the moves of the app in front, and only that app (a maximize or a keyboard snap says nothing else).</summary>
    private void WatchMovesOf(IntPtr window)
    {
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var process) == 0 || process == _moveProcess) return;
        if (_moveHook != IntPtr.Zero) UnhookWinEvent(_moveHook);
        _moveHook = IntPtr.Zero;
        _moveProcess = process;
        _sizes.Clear();
        if (process == (uint)Environment.ProcessId) return;
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void>)&OnEvent, process, 0, 0);
    }

    private void Hold()
    {
        if (!_holding)
        {
            CompositorBoost.Acquire();
            _holding = true;
        }
        _release.Stop();
        _release.Start();
    }

    private void LetGo()
    {
        _release.Stop();
        if (!_holding) return;
        _holding = false;
        CompositorBoost.Release();
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        if (_moveHook != IntPtr.Zero) UnhookWinEvent(_moveHook);
        _moveHook = IntPtr.Zero;
        LetGo();
        _release.Dispose();
        if (_current == this) _current = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, IntPtr callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
