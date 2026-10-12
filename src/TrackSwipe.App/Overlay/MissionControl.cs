using System.Drawing.Drawing2D;
using Microsoft.Win32;
using TrackSwipe.Core;
using TrackSwipe.Desktops;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// macOS-style Mission Control. Openness 0 is the desktop exactly as it is; 1 is the overview:
/// the current desktop's windows spread out without overlapping, a bar of live desktop
/// miniatures across the top, the wallpaper dimmed. Everything in between is interpolated, so
/// a gesture can drive it continuously and reverse it.
/// </summary>
internal sealed class MissionControl : IDisposable
{
    private enum State { Hidden, Tracking, Open, Closing }

    private enum HitKind { None, Window, Desktop, AddDesktop }

    private readonly record struct Hit(HitKind Kind, Part? Window = null, int Desktop = -1);

    private const int EscapeHotKey = 1;
    private const byte DimmedWallpaper = 140;

    private readonly Func<IReadOnlyList<ThumbnailSurface>> _surfaces;
    private readonly Func<DesktopScene> _capture;
    private readonly DesktopSwitcher? _switcher;
    private readonly Swatches _swatches;
    private readonly Dictionary<ThumbnailSurface, DecorationWindow> _decorations = new();
    private readonly List<Plan> _plans = new();
    private readonly Color _accent = ReadAccent();

    private State _state;
    private DesktopScene? _scene;
    private double _openness;
    private double _pan;
    private Spring? _closeSpring;
    private double _lastTick;
    private Part? _focus;
    private (Rectangle From, byte Opacity)? _chosenJourney;
    private Hit _hover;
    private Plan? _hoverPlan;
    private bool _escapeRegistered;
    private (int Count, double Deadline)? _pendingRefresh;
    private int _realDesktop;
    private WindowHandoff? _handoff;
    private readonly Dictionary<(ThumbnailSurface Surface, IntPtr Window, string Slot), Sprite> _reusable = new();

    public MissionControl(Func<IReadOnlyList<ThumbnailSurface>> surfaces, Func<DesktopScene> capture, DesktopSwitcher? switcher, Swatches swatches)
    {
        _surfaces = surfaces;
        _capture = capture;
        _switcher = switcher;
        _swatches = swatches;
    }

    /// <summary>Raised when an animation starts that needs display frames (see <see cref="Tick"/>).</summary>
    public event Action? FramesNeeded;

    public bool IsVisible => _state != State.Hidden;

    public bool IsOpen => _state == State.Open;

    public bool IsAnimating => _state == State.Closing || _pendingRefresh is not null || _handoff is not null;

    /// <summary>Closing, or handing off to the real desktop; a gesture shouldn't reopen it.</summary>
    public bool IsClosing => _state == State.Closing;

    public IEnumerable<IntPtr> DecorationHandles => _decorations.Values.Select(decoration => decoration.Handle);

    /// <summary>Shows Mission Control at openness 0, ready for a gesture to drive it.</summary>
    public void BeginOpening(DesktopScene scene)
    {
        _realDesktop = scene.Current;
        Build(scene);
        RemoveStale();
        foreach (var plan in _plans)
        {
            Render(plan, 0);
            plan.Surface.Show();
            Paint(plan);
            plan.Decoration.SetAlpha(0);
            plan.Decoration.Show();
        }
        _openness = 0;
        _state = State.Tracking;
    }

    /// <summary>Hands control back to a gesture (swiping down to close).</summary>
    /// <remarks>
    /// If the pointer is over a window, the swipe closes onto it, as clicking it would: it comes to the
    /// front and your fingers carry it to its real position.
    /// </remarks>
    public void BeginGestureClose()
    {
        var target = WindowUnderPointer();
        SetInteractive(false);
        SetHover(null, default);
        if (target is not null) Choose(target);
        _state = State.Tracking;
    }

    public void Render(double openness)
    {
        if (_state == State.Hidden) return;
        _openness = Math.Clamp(openness, 0, 1);
        foreach (var plan in _plans) Render(plan, _openness);
    }

    /// <summary>Whether there's a desktop <paramref name="step"/> places away to pan to.</summary>
    public bool CanPan(int step) =>
        _state == State.Open && _scene is { } scene && scene.Current + step >= 0 && scene.Current + step < scene.DesktopCount;

    /// <summary>A sideways swipe started while open: stop taking clicks until it ends.</summary>
    public void BeginPan()
    {
        SetInteractive(false);
        SetHover(null, default);
    }

    /// <summary>Slides the window spreads sideways: +1 shows the next desktop's spread.</summary>
    public void Pan(double progress)
    {
        if (_state != State.Open) return;
        _pan = progress;
        foreach (var plan in _plans) Render(plan, _openness);
    }

    /// <summary>
    /// Makes the desktop the pan landed on the one being viewed. The real switch waits until Mission
    /// Control closes: switching now would hide and show windows under the overview, and their repaints flicker.
    /// </summary>
    public void CommitPan(int step)
    {
        if (!CanPan(step)) return;
        _pan = 0;
        Rebuild(_scene!.WithCurrent(_scene.Current + step)); // the target spread is now exactly where the pan left it
    }

    /// <summary>A swipe down finished closing it: hand off to the viewed desktop.</summary>
    public void FinishGestureClose()
    {
        if (_state == State.Hidden) return;
        _state = State.Closing;
        Closed();
    }

    public void EndPan()
    {
        _pan = 0;
        foreach (var plan in _plans) Render(plan, _openness);
        if (_state == State.Open) SetInteractive(true);
    }

    /// <summary>Settles fully open and starts taking clicks.</summary>
    public void SetOpen()
    {
        if (_state == State.Hidden) return;
        Unchoose(); // a swipe down that snapped back open closes onto nothing
        Render(1);
        _state = State.Open;
        SetInteractive(true);
        foreach (var plan in _plans) Paint(plan);
        if (_plans.FirstOrDefault(plan => plan.Surface.IsPrimary) is { } primary && !_escapeRegistered)
            _escapeRegistered = RegisterHotKey(primary.Decoration.Handle, EscapeHotKey, 0, VK_ESCAPE);
    }

    public void Tick(double now)
    {
        if (_state == State.Closing && _closeSpring is { } spring)
        {
            spring.Step(Math.Clamp(now - _lastTick, 0, 0.05));
            _lastTick = now;
            if (spring.Position <= 0.003)
            {
                _closeSpring = null;
                Closed();
                return;
            }
            Render(spring.Position);
        }

        // Closed: switching, restoring and activating underneath until the real desktop matches the overlay.
        if (_handoff is { } handoff)
        {
            handoff.Tick(now);
            if (handoff.IsDone)
            {
                Hide();
                handoff.Finish();
            }
            return;
        }

        // A new desktop shows up in the registry shortly after Explorer creates it.
        if (_pendingRefresh is { } pending && _state == State.Open)
        {
            if ((VirtualDesktops.Read()?.Count ?? 0) >= pending.Count || now > pending.Deadline)
            {
                _pendingRefresh = null;
                Rebuild(_capture().WithCurrent(_scene!.Current)); // the new desktop is added at the end, so indices hold
            }
        }
    }

    public void Hide()
    {
        _state = State.Hidden;
        _pan = 0;
        _closeSpring = null;
        _pendingRefresh = null;
        _handoff = null;
        _reusable.Clear();
        _focus = null;
        _chosenJourney = null;
        _hover = default;
        _hoverPlan = null;
        UnregisterEscape();
        foreach (var plan in _plans)
        {
            plan.Decoration.Hide();
            plan.Surface.Hide();
            plan.Surface.Clear();
        }
        _plans.Clear();
        _scene = null;
    }

    /// <summary>For diagnostics: where things were laid out, per monitor.</summary>
    public string Describe() => string.Join("; ", _plans.Select(plan =>
        $"monitor {plan.Surface.Monitor.Size.Width}x{plan.Surface.Monitor.Size.Height}: {plan.Parts.Count} thumbnails, " +
        $"{plan.Tiles.Count} desktop tiles{(plan.AddTile is null ? "" : " + add")}, spread windows " +
        string.Join(" ", plan.Windows.Select(part => $"[{part.From.Width}x{part.From.Height} -> {part.To.Width}x{part.To.Height} at {part.To.X},{part.To.Y}]"))));

    public void Dispose()
    {
        Hide();
        foreach (var decoration in _decorations.Values) decoration.Dispose();
        _decorations.Clear();
    }

    /// <summary>Animates closed onto the viewed desktop, as clicking empty space does.</summary>
    public void Dismiss() => Close();

    /// <summary>For diagnostics: the screen point at the centre of <paramref name="window"/> in the spread.</summary>
    public Point? CentreOf(IntPtr window)
    {
        foreach (var plan in _plans)
        {
            if (plan.Windows.FirstOrDefault(part => part.Sprite.Item.Window == window) is not { } part) continue;
            return new Point(plan.Surface.Monitor.X + part.To.X + part.To.Width / 2, plan.Surface.Monitor.Y + part.To.Y + part.To.Height / 2);
        }
        return null;
    }

    /// <summary>For diagnostics: acts as if <paramref name="window"/> were clicked. False if it isn't in the spread.</summary>
    public bool Choose(IntPtr window)
    {
        var part = _plans.SelectMany(plan => plan.Windows).FirstOrDefault(part => part.Sprite.Item.Window == window);
        if (part is null || _state != State.Open) return false;
        Close(part);
        return true;
    }

    /// <summary>Animates closed. <paramref name="focus"/>, if given, is brought to the front and activated once closed.</summary>
    private void Close(Part? focus = null)
    {
        if (_state is State.Hidden or State.Closing) return;
        SetInteractive(false);
        SetHover(null, default);
        UnregisterEscape();
        if (focus is not null) Choose(focus);
        _closeSpring = new Spring(SpringPreset.Snappy) { Position = _openness, Target = 0 };
        _lastTick = Clock.Now;
        _state = State.Closing;
        FramesNeeded?.Invoke();
    }

    /// <summary>
    /// Makes <paramref name="focus"/> the window Mission Control closes onto: it comes to the front and,
    /// once closed, is restored and activated. <see cref="Unchoose"/> reverses this if the close is abandoned.
    /// </summary>
    private void Choose(Part focus)
    {
        // Bring it to the front, but activating a window can't lift it above always-on-top windows, so
        // neither may its thumbnail: those (and the taskbar, which is one) stay above it, in their own order.
        var stack = new List<Sprite>();
        void Push(Part part)
        {
            if (part.Backing is not null) stack.Add(part.Backing);
            stack.Add(part.Sprite);
        }
        Push(focus);
        if (PlanFor(focus.Surface) is { } focusPlan)
        {
            foreach (var part in focusPlan.Parts.Where(part => part != focus && part.Slot == "spread" && part.Sprite.Item.IsTopmost)) Push(part);
            foreach (var part in focusPlan.Parts.Where(part => part.Slot == "taskbar")) Push(part);
        }
        focus.Surface.Restack(stack);

        // A minimized window doesn't shrink away: it flies, fully opaque, to where it will reappear.
        _chosenJourney = (focus.From, focus.FromOpacity);
        if (focus.Sprite.Item.IsMinimized)
        {
            focus.From = focus.Sprite.Home;
            focus.FromOpacity = 255;
        }
        _focus = focus;
    }

    /// <summary>Undoes <see cref="Choose"/>, e.g. when a swipe down is released before it closes.</summary>
    private void Unchoose()
    {
        if (_focus is { } focus && _chosenJourney is { } journey)
        {
            focus.From = journey.From;
            focus.FromOpacity = journey.Opacity;
        }
        _focus = null;
        _chosenJourney = null;
    }

    /// <summary>The app window in the spread under the mouse pointer, if any.</summary>
    private Part? WindowUnderPointer()
    {
        if (!GetCursorPos(out var cursor)) return null;
        foreach (var plan in _plans)
        {
            if (!plan.Surface.Monitor.Contains(cursor.X, cursor.Y)) continue;
            var hit = HitTest(plan, new Point(cursor.X - plan.Surface.Monitor.X, cursor.Y - plan.Surface.Monitor.Y));
            return hit.Kind == HitKind.Window ? hit.Window : null;
        }
        return null;
    }

    /// <summary>Shows that desktop's windows spread out, then closes onto them; the real switch happens as it lands.</summary>
    private void SwitchTo(int desktop)
    {
        if (_scene is null) return;
        if (desktop != _scene.Current) Rebuild(_scene.WithCurrent(desktop));
        Close();
    }

    /// <summary>
    /// Fully closed: the overview now shows the viewed desktop exactly as it will look, the chosen window
    /// (if any) resting where it will reappear. Hand off to the real desktop underneath before removing it.
    /// </summary>
    private void Closed()
    {
        if (_scene is null)
        {
            Hide();
            return;
        }
        Render(0);
        var target = _scene.Current;
        Guid? switchingTo = null;
        if (target != _realDesktop)
        {
            if (_switcher is null || _scene.Desktops is not { } desktops || !_switcher.TrySwitch(desktops.Ids[target]))
            {
                // No instant switching: step there with the shortcut and let Windows animate.
                var focusWindow = _focus?.Sprite.Item.Window ?? IntPtr.Zero;
                var steps = target - _realDesktop;
                Hide();
                for (var i = 0; i < Math.Abs(steps); i++)
                    KeySender.Send(steps > 0 ? SwipeAction.NextDesktop : SwipeAction.PreviousDesktop);
                if (focusWindow != IntPtr.Zero)
                {
                    if (IsIconic(focusWindow)) ShowWindowAsync(focusWindow, SW_RESTORE);
                    Foreground.Take(focusWindow);
                }
                return;
            }
            switchingTo = desktops.Ids[target];
        }

        if (switchingTo is null && _focus is null)
        {
            Hide();
            return;
        }
        var focusPlan = _focus is null ? null : PlanFor(_focus.Surface);
        _handoff = new WindowHandoff(_switcher, switchingTo, _scene.TopWindowOn(target),
            _focus?.Sprite, _focus?.Backing, _focus?.From ?? Rectangle.Empty, Clock.Now,
            focusPlan is null ? null : rect => Freeze(focusPlan, rect),
            focusPlan is null ? null : () => focusPlan.Decoration.SetAlpha(0));
        FramesNeeded?.Invoke();
    }

    /// <summary>Covers <paramref name="rect"/> of a surface with a still image of what it shows right now.</summary>
    private static void Freeze(Plan plan, Rectangle rect)
    {
        rect.Intersect(new Rectangle(Point.Empty, plan.Surface.Monitor.Size));
        if (rect.IsEmpty) return;
        using var still = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(still))
            g.CopyFromScreen(rect.X + plan.Surface.Monitor.X, rect.Y + plan.Surface.Monitor.Y, 0, 0, rect.Size);
        plan.Decoration.Paint(g => g.DrawImageUnscaled(still, rect.Location));
        plan.Decoration.SetAlpha(255);
        if (!plan.Decoration.IsShown) plan.Decoration.Show();
    }

    private void AddDesktop()
    {
        if (_scene is null || _switcher?.TryCreateDesktop() != true) return;
        _pendingRefresh = (_scene.DesktopCount + 1, Clock.Now + 1.0);
        FramesNeeded?.Invoke();
    }

    /// <summary>
    /// Rebuilds in place at the current openness, e.g. after panning or adding a desktop. The new
    /// thumbnails go in on top of the old ones and the old ones only come out once the new ones are
    /// placed, so no frame is ever drawn without them.
    /// </summary>
    private void Rebuild(DesktopScene scene)
    {
        Build(scene);
        foreach (var plan in _plans)
        {
            Render(plan, _openness);
            if (!plan.Surface.IsShown) plan.Surface.Show();
            if (!plan.Decoration.IsShown) plan.Decoration.Show();
            Paint(plan);
        }
        RemoveStale();
        SetInteractive(_state == State.Open);
    }

    /// <summary>Removes the thumbnails the last rebuild didn't reuse.</summary>
    private void RemoveStale()
    {
        foreach (var sprite in _reusable.Values) sprite.Surface.Remove(sprite);
        _reusable.Clear();
    }

    /// <summary>
    /// The thumbnail for a window in a given role, reusing the one already on screen when there is one.
    /// Replacing it instead would briefly stack old and new, and stacked translucent thumbnails (the
    /// dimmed wallpaper) flash brighter for a frame.
    /// </summary>
    private Sprite? Acquire(ThumbnailSurface surface, SceneItem item, string slot, SceneItem? mirror = null)
    {
        var source = mirror ?? item;
        if (_reusable.Remove((surface, item.Window, slot), out var sprite))
        {
            if (sprite.Item == source) return sprite;
            surface.Remove(sprite); // the window moved since; its placement would be stale
        }
        return surface.Add(source);
    }

    /// <summary>A Mica-coloured backing for an app window, registered first so it sits underneath.</summary>
    private Sprite? AcquireBacking(ThumbnailSurface surface, SceneItem item, string slot) =>
        item.IsAppWindow ? Acquire(surface, item, slot + "-back", _swatches.BackingFor(item)) : null;

    private void Build(DesktopScene scene)
    {
        _scene = scene;
        _hover = default;
        _hoverPlan = null;
        foreach (var part in _plans.SelectMany(plan => plan.Parts))
        {
            _reusable.TryAdd((part.Surface, part.Key, part.Slot), part.Sprite);
            if (part.Backing is not null) _reusable.TryAdd((part.Surface, part.Key, part.Slot + "-back"), part.Backing);
        }
        _plans.Clear();

        foreach (var surface in _surfaces())
        {
            var plan = new Plan(surface, DecorationFor(surface));
            surface.MouseMoved -= OnMouseMoved;
            surface.MouseMoved += OnMouseMoved;
            surface.Clicked -= OnClicked;
            surface.Clicked += OnClicked;
            BuildPlan(plan, scene);
            _plans.Add(plan);
        }
    }

    private void BuildPlan(Plan plan, DesktopScene scene)
    {
        var surface = plan.Surface;
        var screen = new Rectangle(Point.Empty, surface.Monitor.Size);
        var work = surface.WorkArea;
        work.Offset(-surface.Monitor.X, -surface.Monitor.Y);
        var s = surface.Scale;
        var margin = (int)(40 * s);

        // Desktop bar along the top of the primary monitor.
        var top = work.Top + margin;
        if (surface.IsPrimary)
        {
            var tileHeight = (int)(screen.Height * 0.105);
            var tileWidth = tileHeight * screen.Width / screen.Height;
            var gap = (int)(22 * s);
            var addWidth = _switcher is null ? 0 : tileHeight / 2;
            var count = scene.DesktopCount;
            var total = count * tileWidth + (count - 1) * gap + (addWidth > 0 ? gap + addWidth : 0);
            var x = (screen.Width - total) / 2;
            var y = work.Top + (int)(20 * s);
            for (var i = 0; i < count; i++)
            {
                var label = scene.Desktops?.Label(i) ?? "Desktop";
                plan.Tiles.Add(new Tile(i, new Rectangle(x + i * (tileWidth + gap), y, tileWidth, tileHeight), label));
            }
            if (addWidth > 0) plan.AddTile = new Rectangle(x + count * (tileWidth + gap), y, addWidth, tileHeight);
            top = y + tileHeight + (int)(30 * s) + margin;
        }
        var area = Rectangle.FromLTRB(work.Left + margin, top, work.Right - margin, work.Bottom - margin);

        // Wallpaper stays put and dims.
        foreach (var wallpaper in scene.Wallpapers)
        {
            if (!wallpaper.Bounds.IntersectsWith(surface.Monitor) || Acquire(surface, wallpaper, "wallpaper") is not { } sprite) continue;
            plan.Parts.Add(new Part(surface, sprite, sprite.Home, sprite.Home, 255, DimmedWallpaper) { Slot = "wallpaper" });
        }

        // The current desktop's windows spread out. Its neighbours' spreads wait one screen to either
        // side, so a sideways swipe can pan to them while the bar stays put.
        for (var layer = -1; layer <= 1; layer++)
        {
            var desktop = scene.Current + layer;
            if (desktop >= 0 && desktop < scene.DesktopCount) BuildSpread(plan, scene, desktop, layer * screen.Width, area, layer == 0);
        }

        // Desktop miniatures: each desktop's wallpaper and on-screen windows, scaled into its tile. They slide down from above.
        foreach (var tile in plan.Tiles)
        {
            var scale = tile.Bounds.Width / (double)screen.Width;
            var lift = tile.Bounds.Bottom + (int)(20 * s);
            var items = scene.Wallpapers.Concat(scene.VisibleWindowsOn(tile.Desktop).Where(item => item.IsAppWindow));
            foreach (var item in items)
            {
                var slot = $"tile{tile.Desktop}";
                if (!item.Bounds.IntersectsWith(surface.Monitor)) continue;
                var backing = AcquireBacking(surface, item, slot);
                if (Acquire(surface, item, slot) is not { } sprite) continue;
                var home = sprite.Home;
                var to = new Rectangle(
                    tile.Bounds.X + (int)Math.Round(home.X * scale),
                    tile.Bounds.Y + (int)Math.Round(home.Y * scale),
                    Math.Max(1, (int)Math.Round(home.Width * scale)),
                    Math.Max(1, (int)Math.Round(home.Height * scale)));
                var from = to;
                from.Offset(0, -lift);
                plan.Parts.Add(new Part(surface, sprite, from, to, 0, 255) { Slot = slot, Backing = backing });
            }
        }

        // Highlight ring around the viewed desktop's tile. While panning it glides towards the tile being swiped to.
        if (plan.Tiles.Count > 0 && plan.Tiles.FirstOrDefault(tile => tile.Desktop == scene.Current) is { } viewed)
        {
            var (ring, overhang) = _swatches.Ring(viewed.Bounds.Size, s, _accent);
            if (Acquire(surface, ring, "ring") is { } sprite)
            {
                Rectangle RingAround(Tile tile) => Rectangle.Inflate(tile.Bounds, overhang, overhang);
                var to = RingAround(viewed);
                var from = to;
                from.Offset(0, -(viewed.Bounds.Bottom + (int)(20 * s)));
                plan.Parts.Add(new Part(surface, sprite, from, to, 0, 255)
                {
                    Slot = "ring",
                    PanPrevious = plan.Tiles.FirstOrDefault(tile => tile.Desktop == scene.Current - 1) is { } previous ? RingAround(previous) : null,
                    PanNext = plan.Tiles.FirstOrDefault(tile => tile.Desktop == scene.Current + 1) is { } next ? RingAround(next) : null,
                });
            }
        }

        foreach (var taskbar in scene.Taskbars)
        {
            if (!taskbar.Bounds.IntersectsWith(surface.Monitor) || Acquire(surface, taskbar, "taskbar") is not { } sprite) continue;
            plan.Parts.Add(new Part(surface, sprite, sprite.Home, sprite.Home, 255, 255) { Slot = "taskbar" });
        }
    }

    /// <summary>
    /// Lays out one desktop's app windows, shifted <paramref name="shift"/> pixels sideways. Visible
    /// windows fly from where they are; minimized ones fade in where they land. Non-app windows fade out.
    /// </summary>
    private void BuildSpread(Plan plan, DesktopScene scene, int desktop, int shift, Rectangle area, bool isCurrent)
    {
        var surface = plan.Surface;
        var screen = new Rectangle(Point.Empty, surface.Monitor.Size);
        var spread = new List<Part>();
        foreach (var item in scene.WindowsOn(desktop))
        {
            // Minimized windows have no monitor of their own; show them on the primary one.
            var onThisMonitor = item.IsMinimized ? surface.IsPrimary : item.Bounds.IntersectsWith(surface.Monitor);
            if (!onThisMonitor) continue;
            var backing = AcquireBacking(surface, item, "spread");
            if (Acquire(surface, item, "spread") is not { } sprite) continue;
            var centre = new Point(sprite.Home.X + sprite.Home.Width / 2, sprite.Home.Y + sprite.Home.Height / 2);
            var spreads = item.IsAppWindow && (item.IsMinimized || screen.Contains(centre));
            var from = sprite.Home;
            from.Offset(shift, 0);
            var part = new Part(surface, sprite, from, from, item.IsMinimized ? (byte)0 : (byte)255, spreads ? (byte)255 : (byte)0) { Pans = true, Slot = "spread", Backing = backing };
            plan.Parts.Add(part);
            if (spreads) spread.Add(part);
        }

        var layout = ExposeLayout.Arrange(
            spread.Select(part => new LayoutRect(part.Sprite.Home.X, part.Sprite.Home.Y, part.Sprite.Home.Width, part.Sprite.Home.Height)).ToArray(),
            new LayoutRect(area.X, area.Y, area.Width, area.Height),
            gap: 36 * surface.Scale);
        for (var i = 0; i < spread.Count; i++)
        {
            var to = Round(layout[i]);
            to.Offset(shift, 0);
            spread[i].To = to;
            if (spread[i].Sprite.Item.IsMinimized) spread[i].From = Rectangle.Inflate(to, -to.Width / 12, -to.Height / 12);
            if (isCurrent) plan.Windows.Add(spread[i]);
        }
    }

    private void Render(Plan plan, double openness)
    {
        var panBy = (int)Math.Round(_pan * plan.Surface.Monitor.Width);
        foreach (var part in plan.Parts)
        {
            var rect = Round(LayoutRect.Lerp(ToLayout(part.From), ToLayout(part.To), openness));
            if (part.Pans) rect.Offset(-panBy, 0);
            if ((_pan > 0 ? part.PanNext : _pan < 0 ? part.PanPrevious : null) is { } panTarget)
                rect = Round(LayoutRect.Lerp(ToLayout(rect), ToLayout(panTarget), Math.Min(1, Math.Abs(_pan))));
            var opacity = (byte)Math.Round(part.FromOpacity + (part.ToOpacity - part.FromOpacity) * openness);
            part.Backing?.Place(rect, opacity);
            part.Sprite.Place(rect, opacity);
        }
        // Labels fade in over the second half of the gesture.
        plan.Decoration.SetAlpha((byte)Math.Round(255 * Math.Clamp(openness * 2 - 1, 0, 1)));
    }

    private void OnMouseMoved(ThumbnailSurface surface, Point point)
    {
        if (_state != State.Open || PlanFor(surface) is not { } plan) return;
        SetHover(plan, HitTest(plan, point));
    }

    private void OnClicked(ThumbnailSurface surface, Point point)
    {
        if (_state != State.Open || PlanFor(surface) is not { } plan) return;
        var hit = HitTest(plan, point);
        switch (hit.Kind)
        {
            case HitKind.Window: Close(hit.Window); break;
            case HitKind.Desktop: SwitchTo(hit.Desktop); break;
            case HitKind.AddDesktop: AddDesktop(); break;
            default: Close(); break;
        }
    }

    private void OnHotKey(int id)
    {
        if (id == EscapeHotKey && _state == State.Open) Close();
    }

    private static Hit HitTest(Plan plan, Point point)
    {
        foreach (var tile in plan.Tiles)
            if (tile.Bounds.Contains(point)) return new Hit(HitKind.Desktop, Desktop: tile.Desktop);
        if (plan.AddTile is { } add && add.Contains(point)) return new Hit(HitKind.AddDesktop);
        for (var i = plan.Windows.Count - 1; i >= 0; i--)
            if (plan.Windows[i].To.Contains(point)) return new Hit(HitKind.Window, plan.Windows[i]);
        return default;
    }

    private void SetHover(Plan? plan, Hit hit)
    {
        if (plan == _hoverPlan && hit == _hover) return;
        var previous = _hoverPlan;
        _hoverPlan = plan;
        _hover = hit;
        if (previous is not null && previous != plan) Paint(previous);
        if (plan is not null) Paint(plan);
    }

    private void Paint(Plan plan)
    {
        var s = plan.Surface.Scale;
        var hover = plan == _hoverPlan ? _hover : default;
        plan.Decoration.Paint(g =>
        {
            using var labelFont = new Font("Segoe UI", 13 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var titleFont = new Font("Segoe UI Semibold", 13 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var white = new SolidBrush(Color.White);
            using var accentPen = new Pen(_accent, 3 * s);
            var radius = 8 * s;

            foreach (var tile in plan.Tiles)
            {
                var frame = RectangleF.Inflate(tile.Bounds, 3 * s, 3 * s);
                if (tile.Desktop != _scene?.Current && hover.Kind == HitKind.Desktop && hover.Desktop == tile.Desktop)
                {
                    using var hoverPen = new Pen(Color.FromArgb(170, 255, 255, 255), 2 * s);
                    DrawRounded(g, hoverPen, frame, radius);
                }
                var labelBox = new RectangleF(tile.Bounds.X - 10 * s, tile.Bounds.Bottom + 8 * s, tile.Bounds.Width + 20 * s, 20 * s);
                g.DrawString(tile.Label, labelFont, white, labelBox, centred);
            }

            if (plan.AddTile is { } add)
            {
                var hovered = hover.Kind == HitKind.AddDesktop;
                using var fill = new SolidBrush(Color.FromArgb(hovered ? 110 : 60, 255, 255, 255));
                using var path = RoundedPath(add, radius);
                g.FillPath(fill, path);
                using var plus = new Pen(Color.White, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float cx = add.X + add.Width / 2f, cy = add.Y + add.Height / 2f, arm = Math.Min(add.Width, add.Height) * 0.18f;
                g.DrawLine(plus, cx - arm, cy, cx + arm, cy);
                g.DrawLine(plus, cx, cy - arm, cx, cy + arm);
            }

            if (hover.Kind == HitKind.Window && hover.Window is { } window)
            {
                DrawRounded(g, accentPen, RectangleF.Inflate(window.To, 2 * s, 2 * s), radius);
                var title = DesktopScene.TitleOf(window.Sprite.Item.Window);
                if (title.Length > 0)
                {
                    var size = g.MeasureString(title, titleFont);
                    var width = Math.Min(size.Width + 24 * s, window.To.Width + 80 * s);
                    var pill = new RectangleF(window.To.X + (window.To.Width - width) / 2, window.To.Bottom + 10 * s, width, size.Height + 10 * s);
                    using var pillPath = RoundedPath(pill, pill.Height / 2);
                    using var pillFill = new SolidBrush(Color.FromArgb(215, 32, 32, 36));
                    g.FillPath(pillFill, pillPath);
                    var text = RectangleF.Inflate(pill, -12 * s, -5 * s);
                    g.DrawString(title, titleFont, white, text, centred);
                }
            }
        });
    }

    private void SetInteractive(bool interactive)
    {
        foreach (var plan in _plans) plan.Surface.Interactive = interactive;
    }

    private void UnregisterEscape()
    {
        if (!_escapeRegistered) return;
        foreach (var decoration in _decorations.Values) UnregisterHotKey(decoration.Handle, EscapeHotKey);
        _escapeRegistered = false;
    }

    private DecorationWindow DecorationFor(ThumbnailSurface surface)
    {
        // Surfaces are recreated when monitors change; drop decorations for ones that are gone.
        foreach (var stale in _decorations.Keys.Where(key => !_surfaces().Contains(key)).ToList())
        {
            _decorations[stale].Dispose();
            _decorations.Remove(stale);
        }
        if (!_decorations.TryGetValue(surface, out var decoration))
        {
            decoration = new DecorationWindow(surface.Monitor);
            decoration.HotKeyPressed += OnHotKey;
            _decorations[surface] = decoration;
        }
        return decoration;
    }

    private Plan? PlanFor(ThumbnailSurface surface) => _plans.FirstOrDefault(plan => plan.Surface == surface);

    private static LayoutRect ToLayout(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    private static Rectangle Round(LayoutRect r)
    {
        var left = (int)Math.Round(r.X);
        var top = (int)Math.Round(r.Y);
        return Rectangle.FromLTRB(left, top, (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
    }

    private static void DrawRounded(Graphics g, Pen pen, RectangleF rect, float radius)
    {
        using var path = RoundedPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
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

    /// <summary>The Windows accent colour (stored as ABGR), for highlights.</summary>
    private static Color ReadAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        return key?.GetValue("AccentColor") is int abgr
            ? Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF)
            : Color.FromArgb(0, 120, 212);
    }

    private sealed class Plan(ThumbnailSurface surface, DecorationWindow decoration)
    {
        public ThumbnailSurface Surface { get; } = surface;
        public DecorationWindow Decoration { get; } = decoration;
        public List<Part> Parts { get; } = new();
        /// <summary>Spread-out app windows, bottom to top, for hit testing.</summary>
        public List<Part> Windows { get; } = new();
        public List<Tile> Tiles { get; } = new();
        public Rectangle? AddTile { get; set; }
    }

    /// <summary>A sprite's journey from openness 0 to 1.</summary>
    private sealed class Part(ThumbnailSurface surface, Sprite sprite, Rectangle from, Rectangle to, byte fromOpacity, byte toOpacity)
    {
        public ThumbnailSurface Surface { get; } = surface;
        public Sprite Sprite { get; } = sprite;
        public Rectangle From { get; set; } = from;
        public Rectangle To { get; set; } = to;
        public byte FromOpacity { get; set; } = fromOpacity;
        public byte ToOpacity { get; } = toOpacity;
        /// <summary>Part of a desktop's window spread, which moves when panning between desktops.</summary>
        public bool Pans { get; init; }
        /// <summary>The window's role (wallpaper, spread, a desktop tile...), so a rebuild can reuse its thumbnail.</summary>
        public string Slot { get; init; } = "";
        /// <summary>Solid Mica-coloured thumbnail kept underneath, for apps that leave their background to the system backdrop.</summary>
        public Sprite? Backing { get; init; }
        /// <summary>Where this part heads while panning towards the previous/next desktop (the highlight ring).</summary>
        public Rectangle? PanPrevious { get; init; }
        public Rectangle? PanNext { get; init; }
        public IntPtr Key => Sprite.Item.Window;
    }

    private sealed record Tile(int Desktop, Rectangle Bounds, string Label);
}
