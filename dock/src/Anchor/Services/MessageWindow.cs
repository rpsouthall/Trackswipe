using System.Runtime.InteropServices;
using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// A hidden, never-shown HWND that exists only to receive window messages on the UI thread —
/// the tray icon's callbacks and <c>WM_HOTKEY</c> from <c>RegisterHotKey</c>, neither of which a
/// WinUI 3 <c>Window</c> can surface. Create it on the UI thread so its messages are pumped by
/// the app's own message loop, and dispose it before the app exits.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    // Unique per process: two Anchors in one session are already prevented by the instance mutex,
    // but a stale class registration from a previous AppDomain would make RegisterClassEx fail.
    private static readonly string ClassName = "Anchor.MessageWindow." + Environment.ProcessId;

    // The delegate is handed to Win32 as a raw function pointer, so it must be rooted for as long
    // as the window lives or the GC will collect it out from under the message pump.
    private readonly NativeMethods.WndProc _wndProc;
    private bool _disposed;

    /// <summary>The window handle. <see cref="nint.Zero"/> if creation failed.</summary>
    public nint Handle { get; }

    /// <summary>
    /// Raised for every message the window receives, on the UI thread. Handlers run before
    /// <c>DefWindowProc</c> and are expected to be quick and non-throwing.
    /// </summary>
    public event Action<uint, nint, nint>? MessageReceived;

    public MessageWindow()
    {
        _wndProc = OnMessage;

        var instance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            Diag.Log($"MessageWindow: RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
            return;
        }

        // WS_EX_TOOLWINDOW and no WS_VISIBLE: never painted, never in the taskbar or Alt-Tab, but
        // still a top-level window, so it can be made foreground for the tray menu.
        Handle = NativeMethods.CreateWindowEx(
            (int)NativeMethods.WS_EX_TOOLWINDOW, ClassName, "Anchor", (uint)NativeMethods.WS_POPUP,
            0, 0, 0, 0, nint.Zero, nint.Zero, instance, nint.Zero);

        if (Handle == nint.Zero)
            Diag.Log($"MessageWindow: CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            MessageReceived?.Invoke(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // A throw here would unwind through native code and take the process down, so a
            // misbehaving handler is logged and the message is otherwise handled normally.
            Diag.Log($"MessageWindow handler failed (msg 0x{msg:X}): {ex}");
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Handle != nint.Zero)
            NativeMethods.DestroyWindow(Handle);
        // The window class is intentionally left registered: it is process-unique and unregisters
        // itself when the process exits, and UnregisterClass would race any in-flight messages.
    }
}
