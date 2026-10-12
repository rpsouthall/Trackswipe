using System.Drawing.Drawing2D;
using TrackSwipe.Interop;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// Hidden windows used as thumbnail sources for things the overlay must draw between other
/// thumbnails, where its own painting can't reach (it always sits beneath them):
/// <list type="bullet">
/// <item>Backings: many apps (WinUI, Electron, browsers) leave their background to the system
/// Mica/Acrylic backdrop, which DWM thumbnails don't include, so without a backing they show
/// whatever is behind them. A solid Mica-coloured thumbnail under each window fixes that.</item>
/// <item>The highlight ring around the viewed desktop in Mission Control, which glides between
/// tiles as you swipe.</item>
/// </list>
/// </summary>
internal sealed class Swatches : IDisposable
{
    // Mica's base colours in dark and light mode, without the wallpaper tint.
    private static readonly Color DarkMica = Color.FromArgb(32, 32, 32);
    private static readonly Color LightMica = Color.FromArgb(243, 243, 243);

    private readonly SolidSwatch _dark = new(DarkMica);
    private readonly SolidSwatch _light = new(LightMica);
    private readonly Dictionary<(Size, Color), DecorationWindow> _rings = new();

    public IEnumerable<IntPtr> Handles => new[] { _dark.Handle, _light.Handle }.Concat(_rings.Values.Select(ring => ring.Handle));

    /// <summary>What to mirror underneath <paramref name="window"/> so its backdrop areas are opaque.</summary>
    public SceneItem BackingFor(SceneItem window) => (window.IsDark ? _dark : _light).Item;

    /// <summary>A rounded accent ring that fits around a tile of <paramref name="tile"/> size, plus how far it overhangs.</summary>
    public (SceneItem Item, int Overhang) Ring(Size tile, float scale, Color accent)
    {
        var overhang = (int)Math.Ceiling(5 * scale);
        var size = new Size(tile.Width + 2 * overhang, tile.Height + 2 * overhang);
        if (!_rings.TryGetValue((size, accent), out var ring))
        {
            var width = 3 * scale;
            ring = DecorationWindow.CreateSwatch(size, g =>
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(accent, width);
                var inset = width / 2 + 1;
                using var path = Rounded(new RectangleF(inset, inset, size.Width - 2 * inset, size.Height - 2 * inset), 9 * scale);
                g.DrawPath(pen, path);
            });
            _rings[(size, accent)] = ring;
        }
        return (ItemFor(ring), overhang);
    }

    public void Dispose()
    {
        _dark.Dispose();
        _light.Dispose();
        foreach (var ring in _rings.Values) ring.Dispose();
        _rings.Clear();
    }

    /// <summary>
    /// A plain window painted one colour and cloaked, so it's never seen. It's as big as the largest
    /// monitor because DWM only ever shrinks thumbnails; a small swatch stretched up draws nothing.
    /// An ordinary window rather than a layered one keeps its pixels in DWM, not in our memory.
    /// </summary>
    private sealed class SolidSwatch : NativeWindow, IDisposable
    {
        private const int WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084;
        private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_UPDATENOW = 0x100;

        private readonly IntPtr _brush;

        public SolidSwatch(Color color)
        {
            _brush = CreateSolidBrush(ColorTranslator.ToWin32(color));
            var size = new Size(Screen.AllScreens.Max(screen => screen.Bounds.Width), Screen.AllScreens.Max(screen => screen.Bounds.Height));
            CreateHandle(new CreateParams
            {
                Caption = "TrackSwipe backing",
                Width = size.Width,
                Height = size.Height,
                Style = WS_POPUP,
                ExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
            });
            var cloak = 1;
            DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref cloak, sizeof(int)); // before showing, so it never flashes
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW);
            var bounds = new Rectangle(Point.Empty, size);
            Item = new SceneItem(Handle, bounds, bounds, SceneKind.Window, -1, IsAppWindow: false);
        }

        public SceneItem Item { get; }

        public void Dispose()
        {
            DestroyHandle();
            DeleteObject(_brush);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_ERASEBKGND:
                    if (GetClientRect(Handle, out var client)) FillRect(m.WParam, ref client, _brush);
                    m.Result = 1;
                    return;
                case WM_NCHITTEST:
                    m.Result = HTTRANSPARENT;
                    return;
            }
            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr update, IntPtr region, uint flags);
    }

    private static SceneItem ItemFor(DecorationWindow swatch)
    {
        var bounds = new Rectangle(new Point(-32000, -32000), swatch.Size);
        return new SceneItem(swatch.Handle, bounds, bounds, SceneKind.Window, -1, IsAppWindow: false);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
