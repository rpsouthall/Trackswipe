using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrackSwipe.Setup;

/// <summary>
/// Windows' own three- and four-finger swipes (Settings, Touchpad, "Swipes"), and its three-finger tap, which would fire
/// alongside TrackSwipe's. TrackSwipe sets the ones it uses to "Nothing", remembering the previous choice so switching or
/// uninstalling puts it back.
/// </summary>
internal static class NativeGestures
{
    private const string TouchpadKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    private const string SavedKey = @"Software\TrackSwipe\SavedWindowsGestures";

    private static string ValueFor(int fingers) => fingers == 3 ? "ThreeFingerSlideEnabled" : "FourFingerSlideEnabled";

    /// <summary>Whether Windows still handles swipes with this many fingers.</summary>
    public static bool Active(int fingers)
    {
        if (fingers is not (3 or 4)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(TouchpadKey);
        // Absent means Windows' default, which is on.
        return key?.GetValue(ValueFor(fingers)) is not int value || value != 0;
    }

    /// <summary>Turns Windows' swipe off for <paramref name="fingers"/> and restores the other count.</summary>
    public static void Claim(int fingers)
    {
        try
        {
            using var touchpad = Registry.CurrentUser.CreateSubKey(TouchpadKey);
            using var saved = Registry.CurrentUser.CreateSubKey(SavedKey);
            foreach (var count in new[] { 3, 4 })
            {
                var name = ValueFor(count);
                if (count == fingers)
                {
                    // Remember what the user had, once: a repeat claim mustn't overwrite it with our own 0.
                    if (saved.GetValue(name) is null) saved.SetValue(name, touchpad.GetValue(name) is int current ? current : -1);
                    touchpad.SetValue(name, 0, RegistryValueKind.DWord);
                }
                else Restore(touchpad, saved, name);
            }
            Log.Write($"Windows' own {fingers}-finger swipes set to Nothing.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Couldn't change Windows' touchpad gestures: {ex.Message}");
        }
        Reload();
    }

    private const string TapValue = "ThreeFingerTapEnabled";

    /// <summary>Whether Windows still does something with a three-finger tap (Search, by default).</summary>
    public static bool TapActive
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(TouchpadKey);
            return key?.GetValue(TapValue) is not int value || value != 0;
        }
    }

    /// <summary>Sets Windows' three-finger tap to "Nothing" while TrackSwipe's opens Start, remembering the previous choice.</summary>
    public static void ClaimTap()
    {
        try
        {
            using var touchpad = Registry.CurrentUser.CreateSubKey(TouchpadKey);
            using var saved = Registry.CurrentUser.CreateSubKey(SavedKey);
            if (saved.GetValue(TapValue) is null) saved.SetValue(TapValue, touchpad.GetValue(TapValue) is int current ? current : -1);
            touchpad.SetValue(TapValue, 0, RegistryValueKind.DWord);
            Log.Write("Windows' own three-finger tap set to Nothing.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Couldn't change Windows' three-finger tap: {ex.Message}");
        }
        Reload();
    }

    /// <summary>Gives Windows' three-finger tap back as it was.</summary>
    public static void RestoreTap()
    {
        try
        {
            using var touchpad = Registry.CurrentUser.CreateSubKey(TouchpadKey);
            using var saved = Registry.CurrentUser.CreateSubKey(SavedKey);
            Restore(touchpad, saved, TapValue);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Couldn't restore Windows' three-finger tap: {ex.Message}");
        }
        Reload();
    }

    /// <summary>Puts back whatever Windows' swipes (and three-finger tap) were set to before TrackSwipe changed them.</summary>
    public static void RestoreAll()
    {
        try
        {
            using var touchpad = Registry.CurrentUser.CreateSubKey(TouchpadKey);
            using var saved = Registry.CurrentUser.CreateSubKey(SavedKey);
            Restore(touchpad, saved, ValueFor(3));
            Restore(touchpad, saved, ValueFor(4));
            Restore(touchpad, saved, TapValue);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Couldn't restore Windows' touchpad gestures: {ex.Message}");
        }
        Reload();
    }

    private static void Restore(RegistryKey touchpad, RegistryKey saved, string name)
    {
        if (saved.GetValue(name) is not int previous) return;
        if (previous < 0) touchpad.DeleteValue(name, throwOnMissingValue: false);
        else touchpad.SetValue(name, previous, RegistryValueKind.DWord);
        saved.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Asks Windows to reload touchpad settings, as the Settings page does after a change: re-applies the current
    /// touchpad parameters with a change notification.
    /// </summary>
    private static unsafe void Reload()
    {
        const uint SPI_GETTOUCHPADPARAMETERS = 0x00AE, SPI_SETTOUCHPADPARAMETERS = 0x00AF;
        var parameters = stackalloc byte[48];
        *(uint*)parameters = 1; // TOUCHPAD_PARAMETERS_VERSION_1
        if (SystemParametersInfoW(SPI_GETTOUCHPADPARAMETERS, 48, parameters, 0))
            SystemParametersInfoW(SPI_SETTOUCHPADPARAMETERS, 48, parameters, 0x2 /* SPIF_SENDCHANGE */);
        SendMessageTimeoutW((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "PrecisionTouchPad", 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern unsafe bool SystemParametersInfoW(uint action, uint param, void* value, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
