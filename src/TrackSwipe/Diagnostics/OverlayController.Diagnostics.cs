using TrackSwipe.Core;
using TrackSwipe.Desktops;
using System.Diagnostics;

namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    /// <summary>For --diagnose: logs what gestures would see, then holds the slide and Mission Control at fixed points.</summary>
    public void RunDiagnostics(Action done)
    {
        var scene = CaptureScene();
        var surfaces = _slideSurfaces.Prepare();
        _slide.Begin(scene, surfaces);
        Log.Write($"Diagnose: {scene.DesktopCount} desktops, current index {scene.Current}; " +
            string.Join(", ", Enumerable.Range(0, scene.DesktopCount).Select(d => $"desktop {d + 1}: {scene.WindowsOn(d).Count(w => w.IsAppWindow)} app windows")) +
            $"; {surfaces.Sum(surface => surface.SpriteCount)} slide thumbnails; {scene.Stats}");
        _slide.Render(0);
        foreach (var surface in surfaces) surface.Show();

        var stages = new (double Until, Action Show)[]
        {
            (1.5, () => _slide.Render(0)),
            (3.0, () => _slide.Render(0.5)),
            (4.5, () => _slide.Render(1)),
            (4.6, () => { HideSlide(); BeginMission(CaptureScene()); }),
            (6.0, () => _mission.Render(0.5)),
            (7.5, () =>
            {
                if (_mission.IsOpen) return;
                _mission.SetOpen();
                Log.Write($"Diagnose Mission Control: {_mission.Describe()}");
            }),
            (9.0, () => _mission.Pan(_mission.CanPan(+1) ? 0.5 : _mission.CanPan(-1) ? -0.5 : 0)),
        };
        // Allow visual inspection without racing the default 1.5-second diagnostic stages.
        var diagnosticSlowdown = double.TryParse(Environment.GetEnvironmentVariable("TRACKSWIPE_DIAGNOSE_SLOWDOWN"),
            System.Globalization.CultureInfo.InvariantCulture, out var slowdown) ? Math.Clamp(slowdown, 1, 20) : 1;
        var start = Clock.Now;
        var timer = new UiTimer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var t = (Clock.Now - start) / diagnosticSlowdown;
            var stage = stages.FirstOrDefault(s => t < s.Until);
            if (stage.Show is not null)
            {
                stage.Show();
                return;
            }
            timer.Dispose();
            _mission.Hide();
            done();
        };
        timer.Start();
    }

    /// <summary>
    /// For --diagnose-mission: opens Mission Control, pans to a neighbouring desktop and back the way a
    /// swipe would, then closes. Ends on the starting desktop, so nothing really switches. With
    /// <paramref name="switchDirection"/> set, pans that way once and closes there, really switching.
    /// </summary>
    public void RunMissionDiagnostics(Action done, int switchDirection = 0)
    {
        BeginMission(CaptureScene());
        var direction = 0;
        var step = 0;
        var start = Clock.Now;
        var timer = new UiTimer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0:
                    _mission.SetOpen();
                    direction = switchDirection != 0 ? switchDirection : _mission.CanPan(+1) ? 1 : -1;
                    Log.Write($"Diagnose pan: direction {direction}, {_mission.Describe()}");
                    step++;
                    return;
                case 1 when t < 1.0: return;
                case 1 when t < 1.5: _mission.Pan(direction * (t - 1.0) / 0.5); return;
                case 1: _mission.Pan(direction); _mission.CommitPan(direction); _mission.EndPan(); step++; return;
                case 2 when switchDirection != 0: step = 3; return;
                case 2 when t < 2.1: return;
                case 2 when t < 2.6: _mission.Pan(-direction * (t - 2.1) / 0.5); return;
                case 2: _mission.Pan(-direction); _mission.CommitPan(-direction); _mission.EndPan(); step++; return;
                case 3 when t < 3.2: return;
                case 3: _mission.Dismiss(); step++; return;
                case 4 when _mission.IsVisible && t < 6: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    Log.Write("Diagnose pan: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    /// <summary>
    /// For --diagnose-focus: opens Mission Control, then clicks <paramref name="window"/> and waits for the hand-off.
    /// With <paramref name="bySwipe"/>, points at it instead and swipes down: once abandoned halfway, then for real.
    /// </summary>
    public void RunFocusDiagnostics(IntPtr window, Action done, bool bySwipe = false)
    {
        if (bySwipe)
        {
            RunSwipeDownDiagnostics(window, done);
            return;
        }
        BeginMission(CaptureScene());
        var step = 0;
        var start = Clock.Now;
        var timer = new UiTimer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0: _mission.SetOpen(); step++; return;
                case 1 when t < 1.0: return;
                case 1:
                    if (!_mission.Choose(window)) Log.Write($"Diagnose focus: window 0x{window:X} isn't in the spread");
                    step++;
                    return;
                case 2 when _mission.IsVisible && t < 6: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    Log.Write("Diagnose focus: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    /// <summary>For --diagnose-zoom: opens Mission Control, shows the desktop bar, then clicks <paramref name="desktop"/>'s tile.</summary>
    public void RunZoomDiagnostics(int desktop, Action done)
    {
        BeginMission(CaptureScene());
        var step = 0;
        var stepStart = Clock.Now;
        var timer = new UiTimer { Interval = 8 };
        void Next() { step++; stepStart = Clock.Now; }
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - stepStart;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0: _mission.SetOpen(); Next(); return;
                case 1 when t < 0.3: return;
                case 1: _mission.ExpandDesktopBar(); Next(); return;
                case 2 when t < 0.8: return;
                case 2:
                    if (!_mission.ClickDesktop(desktop)) Log.Write($"Diagnose zoom: desktop {desktop + 1}'s tile can't be clicked");
                    Next();
                    return;
                case 3 when _mission.IsVisible && t < 4: return;
                // Then open and close it again: the zoom moved thumbnails a cached overview reuses.
                case 3: Log.Write($"Diagnose zoom: reopening (clock {Clock.Now:0.000})"); BeginMission(CaptureScene()); Next(); return;
                case 4 when t < 0.4: _mission.Render(t / 0.4); return;
                case 4: _mission.SetOpen(); Log.Write($"Diagnose zoom: reopened, {_mission.Describe()}"); Next(); return;
                case 5 when t < 0.5: return;
                case 5: _mission.Dismiss(); Next(); return;
                case 6 when _mission.IsVisible && t < 3: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    Log.Write("Diagnose zoom: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    private void RunSwipeDownDiagnostics(IntPtr window, Action done)
    {
        BeginMission(CaptureScene());
        Interop.NativeMethods.GetCursorPos(out var originalCursor);
        var step = 0;
        var start = Clock.Now;
        var stepStart = start;
        var timer = new UiTimer { Interval = 8 };
        void Next() { step++; stepStart = Clock.Now; }
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - stepStart;
            switch (step)
            {
                case 0 when t < 0.4: _mission.Render(t / 0.4); return;
                case 0:
                    _mission.SetOpen();
                    if (_mission.CentreOf(window) is { } centre) SetCursorPos(centre.X, centre.Y);
                    else Log.Write($"Diagnose swipe: window 0x{window:X} isn't in the spread");
                    Next();
                    return;
                case 1 when t < 0.45: return;
                case 1 when t < 0.5: _mission.WakeScaledCopies(); return; // fingers landing, as Prewarm does
                case 1: _mission.BeginGestureClose(); Next(); return;
                // First swipe: down to 60% open, then back up, released open.
                case 2 when t < 0.25: _mission.Render(1 - 0.4 * t / 0.25); return;
                case 2 when t < 0.5: _mission.Render(0.6 + 0.4 * (t - 0.25) / 0.25); return;
                case 2: _mission.SetOpen(); Log.Write("Diagnose swipe: abandoned swipe released open"); Next(); return;
                case 3 when t < 0.45: return;
                case 3 when t < 0.5: _mission.WakeScaledCopies(); return;
                case 3: _mission.BeginGestureClose(); Next(); return;
                // Second swipe: all the way down.
                case 4 when t < 0.35: _mission.Render(1 - t / 0.35); return;
                case 4: _mission.Render(0); _mission.FinishGestureClose(); Next(); return;
                case 5 when _mission.IsVisible && t < 3: return;
                default:
                    timer.Dispose();
                    _mission.Hide();
                    SetCursorPos(originalCursor.X, originalCursor.Y);
                    Log.Write("Diagnose swipe: done");
                    done();
                    return;
            }
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
