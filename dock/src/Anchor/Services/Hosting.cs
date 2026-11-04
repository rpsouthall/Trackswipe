using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// Whether this dock is TrackSwipe's: installed by TrackSwipe into a <c>Dock</c> folder beside
/// <c>TrackSwipe.exe</c>, and started, stopped and set up from TrackSwipe's notification-area menu.
/// <para>
/// A hosted dock has no tray icon of its own (TrackSwipe's is the one), doesn't start with Windows
/// on its own (TrackSwipe starts it), and doesn't look for updates (it updates with TrackSwipe).
/// TrackSwipe drives it with one registered message, <see cref="CommandMessage"/>, whose wParam is
/// the command. Detected from where it is installed, so nothing has to be passed to it — TrackSwipe
/// starts it through Explorer (as the signed-in user, never elevated), which can't pass arguments.
/// <c>ANCHOR_HOSTED=1</c> says the same for development.
/// </para>
/// </summary>
public static class Hosting
{
    public static bool ByTrackSwipe { get; } = Detect();

    /// <summary>Posted by TrackSwipe to the dock's message window; wParam is one of the commands below.</summary>
    public static readonly uint CommandMessage = NativeMethods.RegisterWindowMessage("TrackSwipe.Dock");

    public const int QuitCommand = 1, SettingsCommand = 2, ShowCommand = 3;

    /// <summary>
    /// Posted by TrackSwipe's taskbar label (the app in front, by name) when it's clicked: wParam is
    /// the app's window, lParam the screen point (x low, y high). The dock shows that app's
    /// taskbar-style menu there.
    /// </summary>
    public static readonly uint AppMenuMessage = NativeMethods.RegisterWindowMessage("TrackSwipe.Dock.AppMenu");

    private static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("ANCHOR_HOSTED") == "1")
            return true;
        try
        {
            var folder = Path.GetDirectoryName(Environment.ProcessPath);
            var parent = folder is null ? null : Path.GetDirectoryName(folder);
            return parent is not null &&
                   string.Equals(Path.GetFileName(folder), "Dock", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(Path.Combine(parent, "TrackSwipe.exe"));
        }
        catch
        {
            return false;
        }
    }
}
