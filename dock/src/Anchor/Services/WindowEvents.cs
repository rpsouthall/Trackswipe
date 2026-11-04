using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// The one place Anchor hears about other apps' windows, so nothing else has to poll for them.
/// <para>
/// WinEvent hooks, out of context: Windows posts each event to this (the UI) thread, so nothing
/// runs in other processes and nothing here needs a lock. The hooks are deliberately narrow — exact
/// events, never a range — because every event in a hooked range is delivered across processes,
/// and a range spanning <c>EVENT_OBJECT_LOCATIONCHANGE</c> wakes the listener for every mouse
/// move and caret blink on the whole system. Window moves and resizes are only listened for in the
/// one app in front, which is the only app whose windows the dock dodges.
/// </para>
/// </summary>
public sealed class WindowEvents : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback; // held: the native side calls back through it
    private readonly List<nint> _hooks = new();
    private nint _moveHook;
    private uint _moveHookProcess;

    /// <summary>A top-level window was shown, hidden, destroyed, cloaked or uncloaked.</summary>
    public event Action<nint, uint>? WindowChanged;

    /// <summary>Another app came to the front (Anchor's own windows don't count).</summary>
    public event Action<nint>? ForegroundChanged;

    /// <summary>A window of the app in front moved, resized, minimized or was restored.</summary>
    public event Action? FrontWindowMoved;

    /// <summary>The last window in front that wasn't one of Anchor's own.</summary>
    public nint Foreground { get; private set; }

    public WindowEvents()
    {
        _callback = OnEvent;
        const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
        Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, flags);
        Hook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND, flags);
        Hook(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_HIDE, flags); // destroy, show, hide
        Hook(NativeMethods.EVENT_OBJECT_CLOAKED, NativeMethods.EVENT_OBJECT_UNCLOAKED, flags);
        SetForeground(NativeMethods.GetForegroundWindow());
    }

    private void Hook(uint min, uint max, uint flags)
    {
        var hook = NativeMethods.SetWinEventHook(min, max, nint.Zero, _callback, 0, 0, flags);
        if (hook != nint.Zero)
            _hooks.Add(hook);
        else
            Diag.Log($"WindowEvents: hooking 0x{min:X}-0x{max:X} failed");
    }

    private void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (Probe.Enabled)
            Probe.Count($"event{eventType:X}");
        try
        {
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    if (hwnd != nint.Zero)
                        SetForeground(hwnd);
                    return;
                case NativeMethods.EVENT_SYSTEM_MINIMIZESTART or NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                    FrontWindowMoved?.Invoke(); // cheap for listeners: they only mark themselves stale
                    return;
            }

            // Whole top-level windows only; the controls inside windows come through far more often.
            if (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF || hwnd == nint.Zero)
                return;
            if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
            {
                // Chromium-based apps (browsers, Electron) report their window "moved" many times a second while
                // their content changes, at the same place. Only a real change of place or size is news: the
                // window's frame is compared with the last one seen, which is nearly free, before anyone is told.
                if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd ||
                    NativeMethods.DwmGetWindowRect(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var frame,
                        System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
                    return;
                if (_frames.TryGetValue(hwnd, out var last) &&
                    last.Left == frame.Left && last.Top == frame.Top && last.Right == frame.Right && last.Bottom == frame.Bottom)
                    return;
                _frames[hwnd] = frame;
                if (Probe.Enabled)
                    Probe.Count("frontWindowMoved");
                FrontWindowMoved?.Invoke();
                return;
            }
            // A destroyed window can't be asked about its parent any more; let it through.
            if (eventType != NativeMethods.EVENT_OBJECT_DESTROY && NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
                return;
            WindowChanged?.Invoke(hwnd, eventType);
        }
        catch (Exception ex)
        {
            Diag.Log("WindowEvents: handler failed: " + ex.Message);
        }
    }

    private void SetForeground(nint hwnd)
    {
        if (hwnd == nint.Zero || NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == Environment.ProcessId)
            return;
        Foreground = hwnd;
        WatchMovesOf(pid);
        ForegroundChanged?.Invoke(hwnd);
    }

    /// <summary>Where the front app's windows last were, to tell a real move from a report of the same place.</summary>
    private readonly Dictionary<nint, NativeMethods.RECT> _frames = new();

    private readonly HashSet<object> _moveWatchers = new();

    /// <summary>
    /// Says whether <paramref name="watcher"/> (a dock that dodges windows) needs
    /// <see cref="FrontWindowMoved"/>. The front app's window moves are only listened for while
    /// someone needs them: some apps report dozens a second, and each is a wake-up.
    /// </summary>
    public void WatchFrontMoves(object watcher, bool on)
    {
        if (on ? !_moveWatchers.Add(watcher) : !_moveWatchers.Remove(watcher))
            return;
        if (_moveWatchers.Count == 0)
        {
            if (_moveHook != nint.Zero)
                NativeMethods.UnhookWinEvent(_moveHook);
            _moveHook = nint.Zero;
            _moveHookProcess = 0;
        }
        else if (Foreground != nint.Zero && NativeMethods.GetWindowThreadProcessId(Foreground, out uint pid) != 0)
        {
            WatchMovesOf(pid);
        }
    }

    /// <summary>Listens for window moves in the app in front, and only there.</summary>
    private void WatchMovesOf(uint pid)
    {
        if (_moveWatchers.Count == 0)
            return;
        if (pid == _moveHookProcess && _moveHook != nint.Zero)
            return;
        if (_moveHook != nint.Zero)
            NativeMethods.UnhookWinEvent(_moveHook);
        _moveHookProcess = pid;
        _frames.Clear(); // another app in front: only its windows are compared from now on
        _moveHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, nint.Zero, _callback,
            pid, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
            NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        if (_moveHook != nint.Zero)
            NativeMethods.UnhookWinEvent(_moveHook);
        _moveHook = nint.Zero;
    }
}
