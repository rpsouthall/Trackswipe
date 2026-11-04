using System.Collections.Concurrent;

namespace Anchor.Services;

/// <summary>
/// Counts how often Anchor's recurring work runs, when ANCHOR_PROBE=1 — for checking that an idle
/// dock really is idle. Logged to the diagnostics log every 20 seconds. Costs one string compare
/// per call when off.
/// </summary>
public static class Probe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("ANCHOR_PROBE") == "1" ||
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\TrackSwipe", "DockProbe", 0) is 1; // for a dock TrackSwipe starts
    private static readonly ConcurrentDictionary<string, int> Counts = new();
    private static long _since = Environment.TickCount64;

    public static void Count(string what)
    {
        if (!Enabled)
            return;
        Counts.AddOrUpdate(what, 1, (_, n) => n + 1);
        if (Environment.TickCount64 - _since < 20000)
            return;
        _since = Environment.TickCount64;
        var line = string.Join(" ", Counts.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        Counts.Clear();
        Diag.Log("PROBE " + line);
    }
}
