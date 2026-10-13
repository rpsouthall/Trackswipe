using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe.Touchpad;

/// <summary>
/// Reads the touchpad on its own thread, so 125 reports a second never compete with frame rendering
/// on the UI thread. Frames are queued; the UI thread drains them before each display frame, and when
/// nothing is animating, after one coalesced <see cref="FramesWaiting"/> notification.
/// </summary>
internal sealed class TouchpadThread : IDisposable
{
    private readonly TouchFrameQueue _frames = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private Exception? _startError;
    private uint _threadId;
    private int _notified;
    private volatile bool _forwardAll;
    private bool _engaged; // touchpad thread only

    private volatile int _gestureFingers = 3;

    /// <summary>
    /// Set while a gesture is in progress or the input monitor is open: then every report is delivered.
    /// Otherwise reports with fewer fingers than a gesture uses (pointer movement, two-finger scrolling:
    /// nearly all touchpad use) are dropped on this thread, so they never wake the composition thread.
    /// </summary>
    public bool ForwardAll { get => _forwardAll; set => _forwardAll = value; }

    /// <summary>The gesture's finger count.</summary>
    public int GestureFingers { get => _gestureFingers; set => _gestureFingers = value; }

    public TouchpadThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "TrackSwipe touchpad", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
        if (_startError is not null) throw _startError;
    }

    /// <summary>Raised on the touchpad thread when frames arrive and the queue was empty since the last drain.</summary>
    public event Action? FramesWaiting;

    /// <summary>Raised on the touchpad thread.</summary>
    public event Action<TouchpadDevice>? DeviceFound;

    /// <summary>Queues a synthetic frame (previews and diagnostics), exactly as a real one would be.</summary>
    public void Inject(TouchFrame frame) => Enqueue(frame);

    /// <summary>Hands every queued frame, oldest first, to <paramref name="handle"/>. Call on the UI thread.</summary>
    public void Drain(Action<TouchFrame> handle)
    {
        Volatile.Write(ref _notified, 0);
        while (_frames.TryDequeue(out var frame)) handle(frame);
    }

    /// <summary>After stopping frame sampling, re-arm any notification skipped during the last frame.</summary>
    public void WakeIfPending()
    {
        Volatile.Write(ref _notified, 0);
        if (!_frames.IsEmpty && Interlocked.Exchange(ref _notified, 1) == 0) FramesWaiting?.Invoke();
    }

    public void Dispose()
    {
        if (_threadId != 0) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    private void Run()
    {
        RawInputTouchpad touchpad;
        try
        {
            touchpad = new RawInputTouchpad();
#if DIAGNOSTICS
            // Self-tests that inject touches ignore the real touchpad, so someone using it can't disturb them.
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SYNTHETIC") != "1")
#endif
            touchpad.FrameReceived += EnqueueIfRelevant;
            touchpad.DeviceFound += device => DeviceFound?.Invoke(device);
            _threadId = NativeMethods.GetCurrentThreadId();
        }
        catch (Exception ex)
        {
            _startError = ex;
            _started.Set();
            return;
        }
        _started.Set();

        while (NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
        touchpad.Dispose();
    }

    /// <summary>
    /// An idle engine reacts only to the gesture's finger count. The report where the count falls below it is
    /// always delivered, even if the engine's state lags behind this thread, so a gesture can never miss its lift.
    /// </summary>
    private void EnqueueIfRelevant(TouchFrame frame)
    {
        if (TouchFilter.Deliver(ActiveCount(frame), _gestureFingers, _forwardAll, ref _engaged)) Enqueue(frame);
    }

    private static int ActiveCount(TouchFrame frame)
    {
        var active = 0;
        foreach (var contact in frame.Contacts) if (contact.IsActive) active++;
        return active;
    }

    private void Enqueue(TouchFrame frame)
    {
        _frames.Enqueue(frame);
        if (Interlocked.Exchange(ref _notified, 1) == 0) FramesWaiting?.Invoke();
    }
}
