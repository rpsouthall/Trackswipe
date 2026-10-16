#if DIAGNOSTICS
using TrackSwipe.Interop;

namespace TrackSwipe;

internal sealed partial class TrayApp
{
    /// <summary>
    /// For --diagnose-taskbar-options: loads TrackSwipeTap.dll into Explorer, hides the Start button and the edge line,
    /// checks the taskbar's live XAML tree shows them collapsed, then gives them back and checks again. Quits after.
    /// </summary>
    private void RunTaskbarOptionsDiagnostics()
    {
        var step = 0;
        var started = Clock.Now;
        var timer = new UiTimer { Interval = 100 };
        var tree = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "taskbar-tree.txt");
        string State()
        {
            TaskbarTap.Dump();
            if (!File.Exists(tree)) return "no tree";
            var lines = File.ReadAllLines(tree);
            string Of(string marker) => lines.FirstOrDefault(line => line.Contains(marker)) is { } line
                ? line.Contains("COLLAPSED") ? "collapsed" : "shown" : "not found";
            // Where the app buttons begin: the button strip's left edge, and the first on-screen app icon.
            var strip = lines.FirstOrDefault(line => line.Contains("#TaskbarFrameRepeater"))?.Trim() is { } s ? System.Text.RegularExpressions.Regex.Match(s, @"at (-?\d+),").Groups[1].Value : "?";
            var icon = lines.Where(line => line.Contains("Controls.Image #Icon at "))
                .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"at (-?\d+),(-?\d+)"))
                .Where(m => m.Success && int.Parse(m.Groups[1].Value) >= 0 && int.Parse(m.Groups[2].Value) >= 0)
                .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Min();
            // Open-app indicators on screen and how many are collapsed; tray glyphs (icon fonts) and their sizes; the tray's extent.
            var indicators = lines.Where(line => line.Contains("#RunningIndicator") && !line.Contains(" at -")).ToList();
            var glyphs = lines.Where(line => line.Contains(" glyph U+")).Select(line => System.Text.RegularExpressions.Regex.Match(line, @"size (\d+)x(\d+) font ([\d.]+)"))
                .Where(m => m.Success).Select(m => $"{m.Groups[3].Value}pt {m.Groups[1].Value}x{m.Groups[2].Value}").Distinct();
            var tray = lines.FirstOrDefault(line => line.Contains("#SystemTrayFrameGrid"))?.Trim() is { } g ? System.Text.RegularExpressions.Regex.Match(g, @"at (\d+),\d+ size (\d+)x").Value : "?";
            return $"Start button {Of("[StartButton]")}, edge line {Of("#BackgroundStroke")}, buttons start at x {strip}, first icon at x {icon}, " +
                $"indicators {indicators.Count(line => line.Contains("COLLAPSED"))}/{indicators.Count} collapsed, tray glyphs [{string.Join(", ", glyphs)}], tray {tray}; status 0x{TaskbarTap.Query():X}";
        }
        void Finish() { timer.Dispose(); ExitOnShell(); }
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - started;
            switch (step)
            {
                case 0:
                    Log.Write($"Taskbar options diagnostic: DLL in this build {TaskbarTap.Available}, already in Explorer {TaskbarTap.Loaded}.");
                    if (!TaskbarTap.Load()) { Log.Write("Taskbar options diagnostic: couldn't load the DLL."); Finish(); return; }
                    step++; return;
                case 1 when !TaskbarTap.Loaded && t < 6: return;
                case 1:
                    Log.Write($"Taskbar options diagnostic: Explorer's copy listening {TaskbarTap.Loaded} after {t * 1000:0} ms; before: {State()}.");
                    _taskbarTimer?.Stop(); // or the saved options, applied at start-up, would land on top of these
                    TaskbarTap.Send(true, true, true, true);
                    started = Clock.Now; step++; return;
                case 2 when t < 0.8: return;
                case 2:
                    Log.Write($"Taskbar options diagnostic: options on: {State()}.");
                    TaskbarTap.Restore();
                    started = Clock.Now; step++; return;
                case 3 when t < 0.8: return;
                case 3:
                    Log.Write($"Taskbar options diagnostic: options off: {State()}.");
                    Finish(); return;
            }
        };
        timer.Start();
    }
}
#endif
