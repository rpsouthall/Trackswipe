using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<TouchFrame> _frames = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private Exception? _startError;
    private uint _threadId;
    private int _notified;

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
            touchpad.FrameReceived += Enqueue;
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

    private void Enqueue(TouchFrame frame)
    {
        _frames.Enqueue(frame);
        if (Interlocked.Exchange(ref _notified, 1) == 0) FramesWaiting?.Invoke();
    }
}
