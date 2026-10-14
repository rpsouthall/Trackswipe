using TrackSwipe.Core;
using TrackSwipe.Setup;

namespace TrackSwipe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Write($"Fatal: {e.ExceptionObject}"); Log.Flush(); };
        Log.VerboseEnabled = Environment.GetEnvironmentVariable("TRACKSWIPE_VERBOSE") == "1" || Has(args, "--verbose");

        if (WindowsBuild.Build < WindowsBuild.Minimum)
        {
            Refuse($"TrackSwipe needs Windows 11 (build {WindowsBuild.Minimum} or later); this is Windows {WindowsBuild.Describe()}.");
            return;
        }
        if (Has(args, "--uninstall")) { Installer.Uninstall(); return; }
#if !DIAGNOSTICS
        // A downloaded copy offers to install (or update) itself, unless asked to just run.
        if (!Installer.RunningInstalled && !Has(args, "--portable") && !Has(args, "--restarted") &&
            Installer.Run() == Installer.Choice.Quit) return;
#endif

        // In administrator mode, a normal launch (Start menu, double-click) hands over to the sign-in task, which
        // starts TrackSwipe elevated without a UAC prompt.
        if (Installer.RunningInstalled && !Startup.IsElevated && !Has(args, "--restarted") && Startup.TaskExists && Startup.RunTask()) return;

        // One instance per session. A copy started by setup or a restart waits for the previous one to exit.
        var waitForPrevious = Has(args, "--welcome") || Has(args, "--restarted") || Has(args, "--register-startup-task");
        var instance = AcquireInstance(waitForPrevious ? 10000 : 0);
        if (instance is null) return;
        if (Has(args, "--register-startup-task") && Startup.IsElevated) Startup.Enable(Environment.ProcessPath!);

        var (options, preview, diagnose, missionDiagnostics) = ParseArguments(args);
        TrayApp app;
        try
        {
            app = new TrayApp(options, preview, diagnose, missionDiagnostics, Has(args, "--welcome"), Has(args, "--admin-declined"));
        }
        catch (PlatformNotSupportedException ex)
        {
            Refuse($"{ex.Message}\n\nTrackSwipe relies on parts of Windows 11 that this version doesn't have.");
            instance.Dispose();
            return;
        }
        using (app) MessageLoop.Run(app.HandleFault);
        Log.Flush();
        // Let the next copy in before starting it: a mutex made by an elevated copy can't be opened by a normal one.
        instance.ReleaseMutex();
        instance.Dispose();
        Restart(app.Restart);
    }

    private static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static Mutex? AcquireInstance(int waitMilliseconds)
    {
        var deadline = Environment.TickCount64 + waitMilliseconds;
        while (true)
        {
            try
            {
                var mutex = new Mutex(false, @"Local\TrackSwipe");
                try
                {
                    if (mutex.WaitOne(Math.Max(0, (int)(deadline - Environment.TickCount64)))) return mutex;
                }
                catch (AbandonedMutexException) { return mutex; } // the previous copy ended without releasing it
                mutex.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                // Held by a copy running as administrator.
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Starts TrackSwipe again, switching administrator mode; sign-in startup follows along.</summary>
    private static void Restart(RestartMode mode)
    {
        var exe = Environment.ProcessPath!;
        switch (mode)
        {
            case RestartMode.Elevated:
                var startup = Startup.Enabled ? " --register-startup-task" : "";
                if (Startup.RunElevated(exe, "--restarted" + startup)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restarted") { UseShellExecute = false });
                return;
            case RestartMode.Normal:
                if (Startup.TaskExists) { Startup.Disable(); Startup.SetRunEntry(exe); }
                Startup.RunUnelevated(exe);
                return;
        }
    }

    private static void Refuse(string message)
    {
        Log.Write($"Can't start: {message}");
        Log.Flush();
        MessageBoxW(IntPtr.Zero, message, "TrackSwipe", 0x30 /* MB_ICONWARNING */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
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
#if DIAGNOSTICS
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
                case "--diagnose-window-drag-demo" when long.TryParse(next, out var demoWindow) && i + 2 < args.Length && int.TryParse(args[i + 2], out var demoDesktop):
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DRAG_DEMO", $"{demoWindow}:{demoDesktop}");
                    i += 2;
                    break;
                case "--diagnose-window-drag" when long.TryParse(next, out var dragged):
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_WINDOW_DRAG", dragged.ToString());
                    i++;
                    break;
                case "--diagnose-desktops":
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DESKTOPS", "1");
                    break;
                case "--diagnose-input":
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_INPUT", "1");
                    break;
                case "--diagnose-bar-hover":
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_BAR_HOVER", "1");
                    break;
                case "--diagnose-slide-catch":
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE_CATCH", "1");
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SYNTHETIC", "1");
                    break;
                case "--diagnose-slide-roundtrip":
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE_ROUNDTRIP", "1");
                    break;
                case "--diagnose-peek":
                    var peekMode = next?.ToLowerInvariant() is "reverse" or "hold" ? next.ToLowerInvariant() : "commit";
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_PEEK", peekMode);
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SYNTHETIC", "1");
                    if (peekMode != "commit") i++;
                    break;
                case "--diagnose-slide" when int.TryParse(next, out var slide) && slide is 1 or -1:
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE", slide.ToString());
                    i++;
                    break;
                case "--diagnose-dwell" when int.TryParse(next, out var dwellDesktop):
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DWELL", dwellDesktop.ToString());
                    i++;
                    break;
                case "--diagnose-zoom" when int.TryParse(next, out var zoomDesktop):
                    Environment.SetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_ZOOM", zoomDesktop.ToString());
                    i++;
                    break;
                case "--diagnose-mission":
                    diagnoseMission = true;
                    break;
                case "--switch" when int.TryParse(next, out var direction) && direction is 1 or -1:
                    switchDirection = direction;
                    i++;
                    break;
#endif
            }
        }
        return (options, preview, diagnose, diagnoseMission ? switchDirection : null);
    }
}
