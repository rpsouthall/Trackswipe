using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using TrackSwipe.Core;
using TrackSwipe.Interop;
using TrackSwipe.Overlay;
using TrackSwipe.Touchpad;

namespace TrackSwipe;

/// <summary>Wires touchpad input, the gesture engine, the overlay and the tray menu together. Everything runs on the UI thread.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly GestureEngine _engine;
    private readonly RawInputTouchpad _touchpad;
    private readonly OverlayController _overlay;
    private readonly FrameClock _clock;
    private readonly NotifyIcon _tray;
    private readonly IntPtr _iconHandle;
    private readonly ToolStripMenuItem _enabledItem, _snappyItem, _smoothItem;
    private string _device = "No precision touchpad input seen yet";
    private InputMonitorForm? _monitor;
    private System.Windows.Forms.Timer? _preview;
    private bool _enabled = true;
    private bool _previewing;
    private readonly FramePerf _framePerf = new();

    public TrayApp(GestureOptions options, bool preview, bool diagnose, int? missionDiagnostics)
    {
        _engine = new GestureEngine(options);
        _overlay = new OverlayController(_engine);
        _engine.TrackingStarted += _overlay.TrackingStarted;
        _engine.Committed += OnCommitted;
        _engine.Ended += () =>
        {
            _overlay.Ended();
            _previewing = false;
        };

        _clock = new FrameClock(new WindowsFormsSynchronizationContext(), OnDisplayFrame);
        _overlay.FramesNeeded += _clock.Start;

        _touchpad = new RawInputTouchpad();
        _touchpad.FrameReceived += OnFrame;
        _touchpad.DeviceFound += device =>
        {
            _device = $"Touchpad: {device}";
            Log.Write(_device);
        };

        _enabledItem = new ToolStripMenuItem("Gestures enabled", null, (_, _) => SetEnabled(!_enabled)) { Checked = true };
        _snappyItem = new ToolStripMenuItem("Snappy", null, (_, _) => SetSpring(SpringPreset.Snappy));
        _smoothItem = new ToolStripMenuItem("Smooth", null, (_, _) => SetSpring(SpringPreset.Smooth));
        SetSpring(options.Spring);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"TrackSwipe: {options.Fingers}-finger swipes") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
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
            Text = "TrackSwipe",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseDoubleClick += (_, _) => ShowMonitor();
        _tray.BalloonTipClicked += (_, _) => OpenTouchpadSettings();

        Log.Write($"Started: {options.Fingers} fingers, full swipe {options.FullSwipeMm} mm, spring {options.Spring}. Instant desktop switching {_overlay.SwitcherStatus}.");
        WarnIfWindowsGestureEnabled(options.Fingers);
        if (preview) RunPreview();
        if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_OPEN") == "1") RunOpenDiagnostics();
        if (diagnose) _overlay.RunDiagnostics(ExitThread);
        if (missionDiagnostics is { } switchDirection) _overlay.RunMissionDiagnostics(ExitThread, switchDirection);
        if (long.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_FOCUS"), out var focusWindow))
            _overlay.RunFocusDiagnostics(new IntPtr(focusWindow), ExitThread, Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SWIPE") == "1");
    }

    /// <summary>Last line of defence: hide the overlay and reset so the user is never left stuck.</summary>
    public void HandleFault(Exception exception)
    {
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
            _overlay.Dispose();
            _monitor?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            NativeMethods.DestroyIcon(_iconHandle);
        }
        base.Dispose(disposing);
    }

    private void OnFrame(TouchFrame frame)
    {
        if (_enabled)
        {
            _engine.Process(frame);
            if (_engine.NeedsFrames) _clock.Start();
        }
        _monitor?.Push(frame, _engine, _enabled);
    }

    private void OnDisplayFrame()
    {
        var now = Clock.Now;
        _engine.Tick(now);
        _overlay.RenderFrame(now);
        _framePerf.Frame(now, Clock.Now);
        if (_engine.NeedsFrames || _overlay.NeedsFrames) return;
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
            OnFrame(new TouchFrame(Clock.Now, contacts, 110, 70));
            if (!lifted) return;

            _preview?.Dispose();
            _preview = null;
        };
        _preview.Start();
    }

    /// <summary>
    /// For --diagnose-open: a synthetic four-finger swipe up through the real engine and display clock
    /// (so frame pacing is as in use), then dismisses Mission Control and quits. Frame stats go to the log.
    /// </summary>
    private void RunOpenDiagnostics()
    {
        const double swipe = 0.3;
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeVerticalMm * 0.9;
        var dismissed = false;
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            if (t <= swipe + 0.02)
            {
                var lifted = t > swipe;
                var travel = -reach * Math.Min(t / swipe, 1); // fingers moving up
                var contacts = Enumerable.Range(0, fingers)
                    .Select(i => new Contact(200 + i, 30 + i * 12, 60 + travel, Tip: !lifted, Confidence: true))
                    .ToArray();
                OnFrame(new TouchFrame(Clock.Now, contacts, 110, 70));
                return;
            }
            if (!dismissed && t > 1.5)
            {
                dismissed = true;
                _overlay.DismissMissionControl();
            }
            if (dismissed && t > 2.5)
            {
                timer.Dispose();
                ExitThread();
            }
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
