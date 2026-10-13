using System.Drawing;

namespace TrackSwipe.Overlay;

/// <summary>WINDOWPLACEMENT can retain an unsnapped normal rectangle; remember the actual visible frame.</summary>
internal sealed class RestoreFrameHistory
{
    private readonly Dictionary<IntPtr, (Rectangle Normal, Rectangle Frame, uint Process)> _frames = new();
    public void Remember(IntPtr window, Rectangle normal, Rectangle frame, uint process) => _frames[window] = (normal, frame, process);
    public Rectangle? Find(IntPtr window, Rectangle normal, uint process) =>
        _frames.TryGetValue(window, out var entry) && entry.Normal == normal && entry.Process == process ? entry.Frame : null;
    public void Prune(IEnumerable<IntPtr> windows)
    {
        var alive = windows.ToHashSet();
        foreach (var window in _frames.Keys.Where(window => !alive.Contains(window)).ToArray()) _frames.Remove(window);
    }
}
