using Microsoft.Win32;

namespace TrackSwipe;

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
}
