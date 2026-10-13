using TrackSwipe.Interop;
using System.Numerics;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// A never-activated window covering one monitor whose only content is a DirectComposition tree of live
/// window thumbnails. DWM composes it on the GPU; per frame we only move, resize, clip or fade visuals, and
/// each frame's changes reach the screen as one atomic batch. Click-through while a gesture runs;
/// clickable while Mission Control is open.
/// </summary>
internal sealed class ThumbnailSurface : NativeWindow, IDisposable
{
    private const int WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084;

    private readonly List<Sprite> _sprites = new();
    private readonly List<CompositionVisual> _overlays = new();
    private readonly Dictionary<Sprite, CompositionVisual> _parents = new();
    private readonly CompositionVisual _root;
    private readonly IntPtr _target;
    private readonly Sprite? _base;
    private readonly Swatches _swatches;
    private bool _interactive;

    /// <param name="baseItem">A black swatch to fill the monitor beneath everything, e.g. behind a dimmed wallpaper.</param>
    public ThumbnailSurface(Screen screen, SceneItem? baseItem, Swatches swatches)
    {
        _swatches = swatches;
        Monitor = screen.Bounds;
        WorkArea = screen.WorkingArea;
        IsPrimary = screen.Primary;
        Scale = GetScale(Monitor);
        WindowBounds = ShortOfFullScreen(Monitor, WorkArea);
        CreateHandle(new CreateParams
        {
            Caption = "TrackSwipe surface",
            X = WindowBounds.X,
            Y = WindowBounds.Y,
            Width = WindowBounds.Width,
            Height = WindowBounds.Height,
            Style = WS_POPUP,
            // No redirection bitmap: the window draws nothing itself, only its composition tree.
            ExStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_NOREDIRECTIONBITMAP |
                (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_VISIBLE") == "1" ? 0 : WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
        });
        _root = Composition.CreateVisual();
        _root.SetVisible(false);
        _target = Composition.CreateTarget(Handle, _root);
        if (baseItem is { } item && Create(item) is { } fill)
        {
            _base = fill;
            _root.Add(fill.Visual, onTop: false);
            fill.Place(new Rectangle(Point.Empty, Monitor.Size));
        }
    }

    public Rectangle Monitor { get; }

    public Rectangle WorkArea { get; }

    /// <summary>The surface's window: the monitor less one pixel, so the shell doesn't take it for a full-screen app.</summary>
    public Rectangle WindowBounds { get; }

    public bool IsPrimary { get; }

    /// <summary>
    /// A window covering every pixel of a monitor counts as a full-screen app to the shell, which then turns
    /// on Do Not Disturb (when set to for full-screen apps) for as long as the overlay is up, and the
    /// mirrored taskbar shows its bell. One pixel short is enough. Leave out the row or column beside the
    /// taskbar, where the real taskbar matches its mirror; the origin stays put, so coordinates don't change.
    /// </summary>
    public static Rectangle ShortOfFullScreen(Rectangle monitor, Rectangle workArea) =>
        workArea.Right < monitor.Right && workArea.Height == monitor.Height
            ? new Rectangle(monitor.X, monitor.Y, monitor.Width - 1, monitor.Height)
            : new Rectangle(monitor.X, monitor.Y, monitor.Width, monitor.Height - 1);

    /// <summary>Monitor scale factor (1.0 = 96 DPI).</summary>
    public float Scale { get; }

    public int SpriteCount => _sprites.Count;
    /// <summary>Changes whenever shared thumbnails are cleared, invalidating a cached slide.</summary>
    public int Generation { get; private set; }

    public bool IsShown { get; private set; }

    /// <summary>Raised with client coordinates while interactive.</summary>
    public event Action<ThumbnailSurface, Point>? MouseMoved;

    public event Action<ThumbnailSurface, Point>? Clicked;
    public event Action<ThumbnailSurface, Point>? PointerPressed;
    public event Func<ThumbnailSurface, Point, bool>? PointerReleased;
    public event Action<ThumbnailSurface>? CaptureLost;
    public event Action<int>? HotKeyPressed;

    public void CapturePointer() => SetCapture(Handle);
    public void ReleasePointer() => ReleaseCapture();

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
        if (Create(item) is not { } sprite) return null;
        _root.Add(sprite.Visual);
        _sprites.Add(sprite);
        RaiseOverlays();
        return sprite;
    }

    /// <summary>
    /// Moves sprites above all others, in the given bottom-to-top order. It's one composition batch, so no
    /// frame ever shows a partial order.
    /// </summary>
    public void Restack(IReadOnlyList<Sprite> bottomToTop)
    {
        foreach (var sprite in bottomToTop)
        {
            if (!_sprites.Remove(sprite)) continue;
            (_parents.Remove(sprite, out var parent) ? parent : _root).Remove(sprite.Visual);
            _root.Add(sprite.Visual);
            _sprites.Add(sprite);
        }
        RaiseOverlays();
    }

    public void AddOverlay(CompositionVisual visual) { _overlays.Add(visual); _root.Add(visual); }
    public CompositionVisual CreateGroup()
    {
        var group = Composition.CreateVisual();
        group.SetVisible(true);
        group.UseMultipliedOpacity();
        _root.Add(group);
        RaiseOverlays();
        return group;
    }

    public void RaiseGroup(CompositionVisual group)
    {
        _root.Remove(group); _root.Add(group); RaiseOverlays();
    }

    public void Group(Sprite sprite, CompositionVisual group)
    {
        var parent = _parents.GetValueOrDefault(sprite, _root);
        if (parent == group) return;
        parent.Remove(sprite.Visual);
        group.Add(sprite.Visual);
        _parents[sprite] = group;
    }

    public void Ungroup(CompositionVisual group)
    {
        foreach (var sprite in _sprites)
        {
            if (_parents.GetValueOrDefault(sprite) != group) continue;
            group.Remove(sprite.Visual); _root.Add(sprite.Visual); _parents.Remove(sprite);
        }
        RaiseOverlays();
    }

    public void RemoveGroup(CompositionVisual group)
    {
        if (_root.Pointer != IntPtr.Zero) { Ungroup(group); _root.Remove(group); }
        group.Release();
    }
    public void RaiseOverlay(CompositionVisual visual)
    {
        if (!_overlays.Remove(visual)) return;
        _overlays.Add(visual);
        _root.Remove(visual); _root.Add(visual);
    }
    public void RemoveOverlay(CompositionVisual visual) { if (_overlays.Remove(visual) && _root.Pointer != IntPtr.Zero) _root.Remove(visual); }
    private void RaiseOverlays()
    {
        foreach (var visual in _overlays) { _root.Remove(visual); _root.Add(visual); }
    }

    public void Remove(Sprite sprite)
    {
        if (!_sprites.Remove(sprite)) return;
        (_parents.Remove(sprite, out var parent) ? parent : _root).Remove(sprite.Visual);
        sprite.Release();
    }

    public void Clear()
    {
        foreach (var sprite in _sprites)
        {
            (_parents.Remove(sprite, out var parent) ? parent : _root).Remove(sprite.Visual);
            sprite.Release();
        }
        _sprites.Clear();
        Generation++;
    }

    public void Show()
    {
        // Commit the placed thumbnails first: until the batch lands, the window shows its last committed
        // state, which is the empty tree it was hidden with, never stale content.
        _root.SetVisible(true);
        _root.SetOpacity(1); // detach a previous handoff fade
        Composition.Flush();
        FramePerf.MarkShown();
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW); // show and raise in one call
        IsShown = true;
    }

    public void Hide()
    {
        Interactive = false;
        _root.SetVisible(false);
        Composition.Flush();
        ShowWindow(Handle, SW_HIDE);
        IsShown = false;
    }

    public void FadeOut(long begin, double duration)
    {
        using var animation = CompositorMotion.Curve(new[]
        {
            new SpriteFrame(0, RectangleF.Empty, 1, null),
            new SpriteFrame(duration, RectangleF.Empty, 0, null),
        }, frame => frame.Opacity, begin);
        _root.AnimateOpacity(animation);
    }

    public void Dispose()
    {
        Clear();
        _base?.Release();
        _root.Release();
        if (_target != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(_target);
        Composition.Flush();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND: // nothing to erase: there is no redirection bitmap
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
            case WM_LBUTTONDOWN when _interactive:
                PointerPressed?.Invoke(this, PointFromLParam(m.LParam));
                return;
            case WM_LBUTTONUP when _interactive:
                var point = PointFromLParam(m.LParam);
                if (PointerReleased?.Invoke(this, point) != true) Clicked?.Invoke(this, point);
                return;
            case WM_CAPTURECHANGED:
            case WM_CANCELMODE:
                CaptureLost?.Invoke(this);
                return;
            case WM_HOTKEY:
                HotKeyPressed?.Invoke(m.WParam.ToInt32());
                return;
        }
        base.WndProc(ref m);
    }

    private Sprite? Create(SceneItem item)
    {
        if (_swatches.TryGet(item, out var swatch))
        {
            var image = Composition.CreateVisual();
            using var native = image.Borrow();
            native.SetContent(swatch.Content).CheckError();
            native.SetBorderMode(Vortice.DirectComposition.BorderMode.Hard).CheckError();
            native.SetBitmapInterpolationMode(Vortice.DirectComposition.BitmapInterpolationMode.Linear).CheckError();
            return new Sprite(this, image, IntPtr.Zero, item, swatch.Size);
        }
        return Composition.TryCreateThumbnail(Handle, item.Window, out var content, out var thumbnail)
            ? new Sprite(this, content!, thumbnail, item) : null;
    }

    private static Point PointFromLParam(IntPtr lParam) =>
        new((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
}

/// <summary>
/// One live thumbnail on a surface. Positions are in the surface's client coordinates.
/// </summary>
/// <remarks>
/// The thumbnail stays at its source resolution and a child visual scales it, including at rest.
/// Scale, position, clip and opacity then reach DWM in the same composition batch, without independent
/// thumbnail resizes racing the start or end of motion. The compositor filters the scaled content.
/// </remarks>
internal sealed class Sprite
{
    private readonly ThumbnailSurface _surface;
    private readonly CompositionVisual _content;
    private readonly CompositionVisual _scaler;
    private readonly SpriteState _state = new();
    private Size _rasterSize = new(1, 1);
    private SizeF _scale = new(1, 1);
    private bool _animated;
    private static bool _resizeFailureLogged;
    private IntPtr _thumbnail;
    private CompositionVisual? _sharp;
    private IntPtr _sharpThumbnail;
    private bool _sharpShown, _scaledAsleep;

    internal Sprite(ThumbnailSurface surface, CompositionVisual content, IntPtr thumbnail, SceneItem item, Size? gpuSize = null)
    {
        _surface = surface;
        _content = content;
        _thumbnail = thumbnail;
        Item = item;
        Visual = Composition.CreateVisual();
        Visual.SetVisible(false); // until placed
        Visual.UseMultipliedOpacity(); // one child: fading it directly needs no intermediate texture
        _scaler = Composition.CreateVisual();
        using (var native = _content.Borrow())
            native.SetBitmapInterpolationMode(Vortice.DirectComposition.BitmapInterpolationMode.Linear).CheckError();
        _scaler.Add(content);
        Visual.Add(_scaler);
        SourceSize = gpuSize ?? (DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0 ? new Size(size.Width, size.Height) : item.Frame.Size);
        if (gpuSize is { } raster) _rasterSize = raster;
        var home = PlaceUnscaled(item, SourceSize);
        home.Offset(-surface.Monitor.X, -surface.Monitor.Y);
        Home = home;
    }

    /// <summary>The container visual: position, clip and opacity.</summary>
    internal CompositionVisual Visual { get; }

    public ThumbnailSurface Surface => _surface;

    public SceneItem Item { get; private set; }

    /// <summary>A desktop index change leaves the window texture and geometry valid.</summary>
    public bool TryReuse(SceneItem item)
    {
        if ((Item with { Desktop = item.Desktop }) != item) return false;
        Item = item;
        return true;
    }

    /// <summary>Size of the thumbnail's content: the window's visible frame, not its invisible borders.</summary>
    public Size SourceSize { get; private set; }

    public void RefreshSourceSize()
    {
        if (_thumbnail != IntPtr.Zero && DwmQueryThumbnailSourceSize(_thumbnail, out var size) == 0 && size.Width > 0 && size.Height > 0)
            SourceSize = new Size(size.Width, size.Height);
    }

    /// <summary>Where the thumbnail lands pixel-for-pixel on top of the real window.</summary>
    public Rectangle Home { get; }

    /// <summary>
    /// Draws the whole window scaled into <paramref name="destination"/>, showing only the part inside
    /// <paramref name="clip"/> (a desktop or tile is its own viewport: nothing may bleed into its neighbours).
    /// Only what changed is sent.
    /// </summary>
    public void Place(Rectangle destination, byte opacity = 255, Rectangle? clip = null) =>
        Place(destination, opacity, clip is { } viewport ? (RectangleF)viewport : null);

    public void Place(RectangleF destination, byte opacity, RectangleF? clip) =>
        Place(destination, opacity, clip, new RectangleF(PointF.Empty, _surface.Monitor.Size));

    /// <param name="extent">Where the sprite can show in its parent's coordinates; a moving group spans more than the monitor.</param>
    public void Place(RectangleF destination, byte opacity, RectangleF? clip, RectangleF extent)
    {
        var wasAnimated = _animated;
        _animated = false;
        var changes = _state.Place(destination, opacity, clip, extent);
        if (wasAnimated) changes |= SpriteChanges.Location | SpriteChanges.Clip | SpriteChanges.Opacity | SpriteChanges.Visibility;
        if (_sharp is not null && (changes.HasFlag(SpriteChanges.Size) || !_state.Visible)) DropSharp(); // same commit as the new scale
        if (!_state.Visible)
        {
            if (changes.HasFlag(SpriteChanges.Visibility)) Visual.SetVisible(false);
            return;
        }
        // DWM resizes are independent of a composition commit. Keep the source raster even at rest:
        // switching to a filtered overview raster and back lets one frame combine the wrong raster
        // with the new GPU scale, especially when a click starts a compositor-owned close.
        var raster = ThumbnailSizing.RasterSize(SourceSize);
        if (raster != _rasterSize)
        {
            var properties = new DWM_THUMBNAIL_PROPERTIES
            {
                Flags = DWM_TNP_RECTDESTINATION | DWM_TNP_ENABLE3D,
                Destination = new RECT { Right = raster.Width, Bottom = raster.Height },
            };
            var hr = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
            if (hr != 0 && !_resizeFailureLogged)
            {
                _resizeFailureLogged = true;
                Log.Write($"Resizing a thumbnail of '{Item.Window:X}' failed: 0x{hr:X8}.");
            }
            if (hr == 0) _rasterSize = raster;
            FramePerf.ThumbnailUpdates++;
        }
        var scale = new SizeF(_state.DrawSize.Width / _rasterSize.Width, _state.DrawSize.Height / _rasterSize.Height);
        if (scale != _scale || wasAnimated)
        {
            _scaler.SetScale(scale.Width, scale.Height);
            _scale = scale;
        }
        if (changes.HasFlag(SpriteChanges.Location)) Visual.SetTranslation(_state.Offset.X, _state.Offset.Y);
        if (changes.HasFlag(SpriteChanges.Clip)) Visual.SetClip(_state.Clip);
        if (changes.HasFlag(SpriteChanges.Opacity)) Visual.SetOpacity(_state.Opacity / 255f);
        if (changes.HasFlag(SpriteChanges.Visibility)) Visual.SetVisible(_state.Visible);
    }

    /// <summary>
    /// For a shrunken window at rest: adds a second thumbnail that DWM draws at exactly the shown size, so it's
    /// filtered (the compositor point-samples a scaled thumbnail, and small text shimmers). It goes in beneath
    /// the scaled copy, so it's composed before <see cref="RevealSharp"/> uncovers it in a later commit. Swapping
    /// visuals, rather than resizing the one thumbnail, never lets a DWM resize race a composition commit.
    /// </summary>
    internal bool PrepareSharp()
    {
        // Minimized windows show DWM's cached image, often smaller than the overview slot: nothing to gain.
        if (_sharp is not null || _animated || !_state.Visible || _thumbnail == IntPtr.Zero || Item.IsMinimized) return false;
        var size = Size.Round(_state.DrawSize);
        if (size.Width < 1 || size.Height < 1 || size.Width >= _rasterSize.Width * 0.98) return false; // not noticeably shrunk
        if (!Composition.TryCreateThumbnail(_surface.Handle, Item.Window, out var content, out var id)) return false;
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = DWM_TNP_RECTDESTINATION | DWM_TNP_ENABLE3D,
            Destination = new RECT { Right = size.Width, Bottom = size.Height },
        };
        if (DwmUpdateThumbnailProperties(id, ref properties) != 0)
        {
            content!.Release();
            DwmUnregisterThumbnail(id);
            return false;
        }
        _sharp = content;
        _sharpThumbnail = id;
        Visual.Add(content!, onTop: false);
        return true;
    }

    /// <summary>
    /// Raises the sharp copy above the scaled one, which then sleeps: fully transparent, so DWM stops drawing
    /// it underneath (both drawn cost about 15% more compositor GPU time while the overview is open).
    /// </summary>
    internal void RevealSharp()
    {
        if (_sharp is null) return;
        if (!_sharpShown)
        {
            Visual.Remove(_sharp);
            Visual.Add(_sharp, onTop: true);
            _sharpShown = true;
        }
        SleepScaled();
    }

    private void SleepScaled()
    {
        if (!_sharpShown || _scaledAsleep) return;
        _scaler.SetOpacity(0);
        _scaledAsleep = true;
    }

    /// <summary>
    /// Before anything can move: brings the covered scaled copy back underneath the sharp one. DWM draws a
    /// thumbnail that becomes visible again blank for its first frame, so this must happen at least a frame
    /// before <see cref="DropSharp"/> uncovers it. True if the copy was asleep.
    /// </summary>
    internal bool WakeScaled()
    {
        if (!_scaledAsleep) return false;
        _scaler.SetOpacity(1);
        _scaledAsleep = false;
        return true;
    }

    /// <summary>Back to the scaled copy for motion; called in the same batch as the change that moves it.</summary>
    internal void DropSharp()
    {
        if (_sharp is null) return;
        if (_scaledAsleep) { _scaler.SetOpacity(1); _scaledAsleep = false; } // unwoken: may show one blank frame
        Visual.Remove(_sharp);
        _sharp.Release();
        DwmUnregisterThumbnail(_sharpThumbnail);
        _sharp = null;
        _sharpThumbnail = IntPtr.Zero;
        _sharpShown = false;
    }

    public void Animate(IReadOnlyList<SpriteFrame> frames, long begin)
    {
        DropSharp();
        var first = frames[0];
        Place(first.Bounds, (byte)Math.Clamp(first.Opacity * 255, 0, 255), first.Viewport);
        _animated = true;
        Visual.SetVisible(true);
        using var position = Composition.Device.CreateMatrixTransform();
        var identity = Matrix3x2.Identity;
        position.SetMatrix(ref identity).CheckError();
        using var x = CompositorMotion.Curve(frames, frame => frame.Bounds.X, begin);
        using var y = CompositorMotion.Curve(frames, frame => frame.Bounds.Y, begin);
        position.SetMatrixElement(2, 0, x).CheckError();
        position.SetMatrixElement(2, 1, y).CheckError();
        Visual.SetTransform(position);
        using var size = Composition.Device.CreateMatrixTransform();
        size.SetMatrix(ref identity).CheckError();
        using var width = CompositorMotion.Curve(frames, frame => frame.Bounds.Width / _rasterSize.Width, begin);
        using var height = CompositorMotion.Curve(frames, frame => frame.Bounds.Height / _rasterSize.Height, begin);
        size.SetMatrixElement(0, 0, width).CheckError();
        size.SetMatrixElement(1, 1, height).CheckError();
        _scaler.SetTransform(size);
        using var opacity = CompositorMotion.Curve(frames, frame => frame.Opacity, begin);
        Visual.AnimateOpacity(opacity);
        RectangleF ClipFor(SpriteFrame frame)
        {
            var shown = RectangleF.Intersect(frame.Bounds, new RectangleF(PointF.Empty, _surface.Monitor.Size));
            if (frame.Viewport is { } viewport) shown.Intersect(viewport);
            return shown.Width <= 0 || shown.Height <= 0 ? RectangleF.Empty
                : new RectangleF(shown.X - frame.Bounds.X, shown.Y - frame.Bounds.Y, shown.Width, shown.Height);
        }
        using var clip = Composition.Device.CreateRectangleClip();
        using var left = CompositorMotion.Curve(frames, frame => ClipFor(frame).Left, begin);
        using var top = CompositorMotion.Curve(frames, frame => ClipFor(frame).Top, begin);
        using var right = CompositorMotion.Curve(frames, frame => ClipFor(frame).Right, begin);
        using var bottom = CompositorMotion.Curve(frames, frame => ClipFor(frame).Bottom, begin);
        clip.SetLeft(left).CheckError(); clip.SetTop(top).CheckError();
        clip.SetRight(right).CheckError(); clip.SetBottom(bottom).CheckError();
        Visual.SetClip(clip);
    }

    /// <summary>Frees the visuals and the DWM thumbnail. The caller has already taken it out of the tree.</summary>
    internal void Release()
    {
        DropSharp();
        Visual.Release();
        _scaler.Release();
        _content.Release();
        if (_thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
    }

    /// <summary>
    /// DWM thumbnails of framed windows cover only the visible frame, not the invisible resize
    /// borders included in the window rect, so sizing to the window rect would stretch them.
    /// When only the outer rect is known (a minimized window's restore position), the frame sits
    /// inside it the way Windows 11 draws it: invisible borders left, right and bottom, none on top.
    /// </summary>
    private static Rectangle PlaceUnscaled(SceneItem item, Size source)
    {
        // A minimized thumbnail may be only a small cached image. Its restore bounds,
        // rather than that cache's dimensions, determine where the window will land.
        if (item.IsMinimized) return item.Frame;
        if (source == item.Frame.Size) return item.Frame;
        if (source == item.Bounds.Size) return item.Bounds;
        var bounds = item.Frame;
        if (source.Width <= bounds.Width && source.Height <= bounds.Height)
            return new Rectangle(bounds.X + (bounds.Width - source.Width) / 2, bounds.Y, source.Width, source.Height);
        return new Rectangle(bounds.Location, source);
    }
}
