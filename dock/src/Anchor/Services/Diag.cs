using System.Diagnostics;

namespace Anchor.Services;

/// <summary>Lightweight file logger for diagnosing runtime layout/window issues.</summary>
public static class Diag
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "anchor.log");

    private static readonly object Gate = new();

    static Diag()
    {
        try { File.WriteAllText(LogPath, $"=== Anchor started {DateTime.Now:O} ===\n"); }
        catch { /* ignore */ }
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine("[Anchor] " + line);
        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, line + "\n");
        }
        catch { /* ignore */ }
    }
}
