using TrackSwipe.Core;

namespace TrackSwipe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Shared with the original TrackSwipe on purpose: both react to the same swipes, so only one may run.
        using var instance = new Mutex(true, @"Local\TrackSwipe", out var isFirstInstance);
        if (!isFirstInstance) return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");

        var (options, preview, diagnose, missionDiagnostics) = ParseArguments(args);
        TrayApp app;
        try
        {
            app = new TrayApp(options, preview, diagnose, missionDiagnostics);
        }
        catch (PlatformNotSupportedException ex)
        {
            Log.Write($"Can't start: {ex.Message}");
            MessageBox.Show($"{ex.Message}\n\nThe original TrackSwipe doesn't need this and still works.", "TrackSwipe GPU", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var _ = app;
        Application.ThreadException += (_, e) => app.HandleFault(e.Exception);
        Application.Run(app);
    }

    /// <summary>Supports --fingers 3|4|5, --distance &lt;mm&gt;, --smooth, --preview, --diagnose and --diagnose-mission [--switch ±1].</summary>
    /// <remarks>MissionDiagnostics is null when not requested, 0 for a round trip, or ±1 to really switch that way.</remarks>
    private static (GestureOptions Options, bool Preview, bool Diagnose, int? MissionDiagnostics) ParseArguments(string[] args)
    {
        var options = new GestureOptions { Fingers = GesturePreferences.LoadFingers() };
        bool preview = false, diagnose = false, diagnoseMission = false;
        var switchDirection = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--fingers" when int.TryParse(next, out var fingers) && fingers is >= 3 and <= 5:
                    options.Fingers = fingers;
                    i++;
                    break;
                case "--distance" when double.TryParse(next, System.Globalization.CultureInfo.InvariantCulture, out var mm) && mm is >= 20 and <= 150:
                    options.FullSwipeMm = mm;
                    i++;
                    break;
                case "--smooth":
                    options.Spring = SpringPreset.Smooth;
                    break;
                case "--preview":
                    preview = true;
                    break;
                case "--diagnose":
                    diagnose = true;
                    break;
                case "--diagnose-focus" or "--diagnose-swipe" when long.TryParse(next, out var handle):
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_FOCUS", handle.ToString());
                    if (args[i].Equals("--diagnose-swipe", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SWIPE", "1");
                    i++;
                    break;
                case "--diagnose-open":
                    var openCycles = 2;
                    if (int.TryParse(next, out var count))
                    {
                        openCycles = count;
                        i++;
                    }
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_OPEN", openCycles.ToString());
                    break;
                case "--diagnose-desktops":
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DESKTOPS", "1");
                    break;
                case "--diagnose-slide" when int.TryParse(next, out var slide) && slide is 1 or -1:
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE", slide.ToString());
                    i++;
                    break;
                case "--diagnose-mission":
                    diagnoseMission = true;
                    break;
                case "--switch" when int.TryParse(next, out var direction) && direction is 1 or -1:
                    switchDirection = direction;
                    i++;
                    break;
            }
        }
        return (options, preview, diagnose, diagnoseMission ? switchDirection : null);
    }
}
