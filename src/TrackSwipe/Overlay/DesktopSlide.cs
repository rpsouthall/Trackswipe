using System.Numerics;
using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>
/// The sideways desktop switch: the current desktop and its neighbours side by side, each
/// layer at (layer - progress) screen widths. The taskbar stays put, as it does natively.
/// </summary>
/// <remarks>
/// Every desktop layer of a monitor sits in one group visual, placed once, each clipped to its own
/// desktop. Only the group moves: one property per frame while tracking, one curve for the release,
/// however many windows are open.
/// </remarks>
internal sealed class DesktopSlide(Swatches swatches)
{
    private const int Fixed = int.MaxValue;

    /// <summary>A minimized window being restored as the slide lands: it grows out of its dock icon, in screen space.</summary>
    private const int Arriving = int.MaxValue - 1;

    private readonly List<(Sprite Sprite, Sprite? Backing, int Layer, int Width)> _parts = new();
    private readonly Dictionary<ThumbnailSurface, CompositionVisual> _groups = new();
    private readonly Dictionary<ThumbnailSurface, float> _offsets = new();
    private DesktopScene? _scene;
    private (ThumbnailSurface Surface, int Generation)[] _surfaces = Array.Empty<(ThumbnailSurface, int)>();
    private int? _target;
    private IntPtr[] _raised = Array.Empty<IntPtr>();
    private IntPtr _arriving;
    private Rectangle? _arrivingFrom;

    /// <param name="target">
    /// A desktop to slide to that may not be a neighbour (a jump from the taskbar): it takes the neighbour's place on
    /// its side, and only it and the current desktop are prepared.
    /// </param>
    /// <param name="raise">Windows that come to the front as the switch lands (the one being activated).</param>
    /// <param name="arriving">
    /// A minimized window on the target desktop that is being restored: hidden until <see cref="Settle"/>, which grows
    /// it from <paramref name="arrivingFrom"/> (its dock icon, in screen px) to its place as the desktops slide, as
    /// Windows' own restore animation would, so it is already there when the slide comes off rather than popping in.
    /// </param>
    public void Begin(DesktopScene scene, IReadOnlyList<ThumbnailSurface> surfaces, int? target = null, IReadOnlyCollection<IntPtr>? raise = null,
        IntPtr arriving = default, Rectangle? arrivingFrom = null)
    {
        var raised = raise?.ToArray() ?? Array.Empty<IntPtr>();
        // A repeated swipe on an unchanged desktop can reuse all registrations and their z-order.
        // On a changed scene, reuse unchanged windows individually rather than discarding
        // their DWM representations merely because the current desktop index changed.
        if (_scene is { } previous && previous.Current == scene.Current && previous.Items.SequenceEqual(scene.Items)
            && _target == target && _raised.SequenceEqual(raised) && _arriving == arriving && _arrivingFrom == arrivingFrom
            && (previous.Desktops?.Ids ?? Array.Empty<Guid>()).SequenceEqual(scene.Desktops?.Ids ?? Array.Empty<Guid>())
            && _surfaces.Length == surfaces.Count
            && _surfaces.Select(pair => pair.Surface).SequenceEqual(surfaces)
            && _surfaces.All(pair => pair.Generation == pair.Surface.Generation)) return;

        var reusable = _parts.Where(part => surfaces.Contains(part.Sprite.Surface) &&
            _surfaces.Any(previous => previous.Surface == part.Sprite.Surface && previous.Generation == part.Sprite.Surface.Generation))
            .ToDictionary(part => Key(part.Sprite.Surface, part.Sprite.Item, part.Layer));
        _parts.Clear();
        var reused = 0;
        var created = 0;
        foreach (var surface in surfaces)
        {
            // Wallpaper only where there's a desktop: past the first or last one, the rubber band shows black.
            for (var layer = -1; layer <= 1; layer++)
            {
                if (!HasDesktop(layer)) continue;
                foreach (var wallpaper in scene.Wallpapers) Add(surface, wallpaper, layer);
            }
            // Windows on every desktop don't slide: Windows leaves them where they are when the desktop changes.
            var windows = scene.Items.Where(item => item.Kind == SceneKind.Window && !item.IsMinimized && !item.OnAllDesktops && LayerOf(item) is not null).ToList();
            foreach (var window in windows.Where(item => !raised.Contains(item.Window)).Concat(windows.Where(item => raised.Contains(item.Window))))
                Add(surface, window, LayerOf(window)!.Value);
            if (arriving != IntPtr.Zero && scene.Items.FirstOrDefault(item => item.Window == arriving && item.IsMinimized) is { } restoring &&
                restoring.Window != IntPtr.Zero)
                Add(surface, restoring, Arriving);
            foreach (var taskbar in scene.Taskbars) Add(surface, taskbar, Fixed);
            // Ones that stay on top are the real windows, kept above the overlay (DesktopScene.RaiseKeptAbove); the
            // rest can't be above it without being made topmost, so they're mirrored in place, like the taskbar.
            foreach (var shared in scene.Items.Where(item => item.OnAllDesktops && !item.IsTopmost)) Add(surface, shared, Fixed);
        }
        foreach (var stale in reusable.Values)
        {
            stale.Sprite.Surface.Remove(stale.Sprite);
            if (stale.Backing is { } backing) backing.Surface.Remove(backing);
        }
        foreach (var gone in _groups.Keys.Where(surface => !surfaces.Contains(surface)).ToList())
        {
            gone.RemoveGroup(_groups[gone]);
            _groups.Remove(gone);
            _offsets.Remove(gone);
        }
        foreach (var surface in surfaces) Arrange(surface);
        _scene = scene;
        _target = target;
        _raised = raised;
        _arriving = arriving;
        _arrivingFrom = arrivingFrom;
        _surfaces = surfaces.Select(surface => (surface, surface.Generation)).ToArray();
        Log.Verbose($"Desktop slide prepared: {reused} reused thumbnails, {created} new thumbnails.");

        bool HasDesktop(int layer)
        {
            if (layer == 0 || scene.Desktops is not { } desktops) return true;
            if (target is { } to) return layer == Math.Sign(to - scene.Current);
            var index = scene.Current + layer;
            return index >= 0 && index < desktops.Count;
        }

        int? LayerOf(SceneItem item)
        {
            if (target is not { } to) return Math.Abs(item.Desktop - scene.Current) <= 1 ? item.Desktop - scene.Current : null;
            if (item.Desktop == scene.Current) return 0;
            return item.Desktop == to ? Math.Sign(to - scene.Current) : null;
        }

        void Add(ThumbnailSurface surface, SceneItem item, int layer)
        {
            if (!item.Bounds.IntersectsWith(surface.Monitor)) return;
            Sprite? sprite = null;
            Sprite? backing = null;
            if (reusable.Remove(Key(surface, item, layer), out var previous))
            {
                if (previous.Sprite.TryReuse(item))
                {
                    sprite = previous.Sprite;
                    backing = previous.Backing;
                    reused++;
                }
                else
                {
                    surface.Remove(previous.Sprite);
                    if (previous.Backing is { } oldBacking) surface.Remove(oldBacking);
                }
            }
            if (sprite is null)
            {
                backing = item.NeedsBacking ? surface.Add(swatches.BackingFor(item)) : null;
                sprite = surface.Add(item);
                if (sprite is not null) created++;
                else if (backing is not null) surface.Remove(backing);
            }
            if (sprite is not null) _parts.Add((sprite, backing, layer, surface.Monitor.Width));
        }
    }

    /// <summary>
    /// Stacks one monitor's parts in scene order: the desktop layers inside the group, the taskbar above it.
    /// Places them once, in group coordinates, each clipped to its own desktop.
    /// </summary>
    private void Arrange(ThumbnailSurface surface)
    {
        var parts = _parts.Where(part => part.Sprite.Surface == surface).ToList();
        surface.Restack(parts.SelectMany(part => part.Backing is { } backing ? new[] { backing, part.Sprite } : new[] { part.Sprite }).ToArray());
        if (!_groups.TryGetValue(surface, out var group)) _groups[surface] = group = surface.CreateGroup();
        surface.RaiseGroup(group);
        var width = surface.Monitor.Width;
        var height = surface.Monitor.Height;
        var span = new RectangleF(-width, 0, 3f * width, height); // the three desktops side by side
        foreach (var (sprite, backing, layer, _) in parts)
        {
            if (layer is Fixed or Arriving) continue;
            var destination = (RectangleF)sprite.Home;
            destination.Offset(layer * width, 0);
            var viewport = new RectangleF(layer * width, 0, width, height);
            if (backing is not null) { surface.Group(backing, group); backing.Place(destination, 255, viewport, span); }
            surface.Group(sprite, group);
            sprite.Place(destination, 255, viewport, span);
        }
        // The taskbar above the moving desktops; a window being restored between them, hidden in its dock icon for now.
        var fixedParts = parts.Where(part => part.Layer is Fixed or Arriving).ToArray();
        surface.Restack(fixedParts.Select(part => part.Sprite).ToArray());
        foreach (var (sprite, _, layer, _) in fixedParts)
            if (layer == Arriving) sprite.Place(ArrivalStart(surface), 0, null);
            else sprite.Place(sprite.Home);
        _offsets[surface] = float.NaN;
    }

    /// <summary>Where a restored window grows from, in the monitor's coordinates: its dock icon, or the middle of the bottom edge.</summary>
    private RectangleF ArrivalStart(ThumbnailSurface surface)
    {
        var monitor = surface.Monitor;
        if (_arrivingFrom is { } icon) return new RectangleF(icon.X - monitor.X, icon.Y - monitor.Y, icon.Width, icon.Height);
        return new RectangleF(monitor.Width / 2f - 24, monitor.Height - 48, 48, 48);
    }

    private static (ThumbnailSurface Surface, IntPtr Window, int Slot) Key(ThumbnailSurface surface, SceneItem item, int layer) =>
        (surface, item.Window, item.Kind == SceneKind.Wallpaper ? layer : Fixed);

    public void Render(double progress)
    {
        foreach (var (surface, group) in _groups)
        {
            var offset = (float)(-progress * surface.Monitor.Width);
            if (_offsets.TryGetValue(surface, out var current) && current == offset) continue;
            group.SetTranslation(offset, 0);
            _offsets[surface] = offset;
        }
    }

    public void Settle(SettleMotion motion, long begin)
    {
        foreach (var (surface, group) in _groups)
        {
            var width = surface.Monitor.Width;
            var frames = motion.Samples.Select(sample =>
                new SpriteFrame(sample.Time, new RectangleF((float)(-sample.Value * width), 0, 0, 0), 1, null)).ToArray();
            using var transform = Composition.Device.CreateMatrixTransform();
            var identity = Matrix3x2.Identity;
            transform.SetMatrix(ref identity).CheckError();
            using var x = CompositorMotion.Curve(frames, frame => frame.Bounds.X, begin);
            transform.SetMatrixElement(2, 0, x).CheckError();
            group.SetTransform(transform);
            _offsets[surface] = float.NaN; // the next Render replaces the curve
        }
        // A window being restored grows from its dock icon to its place on the same curve, landing as the desktops do.
        foreach (var (sprite, _, _, _) in _parts.Where(part => part.Layer == Arriving))
        {
            sprite.RefreshSourceSize(); // restored by now: its live image, not the small one kept while it was minimized
            var from = ArrivalStart(sprite.Surface);
            var to = (RectangleF)sprite.Home;
            var frames = motion.Samples.Select(sample =>
            {
                var t = (float)Math.Clamp(Math.Abs(sample.Value), 0, 1);
                var bounds = new RectangleF(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
                    from.Width + (to.Width - from.Width) * t, from.Height + (to.Height - from.Height) * t);
                // Faded in over its first third, as Windows does; never quite 0, which would skip sizing its thumbnail.
                return new SpriteFrame(sample.Time, bounds, Math.Clamp(t * 3, 0.01f, 1f), null);
            }).ToArray();
            sprite.Animate(frames, begin);
        }
        Composition.Flush();
    }
}
