using Microsoft.Win32;

namespace TrackSwipe;

/// <summary>The Windows build, for the log and for refusing to start where the APIs this app needs don't exist.</summary>
internal static class WindowsBuild
{
    /// <summary>Windows 11 21H2: DirectComposition's compositor clock and the shared thumbnail visuals.</summary>
    public const int Minimum = 22000;

    public static int Build => Environment.OSVersion.Version.Build;

    public static string Describe()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion");
        var revision = key?.GetValue("UBR") is int ubr ? $".{ubr}" : "";
        var release = key?.GetValue("DisplayVersion") as string;
        return $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Build}{revision}" + (release is null ? "" : $" ({release})");
    }
}
