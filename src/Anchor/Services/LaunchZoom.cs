using System.Runtime.InteropServices;
using Anchor.Interop;
using Microsoft.UI.Dispatching;

namespace Anchor.Services;

/// <summary>
/// The launch animation Dash to Dock (and GNOME) plays when an app is started from the dock: a copy
/// of its icon zooms out to twice its size while fading away.
/// <para>
/// The dock window is exactly as big as its strip, so the copy can't be drawn inside it. It is drawn
/// in a window of its own instead: a plain Win32 layered window — topmost, click-through, never
/// activated, never on the taskbar — fed one premultiplied 32-bit frame at a time through
/// <c>UpdateLayeredWindow</c>. No XAML, no compositor, no second WinUI window: about fifteen small
/// blits over a quarter of a second, then the window hides and costs nothing until the next launch.
/// </para>
/// </summary>
public sealed unsafe class LaunchZoom : IDisposable
{
    private const double DurationMs = 260;
    private const double EndScale = 2.0;

    private static readonly string ClassName = "Anchor.LaunchZoom." + Environment.ProcessId;
    private static bool _classRegistered;
    private static NativeMethods.WndProc? _wndProc; // rooted: Win32 holds it as a raw pointer

    private readonly DispatcherQueue _dispatcher;
    private nint _hwnd;
    private DispatcherQueueTimer? _timer;

    // The current animation: the icon at full (end) size, and where it plays.
    private nint _sourceDc, _sourceBitmap, _frameDc, _frameBitmap;
    private void* _frameBits;
    private int _size; // the square frame's side, physical px: the icon at EndScale
    private int _centerX, _centerY;
    private long _start;
    private int _frames;

    public LaunchZoom(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Plays the zoom over an icon. <paramref name="bgra"/> is the icon rendered at
    /// <see cref="EndScale"/> times its on-screen size: <paramref name="side"/> square, premultiplied
    /// BGRA, as <c>RenderTargetBitmap</c> hands it out. The centre is in physical screen pixels.
    /// </summary>
    public void Play(byte[] bgra, int side, int centerX, int centerY)
    {
        if (side <= 0 || bgra.Length < side * side * 4 || !EnsureWindow())
            return;
        Release();

        _size = side;
        _centerX = centerX;
        _centerY = centerY;
        nint screen = GetDC(nint.Zero);
        try
        {
            _sourceDc = CreateCompatibleDC(screen);
            _frameDc = CreateCompatibleDC(screen);
            _sourceBitmap = CreateBitmap(screen, side, out var sourceBits);
            _frameBitmap = CreateBitmap(screen, side, out _frameBits);
            if (_sourceBitmap == nint.Zero || _frameBitmap == nint.Zero)
            {
                Release();
                return;
            }
            Marshal.Copy(bgra, 0, (nint)sourceBits, side * side * 4);
            SelectObject(_sourceDc, _sourceBitmap);
            SelectObject(_frameDc, _frameBitmap);
        }
        finally
        {
            ReleaseDC(nint.Zero, screen);
        }

        _start = Environment.TickCount64;
        DrawFrame(0);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        if (_timer is null)
        {
            _timer = _dispatcher.CreateTimer();
            // Asked for 8 ms, served on the next ~15.6 ms system tick: 60 frames a second. Asking for
            // 16 ms rounds up to two ticks, and the zoom drops to about 30.
            _timer.Interval = TimeSpan.FromMilliseconds(8);
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
    }

    private void Tick()
    {
        double t = (Environment.TickCount64 - _start) / DurationMs;
        _frames++;
        if (t >= 1)
        {
            _timer?.Stop();
            Probe.Count($"launchZoomFrames{_frames}");
            _frames = 0;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            Release();
            return;
        }
        DrawFrame(t);
    }

    /// <summary>One frame: the icon scaled from its own size up to <see cref="EndScale"/>, fading out as it goes.</summary>
    private void DrawFrame(double t)
    {
        double eased = 1 - (1 - t) * (1 - t); // ease-out quad, as GNOME's zoom
        double scale = (1 + (EndScale - 1) * eased) / EndScale; // of the full-size frame
        byte alpha = (byte)Math.Round(255 * (1 - eased) * 0.9);
        int drawn = Math.Max(1, (int)Math.Round(_size * scale));
        int offset = (_size - drawn) / 2;

        NativeMemory.Clear(_frameBits, (nuint)(_size * _size * 4));
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        AlphaBlend(_frameDc, offset, offset, drawn, drawn, _sourceDc, 0, 0, _size, _size, blend);

        var position = new NativeMethods.POINT { X = _centerX - _size / 2, Y = _centerY - _size / 2 };
        var size = new SIZE { cx = _size, cy = _size };
        var origin = new NativeMethods.POINT();
        var opaque = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, nint.Zero, ref position, ref size, _frameDc, ref origin, 0, ref opaque, 2 /* ULW_ALPHA */);
    }

    private bool EnsureWindow()
    {
        if (_hwnd != nint.Zero)
            return true;
        var instance = NativeMethods.GetModuleHandle(null);
        if (!_classRegistered)
        {
            _wndProc = (hwnd, msg, w, l) => NativeMethods.DefWindowProc(hwnd, msg, w, l);
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassEx(ref wc) == 0)
                return false;
            _classRegistered = true;
        }
        const int exStyle = 0x00080000 /* LAYERED */ | 0x00000020 /* TRANSPARENT: clicks pass through */ |
                            0x00000008 /* TOPMOST */ | 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
        _hwnd = NativeMethods.CreateWindowEx(exStyle, ClassName, null, (uint)NativeMethods.WS_POPUP,
            0, 0, 0, 0, nint.Zero, nint.Zero, instance, nint.Zero);
        return _hwnd != nint.Zero;
    }

    private static nint CreateBitmap(nint dc, int side, out void* bits)
    {
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = side,
            biHeight = -side, // top-down, as RenderTargetBitmap's rows are
            biPlanes = 1,
            biBitCount = 32,
        };
        nint bitmap = CreateDIBSection(dc, ref info, 0, out var pointer, nint.Zero, 0);
        bits = (void*)pointer;
        return bitmap;
    }

    private void Release()
    {
        if (_sourceDc != nint.Zero) DeleteDC(_sourceDc);
        if (_frameDc != nint.Zero) DeleteDC(_frameDc);
        if (_sourceBitmap != nint.Zero) DeleteObject(_sourceBitmap);
        if (_frameBitmap != nint.Zero) DeleteObject(_frameBitmap);
        _sourceDc = _frameDc = _sourceBitmap = _frameBitmap = nint.Zero;
        _frameBits = null;
    }

    public void Dispose()
    {
        _timer?.Stop();
        Release();
        if (_hwnd != nint.Zero)
            NativeMethods.DestroyWindow(_hwnd);
        _hwnd = nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("msimg32.dll")] private static extern bool AlphaBlend(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, BLENDFUNCTION blend);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint dstDc, ref NativeMethods.POINT dst, ref SIZE size, nint srcDc, ref NativeMethods.POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
}
