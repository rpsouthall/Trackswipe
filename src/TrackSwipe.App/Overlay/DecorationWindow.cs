using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TrackSwipe.Interop;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// A transparent, click-through layer above a thumbnail surface for what thumbnails can't show:
/// labels, highlights and buttons. GDI+ draws straight into the DIB that UpdateLayeredWindow
/// presents, so repainting copies nothing, and fading only changes the window's overall alpha.
/// </summary>
internal sealed class DecorationWindow : NativeWindow, IDisposable
{
    private const uint ULW_ALPHA = 0x2;

    private readonly Rectangle _monitor;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _previousBitmap;
    private readonly Bitmap _canvas;
    private byte _alpha;
    private bool _presented;

    public DecorationWindow(Rectangle monitor, bool topmost = true)
    {
        _monitor = monitor;
        CreateHandle(new CreateParams
        {
            Caption = "TrackSwipe decoration",
            X = monitor.X,
            Y = monitor.Y,
            Width = monitor.Width,
            Height = monitor.Height,
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (topmost ? WS_EX_TOPMOST : 0),
        });

        var header = new BITMAPINFOHEADER
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            Width = monitor.Width,
            Height = -monitor.Height, // top-down, matching GDI+ row order
            Planes = 1,
            BitCount = 32,
        };
        var screenDc = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        _dib = CreateDIBSection(_memoryDc, ref header, 0, out var bits, IntPtr.Zero, 0);
        _previousBitmap = SelectObject(_memoryDc, _dib);
        // Premultiplied ARGB is exactly what UpdateLayeredWindow expects.
        _canvas = new Bitmap(monitor.Width, monitor.Height, monitor.Width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    /// <summary>
    /// A hidden source for thumbnails: painted once, then cloaked and parked off-screen so it's never
    /// seen itself. DWM keeps rendering it, so a thumbnail of it draws <paramref name="paint"/>'s
    /// picture anywhere in a surface's z-order. Mirror it at its own size: DWM won't enlarge thumbnails.
    /// </summary>
    public static DecorationWindow CreateSwatch(Size size, Action<Graphics> paint)
    {
        var swatch = new DecorationWindow(new Rectangle(new Point(-32000, -32000), size), topmost: false);
        var cloak = 1;
        DwmSetWindowAttribute(swatch.Handle, DWMWA_CLOAK, ref cloak, sizeof(int));
        swatch._alpha = 255;
        swatch.Paint(paint);
        ShowWindow(swatch.Handle, SW_SHOWNOACTIVATE);
        return swatch;
    }

    public Size Size => _monitor.Size;

    /// <summary>Raised when a hotkey registered against this window fires.</summary>
    public event Action<int>? HotKeyPressed;

    public void Paint(Action<Graphics> draw)
    {
        using (var g = Graphics.FromImage(_canvas))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            draw(g);
        }
        Present();
    }

    /// <summary>Fades the whole layer without repainting it.</summary>
    public void SetAlpha(byte alpha)
    {
        if (alpha == _alpha && _presented) return;
        _alpha = alpha;
        if (!_presented) return;
        var blend = Blend();
        UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, ULW_ALPHA);
    }

    public bool IsShown { get; private set; }

    public void Show()
    {
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        IsShown = true;
    }

    public void Hide()
    {
        ShowWindow(Handle, SW_HIDE);
        IsShown = false;
    }

    public void Dispose()
    {
        DestroyHandle();
        _canvas.Dispose();
        SelectObject(_memoryDc, _previousBitmap);
        DeleteObject(_dib);
        DeleteDC(_memoryDc);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            HotKeyPressed?.Invoke(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }

    private void Present()
    {
        var destination = new POINT { X = _monitor.X, Y = _monitor.Y };
        var size = new SIZE { Width = _monitor.Width, Height = _monitor.Height };
        var source = new POINT();
        var blend = Blend();
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _memoryDc, ref source, 0, ref blend, ULW_ALPHA);
        _presented = true;
    }

    private BLENDFUNCTION Blend() => new() { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = _alpha, AlphaFormat = 1 };
}
