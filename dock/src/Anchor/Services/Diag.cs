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

    /// <summary>
    /// For work on the dock's own thread, which must never keep the pointer waiting: says so in the log when a piece
    /// of it that began at <paramref name="started"/> (a <see cref="Stopwatch"/> timestamp) took long enough to feel
    /// (the dock not answering under the cursor). Costs two clock reads when it didn't.
    /// </summary>
    public static void Slow(string what, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalMilliseconds >= 80)
            Log($"SLOW {what}: the dock's thread was busy for {elapsed.TotalMilliseconds:0} ms");
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
