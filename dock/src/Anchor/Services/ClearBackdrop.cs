using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Anchor.Services;

/// <summary>
/// A backdrop of nothing at all: with it, a window is see-through wherever its content doesn't draw, so a dock shows
/// only its icons (and their highlights and dots) floating over the desktop. The "clear dock" option.
/// <para>
/// A system backdrop is a brush from the system compositor (Windows.UI.Composition, not WinUI's own), drawn behind the
/// window's content; a fully transparent colour brush is the whole of this one. That compositor needs a
/// <c>Windows.System</c> dispatcher queue on the thread, which a WinUI app's UI thread doesn't have of its own, so one
/// is made for it the first time.
/// </para>
/// </summary>
internal sealed partial class ClearBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static nint _queueController; // kept for the life of the process, as the compositor relies on it

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        connectedTarget.SystemBackdrop = Compositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private static Windows.UI.Composition.Compositor Compositor()
    {
        if (_compositor is not null)
            return _compositor;
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions
            {
                Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = 2,    // DQTYPE_THREAD_CURRENT
                ApartmentType = 2, // DQTAT_COM_STA
            };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _queueController));
        }
        return _compositor = new Windows.UI.Composition.Compositor();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size, ThreadType, ApartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
}
