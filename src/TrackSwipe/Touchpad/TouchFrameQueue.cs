using TrackSwipe.Core;

namespace TrackSwipe.Touchpad;

/// <summary>
/// Reuses its storage between drains instead of allocating ConcurrentQueue segments during a
/// gesture. A short lock supports both the HID reader and injected diagnostic reports; callbacks
/// run outside the lock. A delayed consumer grows the buffer without dropping lift reports.
/// </summary>
internal sealed class TouchFrameQueue
{
    private readonly object _gate = new();
    private TouchFrame[] _frames = new TouchFrame[64];
    private int _head, _count;

    public bool IsEmpty { get { lock (_gate) return _count == 0; } }

    public void Enqueue(TouchFrame frame)
    {
        lock (_gate)
        {
            if (_count == _frames.Length)
            {
                var expanded = new TouchFrame[checked(_frames.Length * 2)];
                for (var i = 0; i < _count; i++) expanded[i] = _frames[(_head + i) % _frames.Length];
                _frames = expanded;
                _head = 0;
            }
            _frames[(_head + _count) % _frames.Length] = frame;
            _count++;
        }
    }

    public bool TryDequeue(out TouchFrame frame)
    {
        lock (_gate)
        {
            if (_count == 0) { frame = default; return false; }
            frame = _frames[_head];
            _frames[_head] = default; // release overflow snapshots from unusual devices
            _head = (_head + 1) % _frames.Length;
            _count--;
            return true;
        }
    }
}
