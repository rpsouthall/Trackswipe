using System.Runtime.InteropServices;

namespace TrackSwipe.Win32;

/// <summary>One entry in the tray menu. <see cref="Children"/> makes a submenu; null text a separator.</summary>
internal sealed class TrayMenuItem
{
    public TrayMenuItem(string? text, Action? click = null, params TrayMenuItem[] children)
    {
        Text = text;
        Click = click;
        Children = children;
    }

    public static TrayMenuItem Separator => new(null);
    public string? Text { get; set; }
    public bool Checked { get; set; }
    public bool Enabled { get; set; } = true;
    public Action? Click { get; }
    public TrayMenuItem[] Children { get; }
}

/// <summary>
/// The notification-area icon and its menu, through Shell_NotifyIcon (version 4). The menu is built each time
/// it opens, so items can change freely. Re-adds itself when Explorer restarts.
/// </summary>
internal sealed unsafe class TrayIcon : NativeWindow, IDisposable
{
    private const int WM_TRAY = 0x8000 + 120, WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, NIN_BALLOONUSERCLICK = 0x0405;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    /// <summary>Posted by the installer to close a running copy.</summary>
    public const string QuitMessage = "TrackSwipe.Quit";
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly uint _quit = RegisterWindowMessageW(QuitMessage);
    private IntPtr _icon;
    private string _text = "";
    private bool _added;

    public TrayIcon(IntPtr icon, string text, IReadOnlyList<TrayMenuItem> menu)
    {
        Menu = menu;
        // A hidden top-level window rather than message-only: menus need a window that can be foreground.
        CreateHandle(new CreateParams { Caption = "TrackSwipe tray", Style = unchecked((int)0x80000000) /* WS_POPUP */ });
        // Running as administrator, Windows drops messages from Explorer and the installer unless allowed.
        foreach (var message in new[] { (uint)WM_TRAY, _taskbarCreated, _quit })
            ChangeWindowMessageFilterEx(Handle, message, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        _icon = icon;
        _text = text;
        Add();
    }

    public IReadOnlyList<TrayMenuItem> Menu { get; }
    public event Action? DoubleClick;
    public event Action? BalloonClicked;
    public event Action? QuitRequested;
    /// <summary>Explorer restarted (the taskbar was recreated); the icon is already back.</summary>
    public event Action? TaskbarRecreated;

    public void ShowBalloon(string title, string text)
    {
        var data = Data(NIF_INFO);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, &data);
            _added = false;
        }
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_TRAY)
        {
            switch ((int)(m.LParam.ToInt64() & 0xFFFF))
            {
                case WM_CONTEXTMENU:
                    ShowMenu((short)(m.WParam.ToInt64() & 0xFFFF), (short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                    break;
                case WM_LBUTTONDBLCLK:
                    DoubleClick?.Invoke();
                    break;
                case NIN_BALLOONUSERCLICK:
                    BalloonClicked?.Invoke();
                    break;
            }
            return;
        }
        if (m.Msg == (int)_quit)
        {
            QuitRequested?.Invoke();
            return;
        }
        if (m.Msg == (int)_taskbarCreated) // Explorer restarted: the icon is gone
        {
            _added = false;
            Add();
            TaskbarRecreated?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, &data) != 0;
        data.uVersion = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private NOTIFYICONDATAW Data(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
        };
        Copy(_text, data.szTip, 128);
        return data;
    }

    private void ShowMenu(int x, int y)
    {
        var commands = new List<Action?>();
        var menu = Build(Menu, commands);
        try
        {
            SetForegroundWindow(Handle); // so the menu closes when the user clicks elsewhere
            var command = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */, x, y, Handle, IntPtr.Zero);
            PostMessageW(Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL, as the shell documentation asks
            if (command > 0 && command <= commands.Count) commands[command - 1]?.Invoke();
        }
        finally { DestroyMenu(menu); } // destroys submenus too
    }

    private static IntPtr Build(IReadOnlyList<TrayMenuItem> items, List<Action?> commands)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.Text is null) { AppendMenuW(menu, 0x0800 /* MF_SEPARATOR */, 0, null); continue; }
            var flags = (item.Checked ? 0x0008u /* MF_CHECKED */ : 0) | (item.Enabled ? 0 : 0x0001u /* MF_GRAYED */);
            if (item.Children.Length > 0)
            {
                AppendMenuW(menu, flags | 0x0010 /* MF_POPUP */, (nuint)Build(item.Children, commands), item.Text);
                continue;
            }
            commands.Add(item.Click);
            AppendMenuW(menu, flags, (nuint)commands.Count, item.Text);
        }
        return menu;
    }

    private static void Copy(string text, char* destination, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < length; i++) destination[i] = text[i];
        destination[length] = '\0';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion; // union with uTimeout
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // By pointer: the struct is blittable as it stands (UTF-16 buffers), so nothing is marshalled or narrowed to ANSI.
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr filter);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Draws the tray icon (a touchpad with a double-headed arrow) straight into pixels: no GDI+.</summary>
internal static unsafe class TrayIconImage
{
    public static IntPtr Create(bool lightTaskbar)
    {
        var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
        if (size <= 0) size = 16;
        var width = Math.Max(1.5f, size / 12f);
        float inset = size * 0.1f, top = size * 0.2f, bottom = size * 0.8f, mid = size / 2f;
        float left = size * 0.28f, right = size * 0.72f, head = size * 0.12f;
        var segments = new (float X1, float Y1, float X2, float Y2)[]
        {
            (inset, top, size - inset, top), (size - inset, top, size - inset, bottom),
            (size - inset, bottom, inset, bottom), (inset, bottom, inset, top),
            (left, mid, right, mid),
            (left + head, mid - head, left, mid), (left, mid, left + head, mid + head),
            (right - head, mid - head, right, mid), (right, mid, right - head, mid + head),
        };
        byte shade = lightTaskbar ? (byte)28 : (byte)255;

        var header = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        var pixels = (uint*)bits;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Coverage from the distance to the nearest stroke (round caps), for a smooth one-pixel edge.
                float px = x + 0.5f, py = y + 0.5f, distance = float.MaxValue;
                foreach (var s in segments) distance = Math.Min(distance, Distance(px, py, s.X1, s.Y1, s.X2, s.Y2));
                var alpha = Math.Clamp(width / 2 + 0.5f - distance, 0, 1);
                var a = (byte)Math.Round(alpha * 255);
                var c = (byte)Math.Round(alpha * shade); // premultiplied
                pixels[y * size + x] = (uint)(a << 24 | c << 16 | c << 8 | c);
            }
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private static float Distance(float px, float py, float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1, dy = y2 - y1, length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / length, 0, 1);
        float cx = x1 + t * dx - px, cy = y1 + t * dy - py;
        return MathF.Sqrt(cx * cx + cy * cy);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public int fIcon, xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
