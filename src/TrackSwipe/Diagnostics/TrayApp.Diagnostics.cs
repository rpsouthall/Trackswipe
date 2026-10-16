#if DIAGNOSTICS
using System.Diagnostics;
using TrackSwipe.Core;
using TrackSwipe.Touchpad;

namespace TrackSwipe;

internal sealed partial class TrayApp
{
    /// <summary>Starts the self-test chosen on the command line (Debug or -p:Diagnostics=true builds only).</summary>
    private void StartDiagnostics(bool preview, bool diagnose, int? missionDiagnostics)
    {
        _compositionThread.Post(() =>
        {
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_INPUT") == "1")
            {
                RunInputDiagnostics();
                return;
            }
            if (preview) RunPreview();
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_OPEN") is { } open)
                RunOpenDiagnostics(int.TryParse(open, out var cycles) && cycles > 2 ? Math.Min(cycles, 100) : 2);
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_PEEK") is { } peek)
                RunPeekDiagnostics(peek);
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE_CATCH") == "1")
                RunSlideCatchDiagnostics();
            if (int.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE"), out var slideDirection) && slideDirection is 1 or -1)
                RunSlideDiagnostics(slideDirection);
            if (diagnose) _overlay.RunDiagnostics(ExitOnShell);
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DESKTOPS") == "1")
                _overlay.RunDesktopBarDiagnostics(ExitOnShell);
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DRAG_DEMO")?.Split(':') is [var demoWindow, var demoDesktop])
                _overlay.RunWindowDragDemo(new IntPtr(long.Parse(demoWindow)), int.Parse(demoDesktop), ExitOnShell);
            if (long.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_TASKBAR_JUMP"), out var jumpWindow))
                _overlay.RunTaskbarJumpDiagnostics(new IntPtr(jumpWindow), ExitOnShell);
            if (long.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_WINDOW_DRAG"), out var draggedWindow))
                _overlay.RunWindowDragDiagnostics(new IntPtr(draggedWindow), ExitOnShell);
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_BAR_HOVER") == "1")
                _overlay.RunBarRevealDiagnostics(ExitOnShell);
            if (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLIDE_ROUNDTRIP") == "1")
            {
                var desktops = VirtualDesktops.Read();
                RunSlideDiagnostics(desktops is not null && desktops.Index < desktops.Count - 1 ? 1 : -1, roundTrip: true);
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_DWELL"), out var dwellDesktop))
                _overlay.RunDwellDiagnostics(dwellDesktop, ExitOnShell);
            if (int.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_ZOOM"), out var zoomDesktop))
                _overlay.RunZoomDiagnostics(zoomDesktop, ExitOnShell);
            if (missionDiagnostics is { } switchDirection) _overlay.RunMissionDiagnostics(ExitOnShell, switchDirection);
            if (long.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_FOCUS"), out var focusWindow))
                _overlay.RunFocusDiagnostics(new IntPtr(focusWindow), ExitOnShell, Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SWIPE") == "1");
        });
    }

    private void RunInputDiagnostics()
    {
        var queue = new TouchFrameQueue();
        var contacts = new Contact[_engine.Options.Fingers];
        for (var i = 0; i < contacts.Length; i++) contacts[i] = new Contact(i, 30 + i * 12, 40, true, true);
        var start = Clock.Now;
        void Report(int i)
        {
            queue.Enqueue(TouchFrame.FromContacts(start + i / 125.0, contacts.AsSpan(), 110, 70));
            while (queue.TryDequeue(out var frame)) ProcessFrame(frame);
        }
        for (var i = 0; i < 10000; i++) Report(i);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 10000; i < 110000; i++) Report(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        CancelGesture();
        Log.Write($"Diagnose input: 100000 normal reports through snapshot, queue, engine and app dispatch; allocated {allocated} bytes ({allocated / 100000.0:0.00} bytes/report), monitor closed.");
        if (allocated != 0) throw new InvalidOperationException("Normal input dispatch allocated after warmup.");
        ExitOnShell();
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
        var reach = _engine.Options.FullSwipeVerticalMm *
            (Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_OVERSCROLL") == "1" ? 1.35 : 0.9);
        var dismissed = new bool[cycles];
        var timer = new UiTimer { Interval = 8 };
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
                ExitOnShell();
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
    private void RunSlideDiagnostics(int direction, bool roundTrip = false)
    {
        const double swipe = 0.25;
        const double secondSwipe = 1.75;
        var initialDesktops = VirtualDesktops.Read();
        var original = initialDesktops?.Ids.ElementAtOrDefault(initialDesktops.Index);
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeMm * 0.7;
        var timer = new UiTimer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            if (t > (roundTrip ? secondSwipe * 2 : swipe + 1.5))
            {
                if (roundTrip)
                {
                    var desktops = VirtualDesktops.Read();
                    var current = desktops?.Ids.ElementAtOrDefault(desktops.Index);
                    Log.Write($"Desktop slide round trip: original desktop restored {current == original}.");
                }
                timer.Dispose();
                ExitOnShell();
                return;
            }
            var returning = roundTrip && t >= secondSwipe;
            var sampleTime = returning ? t - secondSwipe : t;
            if (sampleTime > swipe + 0.02) return;
            var lifted = sampleTime > swipe;
            var travel = -direction * (returning ? -1 : 1) * reach * Math.Min(sampleTime / swipe, 1);
            var contacts = Enumerable.Range(0, fingers)
                .Select(i => new Contact((returning ? 400 : 300) + i, 80 + i * 12 + travel, 40, Tip: !lifted, Confidence: true))
                .ToArray();
            _touchpad.Inject(new TouchFrame(Clock.Now, contacts, 110, 70));
        };
        timer.Start();
    }

    /// <summary>
    /// For --diagnose-slide-catch: a quick flick to the next desktop, lifted, then caught 60 ms later (while it's still
    /// settling) by a second flick the same way. That should go on to the desktop after: two desktops along in all.
    /// </summary>
    private void RunSlideCatchDiagnostics()
    {
        const double flick = 0.12, gap = 0.06;
        var initial = VirtualDesktops.Read();
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var reach = _engine.Options.FullSwipeMm * 0.55;
        var timer = new UiTimer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            if (t > 2 * flick + gap + 1.5)
            {
                var now = VirtualDesktops.Read();
                Log.Write($"Slide catch diagnostic: from desktop {initial?.Index + 1} to {now?.Index + 1} ({(now?.Index - initial?.Index)} along).");
                timer.Dispose();
                ExitOnShell();
                return;
            }
            var second = t >= flick + gap;
            var sample = second ? t - flick - gap : t;
            if (sample > flick + 0.02 || (!second && t > flick + 0.02)) return;
            var lifted = sample > flick;
            var travel = -reach * Math.Min(sample / flick, 1);
            var contacts = Enumerable.Range(0, fingers)
                .Select(i => new Contact((second ? 600 : 500) + i, 80 + i * 12 + travel, 40, Tip: !lifted, Confidence: true))
                .ToArray();
            _touchpad.Inject(new TouchFrame(Clock.Now, contacts, 110, 70));
        };
        timer.Start();
    }

    /// <summary>
    /// For --diagnose-peek [reverse|hold]: a synthetic swipe down through the real engine and display clock. Plain
    /// ("commit"), it shows the desktop (really minimizing the windows). With reverse, the fingers go 60% of the way,
    /// come back to 10% and lift, which must cancel with every window back where it was. With hold, they move just
    /// past the dead zone, rest and lift: barely any motion, so the overlay's appearance and removal can be checked
    /// for flashes on their own. Quits after.
    /// </summary>
    private void RunPeekDiagnostics(string mode)
    {
        var start = Clock.Now;
        var fingers = _engine.Options.Fingers;
        var full = _engine.Options.FullSwipeVerticalMm;
        // Fingers' travel down over time.
        double TravelAt(double t) => mode switch
        {
            "reverse" => t < 0.45 ? full * 0.6 * Math.Min(1, t / 0.45) : full * (0.6 - 0.5 * Math.Min(1, (t - 0.45) / 0.45)),
            "hold" => 2.5 * Math.Min(1, t / 0.1),
            _ => full * 0.9 * Math.Min(1, t / 0.3),
        };
        var lift = mode switch { "reverse" => 1.0, "hold" => 0.8, _ => 0.32 };
        var reverse = mode != "commit";
        var timer = new UiTimer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            if (t > lift + 2.0)
            {
                Log.Write($"Peek diagnostic ({mode}): done.");
                timer.Dispose();
                ExitOnShell();
                return;
            }
            if (t > lift + 0.02) return;
            var lifted = t > lift;
            var contacts = Enumerable.Range(0, fingers)
                .Select(i => new Contact(500 + i, 30 + i * 12, 15 + TravelAt(Math.Min(t, lift)), Tip: !lifted, Confidence: true))
                .ToArray();
            _touchpad.Inject(new TouchFrame(Clock.Now, contacts, 110, 70));
        };
        timer.Start();
    }
}
#endif
