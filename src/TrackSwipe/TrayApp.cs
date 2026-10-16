using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using TrackSwipe.Core;
using TrackSwipe.Interop;
using TrackSwipe.Overlay;
using TrackSwipe.Touchpad;

namespace TrackSwipe;

/// <summary>
/// Wires touchpad input, the gesture engine, the overlay and the tray menu together. The touchpad is read
/// on its own thread and display frames are paced by another; gesture and overlay logic runs on a
/// dedicated composition thread, independently of the tray and input monitor (the main thread).
/// </summary>
internal sealed partial class TrayApp : IDisposable
{
    private readonly GestureEngine _engine;
    private readonly TapDetector _tap = new(); // composition thread
    private volatile bool _tapForStart; // a three-finger tap opens Start
    private readonly TouchpadThread _touchpad;
    private readonly UiPump _pump;
    private readonly UiPump _shellPump;
    private readonly CompositionThread _compositionThread;
    private readonly Action<TouchFrame> _processFrame;
    private readonly OverlayController _overlay;
    private readonly FrameClock _clock;
    private readonly TrayIcon _tray;
    private readonly IntPtr _iconHandle;
    private readonly TrayMenuItem _enabledItem, _snappyItem, _smoothItem, _startupItem, _adminItem;
    private readonly TrayMenuItem _hideStartItem, _hideEdgeItem, _hideIndicatorsItem, _largeTrayIconsItem, _hideTopTaskbarItem;
    private UiTimer? _taskbarTimer;
    private int _taskbarAttempts;
    private readonly TrayMenuItem _gestureTitle, _threeFingerItem, _fourFingerItem, _tapForStartItem;
    private volatile string _device = "No precision touchpad input seen yet";
    private InputMonitorForm? _monitor;
    private UiTimer? _preview;
    private volatile bool _enabled = true;
    private volatile bool _shuttingDown;
    private volatile bool _monitorOpen;
    private bool _previewing;
    private bool _disposed;
    private readonly FramePerf _framePerf = new();
    private double _candidateSince = double.NegativeInfinity;
    private double _batchMargin = 0.001;
    private bool _firstTrackingFrame, _inDisplayFrame;
    private volatile bool _samplingInput;
    private const double CandidateWarmUp = 0.5; // how long resting fingers keep the clock (and boost) running
    private static readonly string AppVersion = typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private readonly bool _elevated = Setup.Startup.IsElevated;
    private Action? _balloonAction;
    private ForegroundWatch? _adminAppWatch;
    private TaskbarClickWatch? _taskbarClicks;

    /// <summary>Set when TrackSwipe should start again once this instance has exited (switching administrator mode).</summary>
    public RestartMode Restart { get; private set; }

    public TrayApp(GestureOptions options, bool preview, bool diagnose, int? missionDiagnostics, bool welcome = false, bool adminDeclined = false)
    {
        _shellPump = new UiPump(_ => { });
        _compositionThread = new CompositionThread(OnDisplayFrame, HandleFault, CleanupComposition, ExitOnShell);
        try
        {
            (_engine, _overlay, _clock) = _compositionThread.Invoke(() =>
            {
                Composition.Initialize();
                var engine = new GestureEngine(options);
                var overlay = new OverlayController(engine);
                return (engine, overlay, new FrameClock(_compositionThread.Pump));
            });
        }
        catch
        {
            _compositionThread.Invoke(Composition.Dispose);
            _compositionThread.Dispose();
            _shellPump.Dispose();
            throw;
        }
        _engine.TrackingStarted += _overlay.TrackingStarted;
        _engine.TrackingStarted += _ => _tap.Cancel();
        _tap.Tapped += () => _shellPump.Post(KeySender.ToggleStart);
        _engine.SettlingStarted += _overlay.SettlingStarted;
        _engine.SettlingInterrupted += _overlay.ResumeTracking;
        _engine.Committed += OnCommitted;
        _engine.Ended += () =>
        {
            _overlay.Ended();
            _previewing = false;
        };

        _pump = _compositionThread.Pump;
        _overlay.FramesNeeded += _clock.Start;

        _engine.CandidateStarted += () =>
        {
            // Fingers landed: start the frame clock (and with it the 120 Hz boost) now, so the display is
            // already at full rate when the swipe begins, and capture the scene while they cover the dead zone.
            _candidateSince = Clock.Now;
            _clock.Start();
            _overlay.Prewarm();
        };

        _processFrame = ProcessFrame;
        Action drainInput = DrainInput;
        _tapForStart = GesturePreferences.LoadOption(GesturePreferences.ThreeFingerTapForStart);
        _touchpad = new TouchpadThread { GestureFingers = ReportedFingers(options.Fingers) };
        _touchpad.FramesWaiting += () => { if (!_clock.IsRunning || !_samplingInput) _pump.Post(drainInput); };
        _touchpad.DeviceFound += device =>
        {
            _device = $"Touchpad: {device}";
            Log.Write(_device);
        };

        _enabledItem = new TrayMenuItem("Gestures enabled", () => SetEnabled(!_enabled)) { Checked = true };
        _snappyItem = new TrayMenuItem("Snappy", () => SetSpring(SpringPreset.Snappy));
        _smoothItem = new TrayMenuItem("Smooth", () => SetSpring(SpringPreset.Smooth));
        SetSpring(options.Spring);
        _gestureTitle = new TrayMenuItem(Title(options.Fingers)) { Enabled = false };
        _threeFingerItem = new TrayMenuItem("Three fingers", () => SetFingers(3)) { Checked = options.Fingers == 3 };
        _fourFingerItem = new TrayMenuItem("Four fingers", () => SetFingers(4)) { Checked = options.Fingers == 4 };
        _tapForStartItem = new TrayMenuItem("Three-finger tap opens Start", ToggleTapForStart) { Checked = _tapForStart };
        _startupItem = new TrayMenuItem("Start at sign-in", ToggleStartup) { Checked = Setup.Startup.Enabled };
        _adminItem = new TrayMenuItem("Run as administrator", () => RequestRestart(_elevated ? RestartMode.Normal : RestartMode.Elevated))
            { Checked = _elevated };
        // Personal taskbar preferences, all off unless turned on.
        var tapAvailable = Interop.TaskbarTap.Available;
        _hideStartItem = new TrayMenuItem("Hide Start button", () => ToggleOption(GesturePreferences.HideStartButton, _hideStartItem!))
            { Checked = GesturePreferences.LoadOption(GesturePreferences.HideStartButton), Enabled = tapAvailable };
        _hideEdgeItem = new TrayMenuItem("Hide the taskbar's edge line", () => ToggleOption(GesturePreferences.HideTaskbarEdge, _hideEdgeItem!))
            { Checked = GesturePreferences.LoadOption(GesturePreferences.HideTaskbarEdge), Enabled = tapAvailable };
        _hideIndicatorsItem = new TrayMenuItem("Hide the open-app indicators", () => ToggleOption(GesturePreferences.HideRunningIndicators, _hideIndicatorsItem!))
            { Checked = GesturePreferences.LoadOption(GesturePreferences.HideRunningIndicators), Enabled = tapAvailable };
        _largeTrayIconsItem = new TrayMenuItem("Larger taskbar icons and text", () => ToggleOption(GesturePreferences.LargeTrayIcons, _largeTrayIconsItem!))
            { Checked = GesturePreferences.LoadOption(GesturePreferences.LargeTrayIcons), Enabled = tapAvailable };
        _hideTopTaskbarItem = new TrayMenuItem("Hide a top taskbar in Mission Control", () => ToggleOption(GesturePreferences.HideTopTaskbarInMissionControl, _hideTopTaskbarItem!))
            { Checked = GesturePreferences.LoadOption(GesturePreferences.HideTopTaskbarInMissionControl) };

        var menu = new[]
        {
            _gestureTitle,
            TrayMenuItem.Separator,
            _enabledItem,
            new TrayMenuItem("Gesture fingers", null, _threeFingerItem, _fourFingerItem),
            _tapForStartItem,
            new TrayMenuItem("Spring", null, _snappyItem, _smoothItem),
            new TrayMenuItem("Preview animation", RunPreview),
            new TrayMenuItem("Input monitor...", ShowMonitor),
            new TrayMenuItem("Touchpad settings...", OpenTouchpadSettings),
            new TrayMenuItem("Taskbar", null, _hideStartItem, _hideEdgeItem, _hideIndicatorsItem, _largeTrayIconsItem, _hideTopTaskbarItem),
            TrayMenuItem.Separator,
            _startupItem,
            _adminItem,
            TrayMenuItem.Separator,
            new TrayMenuItem("Quit", MessageLoop.Quit),
        };

        using (var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            _iconHandle = TrayIconImage.Create(lightTaskbar: personalize?.GetValue("SystemUsesLightTheme") is 1);
        _tray = new TrayIcon(_iconHandle, _elevated ? "TrackSwipe (administrator)" : "TrackSwipe", menu);
        _tray.DoubleClick += ShowMonitor;
        _tray.BalloonClicked += () => _balloonAction?.Invoke();
        _tray.QuitRequested += MessageLoop.Quit;
        _tray.TaskbarRecreated += () => ScheduleTaskbarOptions(1500); // a new Explorer: the DLL went with the old one
        var hideTopTaskbar = _hideTopTaskbarItem.Checked;
        _compositionThread.Invoke(() => _overlay.HideTopTaskbarInMissionControl = hideTopTaskbar);
        ScheduleTaskbarOptions(0);

        Log.Write($"Started {AppVersion}{(_elevated ? " as administrator" : "")} on Windows {WindowsBuild.Describe()}: {options.Fingers} fingers, full swipe {options.FullSwipeMm} mm, spring {options.Spring}. Instant desktop switching {_overlay.SwitcherStatus}.");
        if (Setup.NativeGestures.Active(options.Fingers)) Setup.NativeGestures.Claim(options.Fingers);
        if (_tapForStart && Setup.NativeGestures.TapActive) Setup.NativeGestures.ClaimTap();
        if (welcome)
            Notify("TrackSwipe is running", $"Swipe {options.Fingers} fingers left or right to change desktops, and up for Mission Control. " +
                (adminDeclined ? "Administrator mode wasn't approved; you can turn it on from this icon's menu." : "Settings are in this icon's menu."));
        // Windows keeps a normal app's input away from apps running as administrator: say so the first time one is in front.
        if (!_elevated) _adminAppWatch = new ForegroundWatch(OnAdminAppInFront);
        // A taskbar click that jumps to another desktop slides there instead of cutting.
        _taskbarClicks = new TaskbarClickWatch(
            (point, interested) => _compositionThread.Post(() => interested(_overlay.TaskbarPressed(point))),
            (point, shown) => _compositionThread.Post(() => { try { _overlay.TaskbarReleased(point); } finally { shown(); } }),
            () => _compositionThread.Post(_overlay.TaskbarAbandoned));
        _compositionThread.Post(_overlay.WarmTaskbarButtons);
#if DIAGNOSTICS
        StartDiagnostics(preview, diagnose, missionDiagnostics);
#else
        if (preview) _compositionThread.Post(RunPreview);
#endif
    }

    /// <summary>Ends the main thread's message loop, from any thread.</summary>
    private void ExitOnShell() => _shellPump.Post(MessageLoop.Quit);

    /// <summary>Last line of defence: hide the overlay and reset so the user is never left stuck.</summary>
    public void HandleFault(Exception exception)
    {
        if (Thread.CurrentThread != _pump.UiThread)
        {
            _pump.Post(() => HandleFault(exception)); // recovery touches UI-thread state
            return;
        }
        Log.Write($"Error: {exception}");
        try
        {
            CancelGesture();
        }
        catch (Exception cleanup)
        {
            Log.Write($"Error during recovery: {cleanup}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shuttingDown = true;
        _taskbarClicks?.Dispose();
        _compositionThread.Dispose();
        _shellPump.Dispose();
        _monitor?.Dispose();
        _adminAppWatch?.Dispose();
        _taskbarTimer?.Dispose();
        if (_hideStartItem.Checked || _hideEdgeItem.Checked || _hideIndicatorsItem.Checked || _largeTrayIconsItem.Checked)
            Interop.TaskbarTap.Restore(); // the taskbar as Windows draws it
        _tray.Dispose();
        NativeMethods.DestroyIcon(_iconHandle);
    }

    private void ToggleOption(string name, TrayMenuItem item)
    {
        item.Checked = !item.Checked;
        GesturePreferences.SaveOption(name, item.Checked);
        Log.Write($"{item.Text} {(item.Checked ? "on" : "off")}.");
        if (name == GesturePreferences.HideTopTaskbarInMissionControl)
            _compositionThread.Invoke(() => _overlay.HideTopTaskbarInMissionControl = item.Checked);
        else ScheduleTaskbarOptions(0);
    }

    /// <summary>
    /// Brings the taskbar in line with the options: tells Explorer's copy of the DLL, loading it first if an option
    /// needs it. It starts listening a moment after loading, so this retries briefly. Nothing is loaded while all are off.
    /// </summary>
    private void ScheduleTaskbarOptions(int delayMs)
    {
        _taskbarAttempts = 0;
        _taskbarTimer ??= new UiTimer();
        _taskbarTimer.Stop();
        _taskbarTimer.Interval = Math.Max(1, delayMs);
        _taskbarTimer.Tick -= ApplyTaskbarOptions;
        _taskbarTimer.Tick += ApplyTaskbarOptions;
        _taskbarTimer.Start();
    }

    private void ApplyTaskbarOptions(object? sender, EventArgs e)
    {
        bool start = _hideStartItem.Checked, edge = _hideEdgeItem.Checked, indicators = _hideIndicatorsItem.Checked, icons = _largeTrayIconsItem.Checked;
        _taskbarTimer!.Interval = 250;
        if (Interop.TaskbarTap.Send(start, edge, icons, indicators) || (!start && !edge && !indicators && !icons)) { _taskbarTimer.Stop(); return; }
        if (_taskbarAttempts++ == 0 && !Interop.TaskbarTap.Load()) { _taskbarTimer.Stop(); return; }
        if (_taskbarAttempts > 40) { Log.Write("Taskbar options: Explorer didn't pick them up."); _taskbarTimer.Stop(); }
    }

    private void CleanupComposition()
    {
        _shuttingDown = true;
        _preview?.Dispose();
        _clock?.Dispose();
        _touchpad?.Dispose();
        _overlay?.Dispose();
        Composition.Dispose();
    }

    private void DrainInput()
    {
        if (_shuttingDown) return;
        _touchpad.Drain(_processFrame);
        if (_firstTrackingFrame && !_inDisplayFrame)
        {
            _firstTrackingFrame = false;
            OnDisplayFrame(Clock.Now); // first movement needn't wait for another compositor-clock tick
        }
    }

    private void ProcessFrame(TouchFrame frame)
    {
        if (_enabled)
        {
            var phase = _engine.Phase;
            _engine.Process(frame);
            if (_tapForStart) _tap.Process(frame);
            if (phase != GesturePhase.Tracking && _engine.Phase == GesturePhase.Tracking) _firstTrackingFrame = true;
            if (_engine.NeedsFrames) _clock.Start();
            _samplingInput = _engine.NeedsFrames;
        }
        UpdateInputFilter();
        if (_monitorOpen) PublishMonitorFrame(frame);
    }

    /// <summary>On the composition thread: whether the touchpad thread must deliver even one-finger reports.</summary>
    private void UpdateInputFilter() => _touchpad.ForwardAll = _monitorOpen || _engine.Phase != GesturePhase.Idle;

    // Keep the captured frame out of ProcessFrame: the compiler otherwise allocates its closure
    // on every input report, even when the diagnostic monitor is closed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PublishMonitorFrame(TouchFrame frame)
    {
        var status = new MonitorStatus(_engine.ActiveContacts, _engine.Phase, _engine.Axis, _engine.Progress, _engine.DisplayProgress, _engine.Velocity, _enabled);
        _shellPump.Post(() => _monitor?.Push(frame, status));
    }


    /// <param name="presentTime">When this frame will reach the screen; animations are evaluated for that moment.</param>
    private void OnDisplayFrame(double presentTime)
    {
        if (_shuttingDown) return;
        if (_inDisplayFrame) return;
        _inDisplayFrame = true;
        try
        {
            var now = Clock.Now;
            var estimated = false;
            if (Composition.Device.GetFrameStatistics(out var timing).Success && timing.TimeFrequency > 0 &&
                timing.CurrentCompositionRate.Numerator > 0 && timing.CurrentCompositionRate.Denominator > 0)
            {
                var period = timing.CurrentCompositionRate.Denominator / (double)timing.CurrentCompositionRate.Numerator;
                var estimate = timing.NextEstimatedFrameTime / (double)timing.TimeFrequency;
                if (estimate > now - period * 4 && estimate < now + period * 4)
                {
                    presentTime = PresentationTiming.Choose(now, estimate, period, _batchMargin);
                    estimated = true;
                }
            }
            if (!estimated && double.IsNaN(presentTime)) presentTime = FrameClock.PredictPresentTime();
            DrainInput(); // the freshest touches, right before they're drawn
            _firstTrackingFrame = false;
            _engine.Tick(presentTime);
            UpdateInputFilter(); // a settle finishing returns the engine to idle
            _overlay.RenderFrame(presentTime);
            Composition.Flush(); // the whole frame reaches DWM as one batch
            var finished = Clock.Now;
            _batchMargin = Math.Clamp(_batchMargin * 0.9 + (finished - now + 0.0003) * 0.1, 0.0005, 0.003);
            _framePerf.Frame(now, finished);
            _samplingInput = _engine.NeedsFrames;
            var warmingUp = _engine.Phase == GesturePhase.Candidate && now - _candidateSince < CandidateWarmUp;
            if (_engine.NeedsFrames || _overlay.NeedsFrames || warmingUp) return;
            _clock.Stop();
            _touchpad.WakeIfPending();
            _framePerf.Flush();
        }
        finally { _inDisplayFrame = false; }
    }

    private void OnCommitted(SwipeAction action)
    {
        if (_previewing) return;
        if (!_overlay.Committed(action)) KeySender.Send(action);
    }

    private void SetEnabled(bool enabled)
    {
        _enabled = _enabledItem.Checked = enabled;
        if (!enabled) _compositionThread.Post(CancelGesture);
    }

    private void CancelGesture()
    {
        _preview?.Dispose();
        _preview = null;
        _previewing = false;
        _clock.Stop();
        _engine.Reset();
        UpdateInputFilter();
        _samplingInput = false;
        _firstTrackingFrame = false;
        _touchpad.WakeIfPending();
        _overlay.Cancel();
    }
    private void SetSpring(SpringPreset preset)
    {
        _compositionThread.Invoke(() => _engine.Options.Spring = preset);
        _snappyItem.Checked = preset == SpringPreset.Snappy;
        _smoothItem.Checked = preset == SpringPreset.Smooth;
    }

    private void ShowMonitor()
    {
        if (_monitor is null)
        {
            _monitor = new InputMonitorForm(_device);
            _monitor.FormClosed += () => { _monitor = null; _monitorOpen = false; };
        }
        _monitor.Show();
        _monitorOpen = true;
        _touchpad.ForwardAll = true; // the monitor shows every contact; closing it lets the next report re-filter
        _monitor.Activate();
    }

    private void SetFingers(int fingers)
    {
        if (_compositionThread.Invoke(() => _engine.Options.Fingers) == fingers) return;
        _compositionThread.Invoke(() => { CancelGesture(); _engine.Options.Fingers = fingers; _touchpad.GestureFingers = ReportedFingers(fingers); });
        _threeFingerItem.Checked = fingers == 3;
        _fourFingerItem.Checked = fingers == 4;
        _gestureTitle.Text = Title(fingers);
        GesturePreferences.SaveFingers(fingers);
        Log.Write($"Gesture fingers changed to {fingers}.");
        Setup.NativeGestures.Claim(fingers); // and gives the other count back to Windows
    }

    /// <summary>
    /// The fewest fingers the touchpad reader passes on: the swipe's count, or three for the tap if it's on and swipes
    /// use four. Fewer (pointing, scrolling) never wake this thread.
    /// </summary>
    private int ReportedFingers(int swipeFingers) => _tapForStart ? Math.Min(swipeFingers, _tap.Fingers) : swipeFingers;

    private void ToggleTapForStart()
    {
        _tapForStart = !_tapForStart;
        _tapForStartItem.Checked = _tapForStart;
        GesturePreferences.SaveOption(GesturePreferences.ThreeFingerTapForStart, _tapForStart);
        _compositionThread.Invoke(() => _touchpad.GestureFingers = ReportedFingers(_engine.Options.Fingers));
        Log.Write($"Three-finger tap opens Start {(_tapForStart ? "on" : "off")}.");
        // Windows' own three-finger tap (Search, by default) would fire alongside it.
        if (_tapForStart) Setup.NativeGestures.ClaimTap();
        else Setup.NativeGestures.RestoreTap();
    }

    /// <summary>Plays a synthetic swipe out to 60% and back through the real engine and overlay. Never switches desktops.</summary>
    private void RunPreview()
    {
        if (Thread.CurrentThread != _pump.UiThread) { _compositionThread.Post(RunPreview); return; }
        if (_previewing || !_enabled || _engine.Phase != GesturePhase.Idle) return;
        _previewing = true;

        const double duration = 1.6;
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeMm * 0.6;
        _preview = new UiTimer { Interval = 8 };
        _preview.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            var lifted = t >= duration;
            var travel = -Math.Sin(Math.Min(t / duration, 1) * Math.PI) * reach;
            var contacts = Enumerable.Range(0, fingers)
                .Select(i => new Contact(100 + i, 30 + i * 12 + travel, 40, Tip: !lifted, Confidence: true))
                .ToArray();
            _touchpad.Inject(new TouchFrame(Clock.Now, contacts, 110, 70));
            if (!lifted) return;

            _preview?.Dispose();
            _preview = null;
        };
        _preview.Start();
    }


    private string Title(int fingers) => $"TrackSwipe {AppVersion}: {fingers}-finger swipes{(_elevated ? " (administrator)" : "")}";

    private void Notify(string title, string text, Action? onClick = null)
    {
        _balloonAction = onClick;
        _tray.ShowBalloon(title, text);
    }

    private void ToggleStartup()
    {
        if (Setup.Startup.Enabled) Setup.Startup.Disable();
        else Setup.Startup.Enable(Environment.ProcessPath!);
        _startupItem.Checked = Setup.Startup.Enabled;
    }

    /// <summary>Quits so <c>Program</c> can start TrackSwipe again with or without administrator rights.</summary>
    private void RequestRestart(RestartMode mode)
    {
        Restart = mode;
        MessageLoop.Quit();
    }

    private void OnAdminAppInFront(string app)
    {
        _adminAppWatch?.Dispose(); // once per session is enough
        _adminAppWatch = null;
        Log.Write($"{app} is in front and runs as administrator; offered administrator mode.");
        Notify($"Gestures can't reach {app}", $"{app} runs as administrator, and Windows keeps touchpad input from reaching normal apps while it's in front. " +
            "Click to run TrackSwipe as administrator too.", () => RequestRestart(RestartMode.Elevated));
    }
    private static void OpenTouchpadSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true });
}

internal enum RestartMode { None, Elevated, Normal }

/// <summary>
/// Calls back (on the thread that created it) when a window of a process running as administrator comes to the
/// front: an out-of-context foreground event hook, which costs nothing between foreground changes.
/// </summary>
internal sealed class ForegroundWatch : IDisposable
{
    [ThreadStatic] private static ForegroundWatch? _current;
    private readonly Action<string> _onElevatedApp;
    private IntPtr _hook;

    public unsafe ForegroundWatch(Action<string> onElevatedApp)
    {
        _onElevatedApp = onElevatedApp;
        _current = this;
        _hook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, &OnEvent, 0, 0, 0x0000 /* OUTOFCONTEXT */ | 0x0002 /* SKIPOWNPROCESS */);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        if (_current == this) _current = null;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void OnEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (_current is not { } watch || window == IntPtr.Zero) return;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0 || !IsElevated(pid)) return;
            using var process = Process.GetProcessById((int)pid);
            watch._onElevatedApp(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // it exited meanwhile
    }

    private static bool IsElevated(uint pid)
    {
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern unsafe IntPtr SetWinEventHook(uint min, uint max, IntPtr module, delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
}
