using Microsoft.Win32;

namespace TrackSwipe;

/// <summary>What the taskbar shows where its app buttons were, while the dock is on. Values mirror Protocol.h.</summary>
internal enum TopLeft
{
    /// <summary>Windows' own app buttons.</summary>
    AppIcons = 0,
    /// <summary>The name of the app in front, as a Mac's menu bar shows it (the default).</summary>
    AppName = 1,
    /// <summary>A dot for each virtual desktop and a pill for the current one, as GNOME's top bar shows them.</summary>
    Desktops = 2,
}

/// <summary>Remembers the tray menu's finger choice for the current Windows user.</summary>
internal static class GesturePreferences
{
    private const string KeyPath = @"Software\TrackSwipe";

    public static int LoadFingers()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("GestureFingers") is int fingers && fingers is 3 or 4)
                return fingers;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not load gesture preference: {ex.Message}");
        }
        return 4;
    }

    public static void SaveFingers(int fingers)
    {
        if (fingers is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(fingers));
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("GestureFingers", fingers, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not save gesture preference: {ex.Message}");
        }
    }

    // Taskbar options in the tray menu: personal preferences, so all off unless turned on.
    public const string HideStartButton = "HideStartButton", HideTaskbarEdge = "HideTaskbarEdge", HideTopTaskbarInMissionControl = "HideTopTaskbarInMissionControl",
        LargeTrayIcons = "LargeTrayIcons", HideRunningIndicators = "HideRunningIndicators";

    /// <summary>TrackSwipe's dock (pinned and open apps along the bottom of the screen). Off unless turned on.</summary>
    public const string ShowDock = "ShowDock";

    private const string TapFingersValue = "TapForStartFingers", OldThreeFingerTapValue = "ThreeFingerTapForStart";

    /// <summary>The fingers whose tap opens (or closes) Start: 3 or 4, or 0 for off (the default).</summary>
    public static int LoadTapFingers()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(TapFingersValue) is int fingers) return fingers is 3 or 4 ? fingers : 0;
            // Until four-finger taps were offered, it was a yes-or-no option for three fingers.
            return key?.GetValue(OldThreeFingerTapValue) is int on && on != 0 ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    public static void SaveTapFingers(int fingers)
    {
        if (fingers is not (0 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(fingers));
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(TapFingersValue, fingers, RegistryValueKind.DWord);
            key.DeleteValue(OldThreeFingerTapValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not save the tap for Start: {ex.Message}");
        }
    }

    /// <summary>What the taskbar shows where its app buttons were while the dock is on (see <see cref="TopLeft"/>).</summary>
    public const string TaskbarTopLeft = "TaskbarTopLeft";

    /// <summary>How see-through the taskbar is, in percent: 0 (Windows' own, the default) to 100 (clear).</summary>
    public const string TaskbarTransparency = "TaskbarTransparency";

    public static int LoadNumber(string name, int fallback, int min, int max)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int value && value >= min && value <= max ? value : fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return fallback;
        }
    }

    public static void SaveNumber(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not save {name}: {ex.Message}");
        }
    }

    public static bool LoadOption(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int value && value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void SaveOption(string name, bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not save {name}: {ex.Message}");
        }
    }
}
