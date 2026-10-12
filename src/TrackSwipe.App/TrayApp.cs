using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using TrackSwipe.Core;
using TrackSwipe.Interop;
using TrackSwipe.Overlay;
using TrackSwipe.Touchpad;

namespace TrackSwipe;

/// <summary>
/// Wires touchpad input, the gesture engine, the overlay and the tray menu together. The touchpad is read
/// on its own thread and display frames are paced by another; all gesture and overlay logic runs on the UI thread.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly GestureEngine _engine;
    private readonly TouchpadThread _touchpad;
    private readonly UiPump _pump;
    private readonly Action<TouchFrame> _processFrame;
    private readonly OverlayController _overlay;
    private readonly FrameClock _clock;
    private readonly NotifyIcon _tray;
    private readonly IntPtr _iconHandle;
    private readonly ToolStripMenuItem _enabledItem, _snappyItem, _smoothItem;
    private readonly ToolStripMenuItem _gestureTitle, _threeFingerItem, _fourFingerItem;
    private volatile string _device = "No precision touchpad input seen yet";
    private InputMonitorForm? _monitor;
    private System.Windows.Forms.Timer? _preview;
    private bool _enabled = true;
    private bool _previewing;
    private readonly FramePerf _framePerf = new();
    private double _candidateSince = double.NegativeInfinity;
    private const double CandidateWarmUp = 0.5; // how long resting fingers keep the clock (and boost) running
    private static readonly string AppVersion = typeof(TrayApp).Assembly.GetName().Version?.ToString(2) ?? "0.2";

    public TrayApp(GestureOptions options, bool preview, bool diagnose, int? missionDiagnostics)
    {
        _engine = new GestureEngine(options);
        Composition.Initialize();
        _overlay = new OverlayController(_engine);
        _engine.TrackingStarted += _overlay.TrackingStarted;
        _engine.SettlingStarted += _overlay.SettlingStarted;
        _engine.Committed += OnCommitted;
        _engine.Ended += () =>
        {
            _overlay.Ended();
            _previewing = false;
        };

        _pump = new UiPump(OnDisplayFrame);
        _clock = new FrameClock(_pump);
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
        _touchpad = new TouchpadThread();
        _touchpad.FramesWaiting += () => _pump.Post(drainInput);
        _touchpad.DeviceFound += device =>
        {
            _device = $"Touchpad: {device}";
            Log.Write(_device);
        };

        _enabledItem = new ToolStripMenuItem("Gestures enabled", null, (_, _) => SetEnabled(!_enabled)) { Checked = true };
        _snappyItem = new ToolStripMenuItem("Snappy", null, (_, _) => SetSpring(SpringPreset.Snappy));
        _smoothItem = new ToolStripMenuItem("Smooth", null, (_, _) => SetSpring(SpringPreset.Smooth));
        SetSpring(options.Spring);
        _gestureTitle = new ToolStripMenuItem($"TrackSwipe GPU {AppVersion}: {options.Fingers}-finger swipes") { Enabled = false };
        _threeFingerItem = new ToolStripMenuItem("Three fingers", null, (_, _) => SetFingers(3)) { Checked = options.Fingers == 3 };
        _fourFingerItem = new ToolStripMenuItem("Four fingers", null, (_, _) => SetFingers(4)) { Checked = options.Fingers == 4 };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_gestureTitle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripMenuItem("Gesture fingers", null, _threeFingerItem, _fourFingerItem));
        menu.Items.Add(new ToolStripMenuItem("Spring", null, _snappyItem, _smoothItem));
        menu.Items.Add("Preview animation", null, (_, _) => RunPreview());
        menu.Items.Add("Input monitor...", null, (_, _) => ShowMonitor());
        menu.Items.Add("Touchpad settings...", null, (_, _) => OpenTouchpadSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _iconHandle = CreateTrayIconHandle();
        _tray = new NotifyIcon
        {
            Icon = Icon.FromHandle(_iconHandle),
            Text = "TrackSwipe (GPU)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseDoubleClick += (_, _) => ShowMonitor();
        _tray.BalloonTipClicked += (_, _) => OpenTouchpadSettings();

        Log.Write($"Started: {options.Fingers} fingers, full swipe {options.FullSwipeMm} mm, spring {options.Spring}. Instant desktop switching {_overlay.SwitcherStatus}.");
        WarnIfWindowsGestureEnabled(options.Fingers);
        WarmUpCode();
        if (preview) RunPreview();
        if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_OPEN") is { } open)
            RunOpenDiagnostics(int.TryParse(open, out var cycles) && cycles > 2 ? Math.Min(cycles, 100) : 2);
        if (int.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE"), out var slideDirection) && slideDirection is 1 or -1)
            RunSlideDiagnostics(slideDirection);
        if (diagnose) _overlay.RunDiagnostics(ExitThread);
        if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DESKTOPS") == "1")
            _overlay.RunDesktopBarDiagnostics(ExitThread);
        if (missionDiagnostics is { } switchDirection) _overlay.RunMissionDiagnostics(ExitThread, switchDirection);
        if (long.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_FOCUS"), out var focusWindow))
            _overlay.RunFocusDiagnostics(new IntPtr(focusWindow), ExitThread, Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SWIPE") == "1");
    }

    /// <summary>
    /// Compiles TrackSwipe's own code at the lowest priority right after startup, so the first gesture
    /// doesn't stall while .NET compiles it on first use. A ReadyToRun publish makes this a no-op.
    /// </summary>
    private static void WarmUpCode()
    {
        var thread = new Thread(() =>
        {
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in new[] { typeof(TrayApp).Assembly, typeof(GestureEngine).Assembly }.SelectMany(assembly => assembly.GetTypes()))
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;
                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                    {
                    }
                }
            }
        })
        { IsBackground = true, Name = "TrackSwipe warm-up", Priority = ThreadPriority.Lowest };
        thread.Start();
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.Dispose();
            _clock.Dispose();
            _touchpad.Dispose();
            _pump.Dispose();
            _overlay.Dispose();
            Composition.Dispose();
            _monitor?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            NativeMethods.DestroyIcon(_iconHandle);
        }
        base.Dispose(disposing);
    }

    private void DrainInput() => _touchpad.Drain(_processFrame);

    private void ProcessFrame(TouchFrame frame)
    {
        if (_enabled)
        {
            _engine.Process(frame);
            if (_engine.NeedsFrames) _clock.Start();
        }
        _monitor?.Push(frame, _engine, _enabled);
    }

    /// <param name="presentTime">When this frame will reach the screen; animations are evaluated for that moment.</param>
    private void OnDisplayFrame(double presentTime)
    {
        var now = Clock.Now;
        DrainInput(); // the freshest touches, right before they're drawn
        _engine.Tick(presentTime);
        _overlay.RenderFrame(presentTime);
        Composition.Flush(); // the whole frame reaches DWM as one batch
        _framePerf.Frame(now, Clock.Now);
        var warmingUp = _engine.Phase == GesturePhase.Candidate && now - _candidateSince < CandidateWarmUp;
        if (_engine.NeedsFrames || _overlay.NeedsFrames || warmingUp) return;
        _clock.Stop();
        _framePerf.Flush();
    }

    private void OnCommitted(SwipeAction action)
    {
        if (_previewing) return;
        if (!_overlay.Committed(action)) KeySender.Send(action);
    }

    private void SetEnabled(bool enabled)
    {
        _enabled = _enabledItem.Checked = enabled;
        if (!enabled) CancelGesture();
    }

    private void CancelGesture()
    {
        _preview?.Dispose();
        _preview = null;
        _previewing = false;
        _clock.Stop();
        _engine.Reset();
        _overlay.Cancel();
    }
    private void SetSpring(SpringPreset preset)
    {
        _engine.Options.Spring = preset;
        _snappyItem.Checked = preset == SpringPreset.Snappy;
        _smoothItem.Checked = preset == SpringPreset.Smooth;
    }

    private void ShowMonitor()
    {
        if (_monitor is null)
        {
            _monitor = new InputMonitorForm(_device);
            _monitor.FormClosed += (_, _) => _monitor = null;
        }
        _monitor.Show();
        _monitor.Activate();
    }

    private void SetFingers(int fingers)
    {
        if (_engine.Options.Fingers == fingers) return;
        CancelGesture();
        _engine.Options.Fingers = fingers;
        _threeFingerItem.Checked = fingers == 3;
        _fourFingerItem.Checked = fingers == 4;
        _gestureTitle.Text = $"TrackSwipe GPU {AppVersion}: {fingers}-finger swipes";
        GesturePreferences.SaveFingers(fingers);
        Log.Write($"Gesture fingers changed to {fingers}.");
        WarnIfWindowsGestureEnabled(fingers);
    }

    /// <summary>Plays a synthetic swipe out to 60% and back through the real engine and overlay. Never switches desktops.</summary>
    private void RunPreview()
    {
        if (_previewing || !_enabled || _engine.Phase != GesturePhase.Idle) return;
        _previewing = true;

        const double duration = 1.6;
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeMm * 0.6;
        _preview = new System.Windows.Forms.Timer { Interval = 8 };
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

    /// <summary>
    /// For --diagnose-open [cycles]: a synthetic four-finger swipe up through the real engine and display clock
    /// (so frame pacing is as in use), then dismisses Mission Control; repeated, then quits. Frame stats go to the log.
    /// </summary>
    private void RunOpenDiagnostics(int cycles)
    {
        // At least two cycles: the first in a fresh process includes .NET compiling code on first use; the
        // second is what a long-running TrackSwipe sees. More show whether anything accumulates.
        const double swipe = 0.3, cycle = 3.0;
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeVerticalMm * 0.9;
        var dismissed = new bool[cycles];
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var elapsed = Clock.Now - start;
            var round = Math.Min(cycles - 1, (int)(elapsed / cycle));
            var t = elapsed - round * cycle;
            if (elapsed > cycles * cycle - 0.4)
            {
                using var process = Process.GetCurrentProcess();
                Log.Write($"Diagnose open: {cycles} cycles; private memory {process.PrivateMemorySize64 / 1048576.0:0.0} MB, {process.HandleCount} handles, GDI objects {GetGuiResources(process.Handle, 0)}");
                timer.Dispose();
                ExitThread();
                return;
            }
            if (t <= swipe + 0.02)
            {
                var lifted = t > swipe;
                var travel = -reach * Math.Min(t / swipe, 1); // fingers moving up
                var contacts = Enumerable.Range(0, fingers)
                    .Select(i => new Contact(200 + 10 * round + i, 30 + i * 12, 60 + travel, Tip: !lifted, Confidence: true))
                    .ToArray();
                _touchpad.Inject(new TouchFrame(Clock.Now, contacts, 110, 70));
                return;
            }
            if (!dismissed[round] && t > 1.5)
            {
                dismissed[round] = true;
                _overlay.DismissMissionControl();
            }
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    /// <summary>
    /// For --diagnose-slide ±1: a synthetic swipe sideways through the real engine and display clock that
    /// commits, really switching desktops (+1 the next one, -1 the previous one), then quits.
    /// </summary>
    private void RunSlideDiagnostics(int direction)
    {
        const double swipe = 0.25;
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeMm * 0.7;
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            if (t > swipe + 1.5)
            {
                timer.Dispose();
                ExitThread();
                return;
            }
            if (t > swipe + 0.02) return;
            var lifted = t > swipe;
            var travel = -direction * reach * Math.Min(t / swipe, 1); // fingers moving left show the next desktop
            var contacts = Enumerable.Range(0, fingers)
                .Select(i => new Contact(300 + i, 80 + i * 12 + travel, 40, Tip: !lifted, Confidence: true))
                .ToArray();
            _touchpad.Inject(new TouchFrame(Clock.Now, contacts, 110, 70));
        };
        timer.Start();
    }

    private void WarnIfWindowsGestureEnabled(int fingers)
    {
        var valueName = fingers switch { 3 => "ThreeFingerSlideEnabled", 4 => "FourFingerSlideEnabled", _ => null };
        if (valueName is null) return;

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
        if (key?.GetValue(valueName) is int value && value != 0)
        {
            var count = fingers == 3 ? "three" : "four";
            _tray.ShowBalloonTip(8000, "Turn off Windows' own swipe",
                $"Windows is still handling {count}-finger swipes, so both will fire. Click to open touchpad settings and set {count}-finger swipes to \"Nothing\".",
                ToolTipIcon.Info);
        }
    }

    private static void OpenTouchpadSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true });

    private static IntPtr CreateTrayIconHandle()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var lightTaskbar = personalize?.GetValue("SystemUsesLightTheme") is 1;
        var size = SystemInformation.SmallIconSize.Width;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(lightTaskbar ? Color.FromArgb(28, 28, 28) : Color.White, Math.Max(1.5f, size / 12f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            // Touchpad outline with a double-headed arrow.
            var inset = size * 0.1f;
            g.DrawRectangle(pen, inset, size * 0.2f, size - 2 * inset, size * 0.6f);
            float y = size / 2f, left = size * 0.28f, right = size * 0.72f, head = size * 0.12f;
            g.DrawLine(pen, left, y, right, y);
            g.DrawLines(pen, new[] { new PointF(left + head, y - head), new PointF(left, y), new PointF(left + head, y + head) });
            g.DrawLines(pen, new[] { new PointF(right - head, y - head), new PointF(right, y), new PointF(right - head, y + head) });
        }
        return bitmap.GetHicon();
    }
}
