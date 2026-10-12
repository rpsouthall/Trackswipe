using TrackSwipe.Core;
using TrackSwipe.Desktops;
using TrackSwipe.Interop;
using System.Diagnostics;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Overlay;

/// <summary>
/// The last step of closing Mission Control, run while the overlay still covers the screen, so the
/// moment it comes off the real desktop looks exactly like the overlay did:
/// <list type="number">
/// <item>wait for an instant desktop switch (already requested) to land;</item>
/// <item>restore the chosen window if it's minimized, and bring it to the front. Windows' own restore
/// animation plays underneath the overlay, unseen. Restoring makes DWM blank the window's thumbnail
/// for a frame, so a still of the landed window is put over it first;</item>
/// <item>wait for the real window to come to rest;</item>
/// <item>if it rested somewhere other than where its thumbnail landed (a snapped window, an app that
/// repositions itself), glide the thumbnail there.</item>
/// </list>
/// </summary>
internal sealed class WindowHandoff
{
    private const double SwitchTimeout = 0.5;
    private const double SettleTimeout = 0.8;
    private const double RestoreAnimation = 0.3; // Windows 11's restore animation, when it can't be turned off
    private const double RepaintMinimum = 0.08;  // GPU-rendered apps don't report pending paints; give them a few frames
    private const int StableFrames = 2;

    private enum Step { Switching, Freezing, Activating, Settling, Correcting, Done }

    private readonly Guid? _desktop;
    private readonly SwitchLanding? _landing;
    private readonly Sprite? _sprite;
    private readonly Sprite? _backing;
    private readonly IntPtr _window;
    private readonly Action<Rectangle>? _freeze;
    private readonly Action? _unfreeze;
    private Rectangle _landed;
    private Step _step;
    private double _stepStarted;
    private bool _switchSeen;
    private double _restingAfter;
    private bool _transitionsDisabled;
    private Rectangle? _lastFrame;
    private int _stableFrames;
    private SettleMotion? _correction;
    private Rectangle _correctFrom, _correctTo;
    private readonly double _started;
    private readonly Rectangle _predicted;
    private bool _restored;

    /// <param name="desktop">The desktop an instant switch was requested to, or null if not switching.</param>
    /// <param name="landing">Tells when that switch is on screen; null if not switching.</param>
    /// <param name="sprite">The chosen window's thumbnail, already resting at <paramref name="landed"/>; null when nothing was chosen.</param>
    /// <param name="freeze">Covers a rectangle of the overlay with a still of itself; <paramref name="unfreeze"/> removes it.</param>
    public WindowHandoff(Guid? desktop, SwitchLanding? landing, Sprite? sprite, Sprite? backing, Rectangle landed, double now,
        Action<Rectangle>? freeze = null, Action? unfreeze = null)
    {
        _freeze = freeze;
        _unfreeze = unfreeze;
        _desktop = desktop;
        _landing = landing;
        _sprite = sprite;
        _backing = backing;
        _window = sprite?.Item.Window ?? IntPtr.Zero;
        _landed = _predicted = landed;
        _started = now;
        Enter(desktop is not null ? Step.Switching : Step.Activating, now);
    }

    public bool IsDone => _step == Step.Done;

    /// <summary>Advances one display frame.</summary>
    public void Tick(double now)
    {
        var elapsed = now - _stepStarted;
        switch (_step)
        {
            case Step.Switching:
                // Proceed one frame after the switch is visible, so it has been composed.
                if (_switchSeen || elapsed > SwitchTimeout)
                {
                    if (!_switchSeen) Log.Write("Desktop switch wasn't confirmed in time; carrying on.");
                    Enter(Step.Activating, now);
                }
                else if (_landing?.HasLanded(now) ?? true)
                {
                    _switchSeen = true;
                }
                break;

            case Step.Freezing: // the still has been on screen for a frame; now it's safe to restore
                Activate(now);
                break;

            case Step.Settling:
                Settle(now, elapsed);
                break;

            case Step.Correcting when _correction is { } motion:
                if (elapsed >= motion.Duration)
                {
                    Place(_correctTo);
                    Enter(Step.Done, now);
                }
                break;
        }
    }

    /// <summary>Call once the overlay is gone, to undo anything done to the window along the way.</summary>
    public void Finish()
    {
        if (!_transitionsDisabled) return;
        var enabled = 0;
        DwmSetWindowAttribute(_window, DWMWA_TRANSITIONS_FORCEDISABLED, ref enabled, sizeof(int));
        _transitionsDisabled = false;
    }

    private void Enter(Step step, double now)
    {
        _step = step;
        _stepStarted = now;
        if (step == Step.Activating)
        {
            if (_freeze is not null && _window != IntPtr.Zero && IsWindow(_window) && IsIconic(_window))
            {
                _freeze(_landed);
                _step = Step.Freezing;
            }
            else
            {
                Activate(now);
            }
        }
        if (step == Step.Done && _window != IntPtr.Zero)
            Log.Write($"Hand-off: {(now - _started) * 1000:0} ms, switched desktop {_desktop is not null}, restored {_restored}, " +
                $"restore animation {(_restored ? _transitionsDisabled ? "disabled" : "waited out" : "n/a")}, predicted {_predicted}, rested {_lastFrame?.ToString() ?? "unknown"}, " +
                $"corrected {_correction is not null}, foreground {GetForegroundWindow() == _window}");
    }

    private void Activate(double now)
    {
        if (_window == IntPtr.Zero || !IsWindow(_window))
        {
            Enter(Step.Done, now);
            return;
        }

        _restingAfter = now;
        if (IsIconic(_window))
        {
            _restored = true;
            // Ask DWM to skip the restore animation; where Windows won't let us, wait it out under the overlay.
            var disabled = 1;
            _transitionsDisabled = DwmSetWindowAttribute(_window, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int)) == 0;
            _restingAfter = now + (!_transitionsDisabled && WindowsAnimateMinimizing() ? RestoreAnimation : RepaintMinimum);
            ShowWindowAsync(_window, SW_RESTORE); // async: a hung app must not hang us
        }
        // Raise it explicitly as well: z-order isn't subject to the foreground lock, so even if Windows refuses
        // to activate it, it still ends up where its thumbnail landed (above every normal window).
        SetWindowPos(_window, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        Foreground.Take(_window);
        _step = Step.Settling;
        _stepStarted = now;
    }

    /// <summary>Waits until the real window has stopped moving, then glides the thumbnail onto it if needed.</summary>
    private void Settle(double now, double elapsed)
    {
        var frame = RealFrame();
        if (frame is not null && frame == _lastFrame) _stableFrames++;
        else _stableFrames = 0;
        _lastFrame = frame;

        // At rest: not moving, past any restore animation, and nothing left to repaint, so the real window
        // shows its content rather than a blank frame the moment the overlay comes off.
        var resting = _stableFrames >= StableFrames && now >= _restingAfter && !(_restored && PaintPending(_window));
        if (!resting && elapsed < SettleTimeout) return;
        if (!resting) Log.Write("The chosen window didn't settle in time; closing anyway.");

        if (frame is not { } target || Near(target, _landed))
        {
            Enter(Step.Done, now);
            return;
        }

        _unfreeze?.Invoke(); // the live thumbnail is good again by now, and it's what will move
        _correctFrom = _landed;
        _correctTo = target;
        _correction = SettleMotion.Create(new SpringPreset(0.22, 1.0), 0, 0, 1, 0.002);
        Enter(Step.Correcting, now);
        _sprite?.RefreshSourceSize();
        var frames = _correction.Samples.Select(sample =>
        {
            var rect = LayoutRect.Lerp(ToLayout(_correctFrom), ToLayout(_correctTo), sample.Value);
            return new SpriteFrame(sample.Time, new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height), 1, null);
        }).ToArray();
        var begin = (long)(now * Stopwatch.Frequency);
        _sprite?.Animate(frames, begin);
        _backing?.Animate(frames, begin);
        Composition.Flush();
    }

    /// <summary>The real window's visible frame in the thumbnail surface's coordinates, once it's restored.</summary>
    private Rectangle? RealFrame()
    {
        if (_sprite is null || !IsWindow(_window) || IsIconic(_window)) return null;
        if (DwmGetWindowAttribute(_window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0) return null;
        var frame = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        frame.Offset(-_sprite.Surface.Monitor.X, -_sprite.Surface.Monitor.Y);
        return frame;
    }

    private void Place(Rectangle rect)
    {
        _landed = rect;
        _backing?.Place(rect);
        _sprite?.Place(rect);
    }

    private static bool Near(Rectangle a, Rectangle b) =>
        Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    /// <summary>Whether the window or any of its child windows still has an unpainted region.</summary>
    private static bool PaintPending(IntPtr window)
    {
        if (GetUpdateRect(window, IntPtr.Zero, false)) return true;
        var pending = false;
        var checkedChildren = 0;
        EnumChildWindows(window, (child, _) =>
        {
            pending = IsWindowVisible(child) && GetUpdateRect(child, IntPtr.Zero, false);
            return !pending && ++checkedChildren < 256;
        }, IntPtr.Zero);
        return pending;
    }

    private static bool WindowsAnimateMinimizing()
    {
        var info = new ANIMATIONINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<ANIMATIONINFO>() };
        return !SystemParametersInfoW(SPI_GETANIMATION, (uint)info.Size, ref info, 0) || info.MinAnimate != 0;
    }

    private static LayoutRect ToLayout(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

}
