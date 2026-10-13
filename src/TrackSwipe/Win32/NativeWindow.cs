using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TrackSwipe.Win32;

/// <summary>A window message, as handed to <see cref="NativeWindow.WndProc"/>.</summary>
internal struct Message
{
    public IntPtr HWnd;
    public int Msg;
    public IntPtr WParam;
    public IntPtr LParam;
    public IntPtr Result;
}

/// <summary>What <see cref="NativeWindow.CreateHandle"/> creates.</summary>
internal sealed class CreateParams
{
    public string? Caption { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Style { get; set; }
    public int ExStyle { get; set; }
    /// <summary>Owner or parent; <c>HWND_MESSAGE</c> makes a message-only window.</summary>
    public IntPtr Parent { get; set; }
}

/// <summary>
/// A Win32 window whose messages arrive at <see cref="WndProc"/>: the small part of WinForms' NativeWindow this
/// app needs, without the WinForms runtime. One window class serves every instance; the instance travels in
/// the creation parameters and then in GWLP_USERDATA. An exception thrown while handling a message never
/// crosses back into Windows: it goes to the thread's <see cref="MessageLoop"/> handler.
/// </summary>
internal unsafe class NativeWindow
{
    private const string ClassName = "TrackSwipe.Window";
    private const int GWLP_USERDATA = -21, WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082;
    private static ushort _atom;
    private static readonly object Gate = new();
    private GCHandle _self;

    public IntPtr Handle { get; private set; }

    public void CreateHandle(CreateParams cp)
    {
        EnsureClass();
        _self = GCHandle.Alloc(this);
        var handle = CreateWindowExW(cp.ExStyle, ClassName, cp.Caption ?? "", cp.Style, cp.X, cp.Y, cp.Width, cp.Height,
            cp.Parent, IntPtr.Zero, GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (_self.IsAllocated) _self.Free();
            throw new System.ComponentModel.Win32Exception(error, "Could not create a window.");
        }
        Handle = handle;
    }

    public void DestroyHandle()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle); // WM_NCDESTROY releases the instance
    }

    /// <summary>Handles a message; the default passes it to DefWindowProc.</summary>
    protected virtual void WndProc(ref Message m) => m.Result = DefWindowProcW(m.HWnd, (uint)m.Msg, m.WParam, m.LParam);

    private static void EnsureClass()
    {
        lock (Gate)
        {
            if (_atom != 0) return;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    style = 0x0001 | 0x0002, // CS_VREDRAW | CS_HREDRAW
                    lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&Dispatch,
                    hInstance = GetModuleHandleW(null),
                    hCursor = LoadCursorW(IntPtr.Zero, 32512 /* IDC_ARROW */),
                    lpszClassName = name,
                };
                _atom = RegisterClassExW(ref wc);
                if (_atom == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not register the window class.");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr pointer;
        if (msg == WM_NCCREATE)
        {
            pointer = ((CREATESTRUCTW*)lParam)->lpCreateParams;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, pointer);
        }
        else pointer = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (pointer == IntPtr.Zero) return DefWindowProcW(hwnd, msg, wParam, lParam);

        var handle = GCHandle.FromIntPtr(pointer);
        var window = (NativeWindow)handle.Target!;
        if (window.Handle == IntPtr.Zero) window.Handle = hwnd; // messages sent during CreateWindowEx
        var m = new Message { HWnd = hwnd, Msg = (int)msg, WParam = wParam, LParam = lParam };
        try
        {
            window.WndProc(ref m);
        }
        catch (Exception ex)
        {
            MessageLoop.Report(ex);
            m.Result = IntPtr.Zero;
        }
        if (msg == WM_NCDESTROY)
        {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, IntPtr.Zero);
            window.Handle = IntPtr.Zero;
            if (window._self.IsAllocated) window._self.Free();
        }
        return m.Result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCTW
    {
        public IntPtr lpCreateParams, hInstance, hMenu, hwndParent;
        public int cy, cx, y, x, style;
        public IntPtr lpszName, lpszClass;
        public int dwExStyle;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, nint cursor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}

/// <summary>A thread's message loop, and where exceptions thrown while handling its messages go.</summary>
internal static class MessageLoop
{
    [ThreadStatic] private static Action<Exception>? _handler;

    /// <summary>Pumps messages until <see cref="Quit"/> is called on this thread.</summary>
    public static void Run(Action<Exception>? onException = null)
    {
        _handler = onException;
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    /// <summary>Ends the calling thread's <see cref="Run"/>.</summary>
    public static void Quit() => PostQuitMessage(0);

    internal static void Report(Exception exception)
    {
        if (_handler is { } handler)
        {
            try { handler(exception); return; }
            catch (Exception nested) { exception = new AggregateException(exception, nested); }
        }
        Log.Write($"Unhandled error: {exception}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}

/// <summary>
/// A timer that raises <see cref="Tick"/> on the thread that started it, from that thread's message loop: the
/// part of the WinForms timer this app uses. Backed by SetTimer on one message-only window per thread.
/// </summary>
internal sealed class UiTimer : IDisposable
{
    private const int WM_TIMER = 0x0113;
    [ThreadStatic] private static Host? _host;
    private static int _nextId;
    private readonly nint _id = Interlocked.Increment(ref _nextId);
    private Host? _owner;
    private int _interval = 100;

    public event EventHandler? Tick;

    public int Interval
    {
        get => _interval;
        set { _interval = Math.Max(1, value); if (Enabled) Start(); }
    }

    public bool Enabled { get; private set; }

    public void Start()
    {
        _owner ??= _host ??= new Host();
        _owner.Timers[_id] = this;
        SetTimer(_owner.Handle, _id, (uint)_interval, IntPtr.Zero);
        Enabled = true;
    }

    public void Stop()
    {
        if (!Enabled || _owner is null) return;
        KillTimer(_owner.Handle, _id);
        _owner.Timers.Remove(_id);
        Enabled = false;
    }

    public void Dispose() => Stop();

    private sealed class Host : NativeWindow
    {
        public readonly Dictionary<nint, UiTimer> Timers = new();

        public Host() => CreateHandle(new CreateParams { Caption = "TrackSwipe timers", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TIMER && Timers.TryGetValue(m.WParam, out var timer))
            {
                timer.Tick?.Invoke(timer, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll")] private static extern nint SetTimer(IntPtr hwnd, nint id, uint elapse, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, nint id);
}
