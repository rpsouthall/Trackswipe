using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TrackSwipe;

/// <summary>
/// Sees a left click on the taskbar before Explorer acts on it. A click on an app whose window is on another desktop
/// makes Windows cut straight to that desktop; seeing the press first leaves time to prepare a slide, and the release
/// is held (briefly) until the slide is on screen, so the cut happens underneath it.
/// </summary>
/// <remarks>
/// Costs nothing while the pointer just moves. The press is noticed asynchronously, when the taskbar takes the mouse
/// capture (a WinEvent, delivered to this thread with no hook in the input path). Only then is a low-level mouse hook
/// installed, to catch that one release before Explorer does, and it's removed straight after.
/// </remarks>
internal sealed unsafe class TaskbarClickWatch : IDisposable
{
    /// <summary>The longest a release waits for the slide to be shown (Windows drops hooks that take ~300 ms).</summary>
    private const int ReleaseWaitMs = 30;
    /// <summary>A press held this long isn't a click; stop waiting for its release.</summary>
    private const int PressTimeoutMs = 3000;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_QUIT = 0x0012, WM_TIMER = 0x0113, WM_APP_UNHOOK = 0x8001;
    private const uint EVENT_SYSTEM_CAPTURESTART = 0x0008;
    private const int Unknown = 0, Interested = 1, NotInterested = 2;

    private static TaskbarClickWatch? _current;
    private readonly Action<Point, Action<bool>> _pressed;
    private readonly Action<Point, Action> _released;
    private readonly Action _abandoned;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _shown = new(false);
    private uint _threadId;
    private IntPtr _mouseHook, _timer;
    private long _pressedAt;
    private static volatile int _upMessage = WM_LBUTTONUP;
    private volatile int _interest;

    /// <param name="pressed">The left button went down on a taskbar. Call the action with whether the release matters.</param>
    /// <param name="released">It came up again; call the action once anything that must be on screen first is.</param>
    /// <param name="abandoned">The release was missed (or the press went on too long): forget the press.</param>
    public TaskbarClickWatch(Action<Point, Action<bool>> pressed, Action<Point, Action> released, Action abandoned)
    {
        _pressed = pressed;
        _released = released;
        _abandoned = abandoned;
        _current = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "TrackSwipe taskbar clicks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait(1000);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var capture = SetWinEventHook(EVENT_SYSTEM_CAPTURESTART, EVENT_SYSTEM_CAPTURESTART, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void>)&OnCaptureStart, 0, 0, 0x0002 /* SKIPOWNPROCESS */);
        if (capture == IntPtr.Zero) Log.Write("Taskbar clicks can't be watched: desktop jumps from the taskbar won't slide.");
        _ready.Set();
        if (capture == IntPtr.Zero) return;
        try
        {
            MSG message;
            while (GetMessageW(&message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Window != IntPtr.Zero) continue;
                if (message.Message == WM_APP_UNHOOK) RemoveMouseHook();
                else if (message.Message == WM_TIMER && _mouseHook != IntPtr.Zero && Environment.TickCount64 - _pressedAt > PressTimeoutMs)
                {
                    RemoveMouseHook();
                    _abandoned();
                }
            }
        }
        finally
        {
            RemoveMouseHook();
            UnhookWinEvent(capture);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnCaptureStart(IntPtr hook, uint kind, IntPtr window, int objectId, int child, uint thread, uint time)
    {
        try { _current?.CaptureStarted(window); }
        catch (Exception ex) { Log.Write($"Taskbar click watch: {ex.Message}"); }
    }

    /// <summary>Some window took the mouse capture: if it's a taskbar and the left button is down, a click is under way.</summary>
    private void CaptureStarted(IntPtr window)
    {
        if (_mouseHook != IntPtr.Zero || !IsTaskbar(GetAncestor(window, 2 /* GA_ROOT */))) return;
        var swapped = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0;
        var left = swapped ? 0x02 /* VK_RBUTTON */ : 0x01 /* VK_LBUTTON */;
        _upMessage = swapped ? WM_RBUTTONUP : WM_LBUTTONUP;
        if ((GetAsyncKeyState(left) & 0x8000) == 0) return; // released already, or not the primary button
        if (!GetCursorPos(out var point)) return;
        _pressedAt = Environment.TickCount64;
        _interest = Unknown;
        _mouseHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, (IntPtr)(delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr>)&OnMouse, GetModuleHandleW(null), 0);
        if (_mouseHook == IntPtr.Zero) return;
        _timer = SetTimer(IntPtr.Zero, IntPtr.Zero, 500, IntPtr.Zero); // gives up on a press held too long
        _pressed(point, interested => _interest = interested ? Interested : NotInterested);
        // The button may have come up between the capture and the hook: then the release has gone to Explorer already.
        if ((GetAsyncKeyState(left) & 0x8000) == 0)
        {
            RemoveMouseHook();
            _abandoned();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _current is { } watch && (int)message == _upMessage)
        {
            try { watch.Released(*(Point*)data); }
            catch (Exception ex) { Log.Write($"Taskbar click watch: {ex.Message}"); }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void Released(Point point)
    {
        PostThreadMessageW(_threadId, WM_APP_UNHOOK, IntPtr.Zero, IntPtr.Zero); // one release is all this hook is for
        if (_interest == NotInterested)
        {
            _released(point, () => { });
            return;
        }
        _shown.Reset();
        _released(point, _shown.Set);
        _shown.Wait(ReleaseWaitMs); // Explorer acts on the release: hold it until the slide covers the screen
    }

    private void RemoveMouseHook()
    {
        if (_timer != IntPtr.Zero) { KillTimer(IntPtr.Zero, _timer); _timer = IntPtr.Zero; }
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private static bool IsTaskbar(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var name = stackalloc char[32];
        var length = GetClassNameW(window, name, 32);
        var className = new ReadOnlySpan<char>(name, Math.Max(0, length));
        return className.SequenceEqual("Shell_TrayWnd") || className.SequenceEqual("Shell_SecondaryTrayWnd");
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        if (_current == this) _current = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public Point Point; public uint Private; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, IntPtr callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int kind, IntPtr callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr window, IntPtr id, uint ms, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, IntPtr id);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, char* name, int max);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
