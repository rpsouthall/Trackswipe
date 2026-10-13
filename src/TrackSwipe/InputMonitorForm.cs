using System.Runtime.InteropServices;
using TrackSwipe.Core;
using TrackSwipe.Win32;

namespace TrackSwipe;

internal sealed record MonitorStatus(int ActiveContacts, GesturePhase Phase, GestureAxis Axis, double Progress, double DisplayProgress, double Velocity, bool Enabled);

/// <summary>Live view of touchpad contacts and engine state, for checking input and tuning. A plain Win32 window painted with GDI.</summary>
internal sealed class InputMonitorForm : NativeWindow, IDisposable
{
    private const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_DPICHANGED = 0x02E0;
    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000, CW_USEDEFAULT = unchecked((int)0x80000000);
    private static readonly int Background = Rgb(24, 24, 28), Pad = Rgb(40, 40, 46), Finger = Rgb(76, 160, 255),
        Palm = Rgb(255, 150, 60), Lifted = Rgb(120, 120, 128), Text = Rgb(220, 220, 220);

    private readonly string _device;
    private TouchFrame? _frame;
    private string _status = "Waiting for touchpad input...";
    private int _framesThisSecond, _reportRate;
    private double _rateWindowStart;
    private IntPtr _font;
    private uint _fontDpi;

    public InputMonitorForm(string device)
    {
        _device = device;
        var dpi = GetDpiForSystem();
        CreateHandle(new CreateParams
        {
            Caption = "TrackSwipe - input monitor",
            Style = WS_OVERLAPPEDWINDOW,
            X = CW_USEDEFAULT, Y = CW_USEDEFAULT,
            Width = (int)(576 * dpi / 96), Height = (int)(480 * dpi / 96),
        });
        SendMessageW(Handle, 0x0080 /* WM_SETICON */, 1 /* ICON_BIG */, AppIcon.Large);
        SendMessageW(Handle, 0x0080, 0 /* ICON_SMALL */, AppIcon.Small);
    }

    public event Action? FormClosed;

    public void Show() => ShowWindow(Handle, 5 /* SW_SHOW */);
    public void Activate() => SetForegroundWindow(Handle);
    public void Dispose() => DestroyHandle();

    public void Push(TouchFrame frame, MonitorStatus engine)
    {
        _frame = frame;
        _framesThisSecond++;
        if (frame.Time - _rateWindowStart >= 1)
        {
            _reportRate = _framesThisSecond;
            _framesThisSecond = 0;
            _rateWindowStart = frame.Time;
        }
        _status =
            $"{_device}\n" +
            $"Frames {_reportRate}/s    Active contacts {engine.ActiveContacts}    Gestures {(engine.Enabled ? "on" : "paused")}\n" +
            $"Phase {engine.Phase}    Axis {engine.Axis}\n" +
            $"Progress {engine.Progress:+0.00;-0.00}    Display {engine.DisplayProgress:+0.00;-0.00}    Velocity {engine.Velocity:+0.0;-0.0}/s";
        if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, false);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND:
                m.Result = 1; // painted whole, double-buffered
                return;
            case WM_PAINT:
                Paint();
                return;
            case WM_DPICHANGED:
                InvalidateRect(Handle, IntPtr.Zero, false);
                break;
            case WM_CLOSE:
                DestroyHandle();
                return;
            case WM_DESTROY:
                if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
                FormClosed?.Invoke();
                break;
        }
        base.WndProc(ref m);
    }

    private void Paint()
    {
        var ps = new PAINTSTRUCT();
        var target = BeginPaint(Handle, ref ps);
        try
        {
            GetClientRect(Handle, out var client);
            int width = client.Right, height = client.Bottom;
            if (width <= 0 || height <= 0) return;
            var dpi = GetDpiForWindow(Handle);
            var dc = CreateCompatibleDC(target);
            var bitmap = CreateCompatibleBitmap(target, width, height);
            var previousBitmap = SelectObject(dc, bitmap);
            try { Draw(dc, width, height, dpi); BitBlt(target, 0, 0, width, height, dc, 0, 0, 0x00CC0020 /* SRCCOPY */); }
            finally { SelectObject(dc, previousBitmap); DeleteObject(bitmap); DeleteDC(dc); }
        }
        finally { EndPaint(Handle, ref ps); }
    }

    private void Draw(IntPtr dc, int width, int height, uint dpi)
    {
        int Scale(double value) => (int)Math.Round(value * dpi / 96);
        Fill(dc, new RECT { Right = width, Bottom = height }, Background);
        if (_font == IntPtr.Zero || _fontDpi != dpi)
        {
            if (_font != IntPtr.Zero) DeleteObject(_font);
            _font = CreateFontW(-Scale(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
            _fontDpi = dpi;
        }
        var previousFont = SelectObject(dc, _font);
        SetBkMode(dc, 1 /* TRANSPARENT */);
        SetTextColor(dc, Text);
        var margin = Scale(14);
        var textRect = new RECT { Left = margin, Top = margin, Right = width - margin, Bottom = margin + Scale(80) };
        DrawTextW(dc, _status, -1, ref textRect, 0x0000 /* DT_LEFT */);

        if (_frame is { } frame)
        {
            int areaTop = margin * 2 + Scale(80), areaWidth = width - 2 * margin, areaHeight = height - areaTop - margin;
            if (areaWidth > 0 && areaHeight > 0)
            {
                var scale = Math.Min(areaWidth / frame.PadWidthMm, areaHeight / frame.PadHeightMm);
                var pad = new RECT { Left = margin, Top = areaTop, Right = margin + (int)(frame.PadWidthMm * scale), Bottom = areaTop + (int)(frame.PadHeightMm * scale) };
                Fill(dc, pad, Pad);
                var radius = (int)(5 * scale); // roughly a fingertip
                foreach (var contact in frame.Contacts)
                {
                    int cx = pad.Left + (int)(contact.X * scale), cy = pad.Top + (int)(contact.Y * scale);
                    var color = !contact.Tip ? Lifted : contact.Confidence ? Finger : Palm;
                    var pen = CreatePen(0, Scale(2), color);
                    var brush = contact.Tip ? CreateSolidBrush(color) : GetStockObject(5 /* NULL_BRUSH */);
                    var oldPen = SelectObject(dc, pen);
                    var oldBrush = SelectObject(dc, brush);
                    Ellipse(dc, cx - radius, cy - radius, cx + radius, cy + radius);
                    SelectObject(dc, oldPen);
                    SelectObject(dc, oldBrush);
                    DeleteObject(pen);
                    if (contact.Tip) DeleteObject(brush);
                    var label = new RECT { Left = cx - radius, Top = cy - radius, Right = cx + radius, Bottom = cy + radius };
                    SetTextColor(dc, Rgb(255, 255, 255));
                    DrawTextW(dc, contact.Id.ToString(), -1, ref label, 0x0001 | 0x0004 | 0x0020 /* CENTER | VCENTER | SINGLELINE */);
                    SetTextColor(dc, Text);
                }
            }
        }
        SelectObject(dc, previousFont);
    }

    private static void Fill(IntPtr dc, RECT rect, int color)
    {
        var brush = CreateSolidBrush(color);
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
    }

    private static int Rgb(int r, int g, int b) => r | g << 8 | b << 16;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, nint wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int length, ref RECT rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
