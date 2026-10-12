using TrackSwipe.Interop;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// A never-activated window covering one monitor, filled with live DWM thumbnails of real windows.
/// DWM composes them on the GPU; per frame we only move, scale or fade them. Click-through while
/// a gesture runs; clickable while Mission Control is open.
/// </summary>
internal sealed class ThumbnailSurface : NativeWindow, IDisposable
{
    private const int WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084;

    private readonly List<Sprite> _sprites = new();
    private bool _interactive;

    public ThumbnailSurface(Screen screen)
    {
        Monitor = screen.Bounds;
        WorkArea = screen.WorkingArea;
        IsPrimary = screen.Primary;
        Scale = GetScale(Monitor);
        CreateHandle(new CreateParams
        {
            Caption = "TrackSwipe surface",
            X = Monitor.X,
            Y = Monitor.Y,
            Width = Monitor.Width,
            Height = Monitor.Height,
            Style = WS_POPUP,
            ExStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT |
                (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_VISIBLE") == "1" ? 0 : WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
        });
    }

    public Rectangle Monitor { get; }

    public Rectangle WorkArea { get; }

    public bool IsPrimary { get; }

    /// <summary>Monitor scale factor (1.0 = 96 DPI).</summary>
    public float Scale { get; }

    public int SpriteCount => _sprites.Count;
    /// <summary>Changes whenever shared thumbnails are cleared, invalidating a cached slide.</summary>
    public int Generation { get; private set; }

    public bool IsShown { get; private set; }

    /// <summary>Raised with client coordinates while interactive.</summary>
    public event Action<ThumbnailSurface, Point>? MouseMoved;

    public event Action<ThumbnailSurface, Point>? Clicked;

    /// <summary>Clickable (Mission Control) or click-through (gestures). Never takes focus either way.</summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            var style = GetWindowLongW(Handle, GWL_EXSTYLE);
            SetWindowLongW(Handle, GWL_EXSTYLE, value ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT);
        }
    }

    /// <summary>Mirrors a window. Later sprites draw on top of earlier ones. Null if DWM refuses (the window closed).</summary>
    public Sprite? Add(SceneItem item)
    {
        if (DwmRegisterThumbnail(Handle, item.Window, out var thumbnail) != 0) return null;
        var sprite = new Sprite(this, thumbnail, item);
        _sprites.Add(sprite);
        return sprite;
    }

    /// <summary>
    /// Moves sprites above all others, in the given bottom-to-top order, by registering their thumbnails
    /// again. New thumbnails start hidden and are revealed top first, so each appears beneath the ones
    /// already restacked above it; the old ones go last. No frame ever shows a wrong order or a gap.
    /// </summary>
    public void Restack(IReadOnlyList<Sprite> bottomToTop)
    {
        var fresh = new List<(Sprite Sprite, IntPtr Thumbnail)>();
        foreach (var sprite in bottomToTop)
        {
            if (DwmRegisterThumbnail(Handle, sprite.Item.Window, out var thumbnail) != 0) continue;
            Sprite.Hide(thumbnail);
            fresh.Add((sprite, thumbnail));
        }

        var old = new List<IntPtr>(fresh.Count);
        for (var i = fresh.Count - 1; i >= 0; i--) old.Add(fresh[i].Sprite.Rebind(fresh[i].Thumbnail));
        foreach (var thumbnail in old) DwmUnregisterThumbnail(thumbnail);
        foreach (var (sprite, _) in fresh)
        {
            _sprites.Remove(sprite);
            _sprites.Add(sprite);
        }
    }

    public void Remove(Sprite sprite)
    {
        if (!_sprites.Remove(sprite)) return;
        DwmUnregisterThumbnail(sprite.Handle);
    }

    public void Clear()
    {
        foreach (var sprite in _sprites) DwmUnregisterThumbnail(sprite.Handle);
        _sprites.Clear();
        Generation++;
    }

    public void Show()
    {
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        IsShown = true;
    }

    public void Hide()
    {
        Interactive = false;
        ShowWindow(Handle, SW_HIDE);
        IsShown = false;
    }

    public void Dispose()
    {
        Clear();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND: // black shows wherever no thumbnail covers, e.g. behind a dimmed wallpaper
                if (GetClientRect(Handle, out var client)) FillRect(m.WParam, ref client, GetStockObject(BLACK_BRUSH));
                m.Result = 1;
                return;
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_NCHITTEST:
                m.Result = _interactive ? HTCLIENT : HTTRANSPARENT;
                return;
            case WM_SETCURSOR when _interactive:
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                m.Result = 1;
                return;
            case WM_MOUSEMOVE when _interactive:
                MouseMoved?.Invoke(this, PointFromLParam(m.LParam));
                return;
            case WM_LBUTTONUP when _interactive:
                Clicked?.Invoke(this, PointFromLParam(m.LParam));
                return;
        }
        base.WndProc(ref m);
    }

    private static Point PointFromLParam(IntPtr lParam) =>
        new((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
}

/// <summary>One live thumbnail on a surface. Positions are in the surface's client coordinates.</summary>
internal sealed class Sprite
{
    private readonly ThumbnailSurface _surface;
    private readonly ThumbnailUpdateCache _cache = new();
    private readonly Rectangle _fullSource;

    internal Sprite(ThumbnailSurface surface, IntPtr handle, SceneItem item)
    {
        _surface = surface;
        Handle = handle;
        Item = item;
        SourceSize = DwmQueryThumbnailSourceSize(handle, out var size) == 0 ? new Size(size.Width, size.Height) : item.Frame.Size;
        var home = PlaceUnscaled(item, SourceSize);
        home.Offset(-surface.Monitor.X, -surface.Monitor.Y);
        Home = home;
        // Explicit DWM source rectangles use window coordinates, including the resize borders.
        // Its default thumbnail excludes those borders. Preserve that origin when cropping.
        var sourceOrigin = item.IsMinimized ? Point.Empty : new Point(
            Math.Max(0, home.X + surface.Monitor.X - item.Bounds.X),
            Math.Max(0, home.Y + surface.Monitor.Y - item.Bounds.Y));
        _fullSource = new Rectangle(sourceOrigin, SourceSize);

        Hide(handle); // until placed: otherwise DWM may draw it for a frame wherever its defaults put it
    }

    internal static void Hide(IntPtr thumbnail)
    {
        var hidden = new DWM_THUMBNAIL_PROPERTIES { Flags = DWM_TNP_VISIBLE, Visible = 0 };
        DwmUpdateThumbnailProperties(thumbnail, ref hidden);
    }

    public IntPtr Handle { get; private set; }

    public ThumbnailSurface Surface => _surface;

    public SceneItem Item { get; }

    /// <summary>Size of the thumbnail's content: the window's visible frame, not its invisible borders.</summary>
    public Size SourceSize { get; }

    /// <summary>Where the thumbnail lands pixel-for-pixel on top of the real window.</summary>
    public Rectangle Home { get; }

    /// <summary>
    /// Places the thumbnail. <paramref name="source"/> crops the window (in thumbnail pixels) before
    /// scaling it into <paramref name="destination"/>. Skips the DWM call when nothing changed.
    /// </summary>
    public void Place(Rectangle destination, byte opacity = 255, Rectangle? source = null, Rectangle? clip = null)
    {
        // A desktop or tile is its own viewport: nothing may bleed into its neighbours.
        if (clip is { } viewport)
        {
            if (!ThumbnailClip.TryClip(destination, viewport, source ?? _fullSource, out var cropped, out var crop))
            {
                SetHidden();
                return;
            }
            if (cropped != destination)
            {
                destination = cropped;
                source = crop;
            }
        }
        var visible = opacity > 0 && destination.Width > 0 && destination.Height > 0
            && destination.IntersectsWith(new Rectangle(Point.Empty, _surface.Monitor.Size));
        if (!visible)
        {
            SetHidden();
            return;
        }
        var previousSource = _cache.Source;
        if (!_cache.Update(destination, opacity, source, visible)) return;

        // Leave the source rectangle unset unless cropping: DWM's default is exactly the visible window,
        // whereas an explicit rectangle is measured differently and shifts the content.
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY | (source is null && previousSource is null ? 0 : DWM_TNP_RECTSOURCE),
            Destination = ToRect(destination),
            // Omitting RECTSOURCE would retain the last crop when the whole window re-enters.
            Source = ToRect(source ?? _fullSource),
            Opacity = opacity,
            Visible = visible ? 1 : 0,
            SourceClientAreaOnly = 0,
        };
        DwmUpdateThumbnailProperties(Handle, ref properties);
        FramePerf.ThumbnailUpdates++;
    }

    private void SetHidden()
    {
        // Hidden geometry is irrelevant to DWM. Don't send new off-screen rectangles every frame.
        if (!_cache.Update(default, 0, null, false)) return;
        Hide(Handle);
        FramePerf.ThumbnailUpdates++;
    }

    /// <summary>Switches to a newly registered thumbnail, placed where this one was. Returns the old one.</summary>
    internal IntPtr Rebind(IntPtr handle)
    {
        var old = Handle;
        Handle = handle;
        var wasVisible = _cache.Visible;
        _cache.Reset();
        if (wasVisible) Place(_cache.Destination, _cache.Opacity, _cache.Source);
        else Hide(handle);
        return old;
    }

    /// <summary>
    /// DWM thumbnails of framed windows cover only the visible frame, not the invisible resize
    /// borders included in the window rect, so sizing to the window rect would stretch them.
    /// When only the outer rect is known (a minimized window's restore position), the frame sits
    /// inside it the way Windows 11 draws it: invisible borders left, right and bottom, none on top.
    /// </summary>
    private static Rectangle PlaceUnscaled(SceneItem item, Size source)
    {
        if (source == item.Frame.Size) return item.Frame;
        if (source == item.Bounds.Size) return item.Bounds;
        var bounds = item.Frame;
        if (source.Width <= bounds.Width && source.Height <= bounds.Height)
            return new Rectangle(bounds.X + (bounds.Width - source.Width) / 2, bounds.Y, source.Width, source.Height);
        return new Rectangle(bounds.Location, source);
    }

    private static RECT ToRect(Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
}
