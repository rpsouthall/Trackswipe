#if DIAGNOSTICS
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    /// <summary>
    /// For the slide diagnostics: which windows on every desktop the slide is keeping above its overlay, and whether each
    /// really is above every slide surface in the z-order right now.
    /// </summary>
    public string DescribeKeptAbove()
    {
        if (_scene is not { } scene) return $"no slide scene (mode {_mode})";
        var kept = scene.KeptAbove.ToList();
        if (kept.Count == 0) return "none";
        var surfaces = _slideSurfaces.Handles.ToHashSet();
        return string.Join("; ", kept.Select(item =>
        {
            // Walk up from the window: any slide surface met on the way is drawn over it.
            var covered = false;
            for (var above = GetWindow(item.Window, 3 /* GW_HWNDPREV */); above != IntPtr.Zero; above = GetWindow(above, 3))
                if (surfaces.Contains(above)) { covered = true; break; }
            return $"\"{DesktopScene.TitleOf(item.Window)}\" {(covered ? "UNDER the overlay" : "above the overlay")}";
        }));
    }
}
#endif
