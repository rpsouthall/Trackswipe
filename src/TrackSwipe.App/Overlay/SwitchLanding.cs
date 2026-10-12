namespace TrackSwipe.Overlay;

/// <summary>
/// Decides when an instant desktop switch has visibly landed, so the overlay can come off. The shell
/// uncloaks the new desktop's windows and cloaks the old desktop's as the switch lands. Explorer's
/// current-desktop value changes the moment the switch is requested, before the screen does, so it only
/// counts after a delay: when neither desktop has a window to watch, or the old desktop's window is
/// shown on every desktop and never cloaks.
/// </summary>
internal sealed class SwitchLanding
{
    /// <summary>How long a switch with nothing to watch is given to reach the screen.</summary>
    public const double UnwatchedDelay = 0.3;

    private readonly IntPtr _arriving, _leaving;
    private readonly Func<IntPtr, bool?> _isShown;
    private readonly Func<bool> _idChanged;
    private readonly double _requested;

    /// <param name="arriving">A window on the target desktop (or zero): shown once the switch lands.</param>
    /// <param name="leaving">A window on the desktop being left (or zero): cloaked once the switch lands.</param>
    /// <param name="isShown">Whether DWM shows a window, or null if there's no such window.</param>
    /// <param name="idChanged">Whether Explorer reports the target as the current desktop.</param>
    public SwitchLanding(IntPtr arriving, IntPtr leaving, Func<IntPtr, bool?> isShown, Func<bool> idChanged, double requested)
    {
        _arriving = arriving;
        _leaving = leaving;
        _isShown = isShown;
        _idChanged = idChanged;
        _requested = requested;
    }

    public bool HasLanded(double now)
    {
        if (_isShown(_arriving) is bool arrived) return arrived; // the target's own window is the best evidence
        if (_isShown(_leaving) == false) return true;
        return _idChanged() && now - _requested >= UnwatchedDelay;
    }
}
