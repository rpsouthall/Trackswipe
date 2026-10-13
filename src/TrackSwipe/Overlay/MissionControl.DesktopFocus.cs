using System.Diagnostics;
using TrackSwipe.Core;

namespace TrackSwipe.Overlay;

internal sealed partial class MissionControl
{
    /// <summary>How long the pointer rests on a desktop in the bar before the overview shows that desktop.</summary>
    private const int DwellMs = 1000;
    private const double FocusFadeOut = 0.14, FocusFadeIn = 0.24, FocusGrowFrom = 0.96;

    private UiTimer? _dwell;
    private int _dwellDesktop = -1;
    /// <summary>Showing another desktop's spread: when the old one has faded out (swap), and when the new one is in.</summary>
    private (int Desktop, double SwapAt, double Until, bool Swapped)? _focusing;

    /// <summary>From pointer moves: (re)starts the dwell timer while the pointer rests on another desktop's tile.</summary>
    private void UpdateDwell(Hit hit)
    {
        var desktop = hit.Kind == HitKind.Desktop && _scene is { } scene && hit.Desktop != scene.Current ? hit.Desktop : -1;
        if (desktop == _dwellDesktop) return;
        _dwellDesktop = desktop;
        _dwell?.Stop();
        if (desktop < 0) return;
        if (_dwell is null)
        {
            _dwell = new UiTimer { Interval = DwellMs };
            _dwell.Tick += (_, _) => DwellElapsed();
        }
        _dwell.Start();
    }

    private void StopDwell()
    {
        _dwell?.Stop();
        _dwellDesktop = -1;
    }

    private void DwellElapsed()
    {
        _dwell!.Stop();
        var desktop = _dwellDesktop;
        _dwellDesktop = -1;
        if (desktop < 0 || !IsOpen || _barRevealAnimating || _pan != 0 || _scene is not { } scene ||
            desktop >= scene.DesktopCount || desktop == scene.Current) return;
        if (_hover.Kind != HitKind.Desktop || _hover.Desktop != desktop) return; // the pointer moved off meanwhile
        FocusDesktop(desktop);
    }

    /// <summary>
    /// Shows <paramref name="desktop"/>'s windows in the overview without leaving Mission Control, as a pan would but
    /// for any desktop: the spread on show fades out, then that desktop's fades in, growing slightly into place.
    /// Picking a window or clicking empty space then goes to that desktop, as after a pan.
    /// </summary>
    private void FocusDesktop(int desktop)
    {
        if (DeferUntilAwake(() => FocusDesktop(desktop))) return; // sharp thumbnails: wake the scaled copies a frame first
        var now = Clock.Now;
        var begin = (long)(now * Stopwatch.Frequency);
        SetHover(null, default);
        foreach (var plan in _plans)
            foreach (var part in plan.Windows)
            {
                var at = FrameFor(plan, part, _openness, _pan, 0);
                var frames = Ramp(FocusFadeOut, t => at with { Opacity = at.Opacity * (float)(1 - Ease(t)) });
                part.Sprite.Animate(frames, begin);
                part.Backing?.Animate(frames, begin);
            }
        _focusing = (desktop, now + FocusFadeOut, now + FocusFadeOut + FocusFadeIn, false);
        Log.Verbose($"Mission Control: showing desktop {desktop + 1} after the pointer rested on it.");
        Interop.Composition.Flush();
        FramesNeeded?.Invoke();
    }

    /// <summary>From <see cref="Tick"/>: swaps the spreads once the old one is out, and finishes once the new one is in.</summary>
    private void TickFocus(double now)
    {
        if (_focusing is not { } focusing) return;
        if (!focusing.Swapped && now >= focusing.SwapAt)
        {
            _focusing = focusing with { Swapped = true };
            if (_scene is { } scene && focusing.Desktop < scene.DesktopCount) Rebuild(scene.WithCurrent(focusing.Desktop));
            var begin = (long)(now * Stopwatch.Frequency);
            foreach (var plan in _plans)
                foreach (var part in plan.Windows)
                {
                    var to = FrameFor(plan, part, _openness, 0, 0);
                    var frames = Ramp(FocusFadeIn, t =>
                    {
                        var e = (float)Ease(t);
                        var scale = (float)(FocusGrowFrom + (1 - FocusGrowFrom) * e);
                        var b = to.Bounds;
                        var bounds = new RectangleF(b.X + b.Width * (1 - scale) / 2, b.Y + b.Height * (1 - scale) / 2, b.Width * scale, b.Height * scale);
                        return to with { Bounds = bounds, Opacity = to.Opacity * e };
                    });
                    part.Sprite.Animate(frames, begin);
                    part.Backing?.Animate(frames, begin);
                }
            Interop.Composition.Flush();
        }
        else if (focusing.Swapped && now >= focusing.Until)
        {
            _focusing = null;
            ScheduleSharpen();
        }
    }

    /// <summary>Frames at the compositor's sampling rate over <paramref name="duration"/>, from <paramref name="at"/>(0..1).</summary>
    private static SpriteFrame[] Ramp(double duration, Func<double, SpriteFrame> at)
    {
        const double step = 1.0 / 240;
        var count = (int)Math.Ceiling(duration / step);
        var frames = new SpriteFrame[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var time = Math.Min(duration, i * step);
            frames[i] = at(time / duration) with { Time = time };
        }
        return frames;
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3); // ease-out cubic
}
