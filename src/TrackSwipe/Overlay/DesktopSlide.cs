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

    private readonly List<(Sprite Sprite, Sprite? Backing, int Layer, int Width)> _parts = new();
    private readonly Dictionary<ThumbnailSurface, CompositionVisual> _groups = new();
    private readonly Dictionary<ThumbnailSurface, float> _offsets = new();
    private DesktopScene? _scene;
    private (ThumbnailSurface Surface, int Generation)[] _surfaces = Array.Empty<(ThumbnailSurface, int)>();

    public void Begin(DesktopScene scene, IReadOnlyList<ThumbnailSurface> surfaces)
    {
        // A repeated swipe on an unchanged desktop can reuse all registrations and their z-order.
        // On a changed scene, reuse unchanged windows individually rather than discarding
        // their DWM representations merely because the current desktop index changed.
        if (_scene is { } previous && previous.Current == scene.Current && previous.Items.SequenceEqual(scene.Items)
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
            for (var layer = -1; layer <= 1; layer++)
            {
                foreach (var wallpaper in scene.Wallpapers) Add(surface, wallpaper, layer);
            }
            foreach (var window in scene.Items.Where(item => item.Kind == SceneKind.Window && !item.IsMinimized && Math.Abs(item.Desktop - scene.Current) <= 1))
                Add(surface, window, window.Desktop - scene.Current);
            foreach (var taskbar in scene.Taskbars) Add(surface, taskbar, Fixed);
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
        _surfaces = surfaces.Select(surface => (surface, surface.Generation)).ToArray();
        Log.Verbose($"Desktop slide prepared: {reused} reused thumbnails, {created} new thumbnails.");

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
            if (layer == Fixed) continue;
            var destination = (RectangleF)sprite.Home;
            destination.Offset(layer * width, 0);
            var viewport = new RectangleF(layer * width, 0, width, height);
            if (backing is not null) { surface.Group(backing, group); backing.Place(destination, 255, viewport, span); }
            surface.Group(sprite, group);
            sprite.Place(destination, 255, viewport, span);
        }
        var fixedParts = parts.Where(part => part.Layer == Fixed).Select(part => part.Sprite).ToArray();
        surface.Restack(fixedParts); // the taskbar above the moving desktops
        foreach (var sprite in fixedParts) sprite.Place(sprite.Home);
        _offsets[surface] = float.NaN;
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
        Composition.Flush();
    }
}
