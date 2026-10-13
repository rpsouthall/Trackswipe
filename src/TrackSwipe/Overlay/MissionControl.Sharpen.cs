namespace TrackSwipe.Overlay;

internal sealed partial class MissionControl
{
    private const int SharpenDelay = 120, SharpenReveal = 50, SleepDelay = 600, BusyRetry = 500;
    private static readonly bool SharpenDisabled = Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_NO_SHARPEN") == "1";
    private UiTimer? _sharpen;
    private bool _sharpRevealPending;
    private Action? _afterWake;

    /// <summary>Whether a gesture is in progress or fingers rest on the pad; set by the overlay controller.</summary>
    public Func<bool>? InputBusy { get; set; }

    /// <summary>
    /// The overview came to rest: in a moment, give shrunken windows thumbnails that DWM draws at their shown
    /// size (filtered), then reveal them a few frames later once DWM is composing them, putting the scaled copies
    /// beneath to sleep. Before anything moves, <see cref="WakeScaledCopies"/> brings those back a frame early;
    /// whatever moves a window then drops its sharp copy in the same batch.
    /// </summary>
    private void ScheduleSharpen() => StartSharpenTimer(reveal: false, SharpenDelay);

    private void StartSharpenTimer(bool reveal, int interval)
    {
        if (SharpenDisabled) return; // diagnostics: compare with scaled thumbnails only
        if (_sharpen is null)
        {
            _sharpen = new UiTimer();
            _sharpen.Tick += (_, _) => SharpenStep();
        }
        _sharpen.Stop();
        _sharpRevealPending = reveal;
        _sharpen.Interval = interval;
        _sharpen.Start();
    }

    private bool AtRest => IsOpen && !IsAnimating && !_barRevealAnimating && _desktopDrag is null && _pan == 0 && InputBusy?.Invoke() != true;

    private void SharpenStep()
    {
        if (!AtRest)
        {
            if (_state == State.Open) _sharpen!.Interval = BusyRetry; // try again once fingers lift
            else _sharpen!.Stop();
            return;
        }
        if (!_sharpRevealPending)
        {
            foreach (var part in _plans.SelectMany(plan => plan.Parts)) part.Sprite.PrepareSharp();
            Interop.Composition.Flush();
            _sharpRevealPending = true;
            _sharpen!.Interval = SharpenReveal;
            return;
        }
        _sharpen!.Stop();
        _sharpRevealPending = false;
        foreach (var part in _plans.SelectMany(plan => plan.Parts)) part.Sprite.RevealSharp();
        Interop.Composition.Flush();
    }

    /// <summary>
    /// Something may move soon (fingers landed, a button pressed): wake the scaled copies under the sharp ones,
    /// so they're composed before they're uncovered. They sleep again if nothing moves. True if any were asleep.
    /// </summary>
    public bool WakeScaledCopies()
    {
        var woke = false;
        foreach (var part in _plans.SelectMany(plan => plan.Parts)) woke |= part.Sprite.WakeScaled();
        if (!woke) return false;
        Interop.Composition.Flush();
        StartSharpenTimer(reveal: true, SleepDelay);
        return true;
    }

    /// <summary>Runs <paramref name="action"/> now, or a frame later if scaled copies had to be woken first.</summary>
    private bool DeferUntilAwake(Action action)
    {
        if (!WakeScaledCopies()) return false;
        _afterWake = action;
        _afterWakeFrames = 0;
        FramesNeeded?.Invoke();
        return true;
    }

    private int _afterWakeFrames;
    private UiTimer? _afterWakeTimer;

    /// <summary>
    /// From <see cref="Tick"/>: once a display frame has passed since waking, run what waited, from a message
    /// right after this frame rather than inside it, as the action would have run without the wait. (Run
    /// inside a frame, a close onto a minimized window flashed at the end of its hand-off.)
    /// </summary>
    private void RunAfterWake()
    {
        if (_afterWake is null || ++_afterWakeFrames < 2) return;
        if (_afterWakeTimer is null)
        {
            _afterWakeTimer = new UiTimer { Interval = 1 };
            _afterWakeTimer.Tick += (_, _) =>
            {
                _afterWakeTimer.Stop();
                if (_afterWake is not { } action) return;
                _afterWake = null;
                action();
            };
        }
        if (!_afterWakeTimer.Enabled) _afterWakeTimer.Start();
    }

    /// <summary>Back to scaled thumbnails everywhere, e.g. when Mission Control hides.</summary>
    private void DropSharpThumbnails()
    {
        _sharpen?.Stop();
        _sharpRevealPending = false;
        _afterWake = null;
        foreach (var part in _plans.SelectMany(plan => plan.Parts)) part.Sprite.DropSharp();
    }
}
