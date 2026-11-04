namespace Anchor.Services;

/// <summary>
/// Lets the native half of objects that are already dead go: a closed fly-out's popup window, a
/// closed dialog's composition and XAML resources.
/// <para>
/// A WinUI wrapper releases what it owns natively only when its finalizer runs, and the finalizer
/// runs only when the garbage collector does — but this process's managed heap is a few megabytes,
/// so the collector never has a reason to run while the native side piles up. Measured on a build
/// with nothing else going on: 30 group fly-out opens left 31 hidden <c>PopupWindowSiteBridge</c>
/// windows, 90 USER objects (the quota is 10 000 per process), about 325 handles and 13 MB behind,
/// and none of it came back until a collection ran; with one queued after each close the process
/// stayed at its starting window count. Nothing roots those wrappers (gcroot finds no path), so this
/// is not a leak in the sense of a reference held — it is a collection that never gets asked for.
/// </para>
/// <para>
/// Asked for, then, but rarely: <see cref="Request"/> schedules one collection a few seconds out
/// (so a burst of closes — hovering across three groups — costs one), and never more often than
/// <see cref="MinGap"/>. It runs on a pool thread, not the UI thread, because
/// <c>WaitForPendingFinalizers</c> must never be able to wait on a thread that a finalizer's
/// release might itself need.
/// </para>
/// </summary>
public static class NativeReclaim
{
    /// <summary>How long after the first request in a burst the collection runs.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    /// <summary>The least time between two collections, however many requests arrive.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static Timer? _timer;
    private static bool _scheduled;
    private static long _lastRunTicks = long.MinValue;

    /// <summary>
    /// When a collection requested now should run: <see cref="Delay"/> from now, pushed out if the
    /// last one was less than <see cref="MinGap"/> ago. <paramref name="sinceLastRun"/> is null when
    /// none has run yet.
    /// </summary>
    public static TimeSpan DelayUntilNext(TimeSpan? sinceLastRun)
    {
        if (sinceLastRun is not { } since)
            return Delay;
        var untilGapIsUp = MinGap - since;
        return untilGapIsUp > Delay ? untilGapIsUp : Delay;
    }

    /// <summary>Asks for a collection soon. Cheap, safe from any thread, and coalesced.</summary>
    public static void Request()
    {
        lock (Gate)
        {
            if (_scheduled)
                return;

            TimeSpan? since = _lastRunTicks == long.MinValue
                ? null
                : TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastRunTicks);
            _scheduled = true;
            _timer ??= new Timer(_ => Run());
            _timer.Change(DelayUntilNext(since), Timeout.InfiniteTimeSpan);
        }
    }

    private static void Run()
    {
        try
        {
            // Twice: the first pass queues the finalizers, the second drops what they released.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            Diag.Log("NativeReclaim: " + ex.Message);
        }
        finally
        {
            lock (Gate)
            {
                _lastRunTicks = Environment.TickCount64;
                _scheduled = false;
            }
        }
    }
}
