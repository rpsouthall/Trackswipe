namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    /// <summary>
    /// For --diagnose-window-drag &lt;hwnd&gt;: drags that window (another app's) in Mission Control onto a temporary
    /// desktop's tile, onto +, then into empty bar space on each side of the current desktop, checking where Explorer
    /// puts the window (and the new desktop) each time. Only desktops this test made are removed, which brings the
    /// window back to the desktop it started on.
    /// </summary>
    public void RunWindowDragDiagnostics(IntPtr window, Action done)
    {
        var original = _switcher?.CurrentDesktopId();
        if (original is null || _switcher is null) { Log.Write("Window drag diagnostic: shell unavailable."); done(); return; }
        var created = new List<Guid>();
        var beforeDrop = -1;
        var step = 0;
        var started = Clock.Now;
        var nextAt = started;
        BeginMission(CaptureScene());
        var timer = new UiTimer { Interval = 10 };
        void Next(double delay = 0.6) { step++; nextAt = Clock.Now + delay; }
        void Finish()
        {
            timer.Dispose();
            _mission.Hide();
            foreach (var id in created)
                if (VirtualDesktops.Read()?.Ids.Contains(id) == true) _switcher.TryRemoveDesktop(id, original.Value);
            if (_switcher.CurrentDesktopId() != original) _switcher.TrySwitch(original.Value);
            Log.Write($"Window drag diagnostic: done; window back on the original desktop: {_windowDesktops.GetDesktopId(window) == original}.");
            done();
        }
        timer.Tick += (_, _) =>
        {
            var now = Clock.Now;
            if (now - started > 40) { Log.Write("Window drag diagnostic: timed out."); Finish(); return; }
            if (now < nextAt) return;
            switch (step)
            {
                case 0 when now - started < 0.4: _mission.Render((now - started) / 0.4); return;
                case 0: _mission.SetOpen(); Next(); return;
                case 1 when !_mission.IsOpen: return;
                case 1:
                    if (_mission.AddDesktop() is not { } temporary) { Finish(); return; }
                    created.Add(temporary); Next(); return;
                case 2 when !_mission.IsOpen: return;
                case 2:
                    var index = VirtualDesktops.Read()?.Ids.ToList().IndexOf(created[0]) ?? -1;
                    var dropped = _mission.DragWindowForDiagnostics(window, index);
                    Log.Write($"Window drag diagnostic: dragged onto desktop {index + 1}'s tile, drop taken {dropped}.");
                    if (!dropped) { Finish(); return; }
                    Next(); return;
                case 3 when !_mission.IsOpen: return;
                case 3:
                    Log.Write($"Window drag diagnostic: window on the temporary desktop {_windowDesktops.GetDesktopId(window) == created[0]}, " +
                        $"left the spread {_mission.CentreOf(window) is null}; {_mission.Describe()}");
                    // Back to this desktop the same way removing a desktop does, then onto + this time.
                    _switcher.TryMoveWindow(window, original.Value);
                    _mission.RefreshForDesktopBarDiagnostics();
                    Next(); return;
                case 4 when !_mission.IsOpen: return;
                case 4:
                    var plus = _mission.DragWindowForDiagnostics(window, -1);
                    Log.Write($"Window drag diagnostic: dragged onto +, drop taken {plus}.");
                    if (!plus) { Finish(); return; }
                    Next(1.0); return;
                case 5 when !_mission.IsOpen: return;
                case 5:
                    var desktops = VirtualDesktops.Read();
                    var home = _windowDesktops.GetDesktopId(window);
                    if (home is { } fresh && fresh != original && !created.Contains(fresh)) created.Add(fresh);
                    Log.Write($"Window drag diagnostic: + made desktop {desktops?.Count} and the window is on it: {home is { } h && desktops?.Ids.LastOrDefault() == h}.");
                    // Back again, then into empty bar space on each side of the current desktop.
                    _switcher.TryMoveWindow(window, original.Value);
                    _mission.RefreshForDesktopBarDiagnostics();
                    Next(); return;
                case 6 when !_mission.IsOpen: return;
                case 8 when !_mission.IsOpen: return;
                case 6:
                case 8:
                    var side = step == 6 ? -2 : -3;
                    beforeDrop = VirtualDesktops.Read()?.Ids.ToList().IndexOf(original.Value) ?? -1;
                    var empty = _mission.DragWindowForDiagnostics(window, side);
                    Log.Write($"Window drag diagnostic: dragged into empty bar space {(side == -2 ? "left" : "right")} of the desktops, drop taken {empty}.");
                    if (!empty) { Finish(); return; }
                    Next(1.0); return;
                case 7 when !_mission.IsOpen: return;
                case 9 when !_mission.IsOpen: return;
                case 7:
                case 9:
                    var ids = VirtualDesktops.Read()?.Ids.ToList() ?? new List<Guid>();
                    var landed = _windowDesktops.GetDesktopId(window);
                    if (landed is { } made && made != original && !created.Contains(made)) created.Add(made);
                    var at = landed is { } l ? ids.IndexOf(l) : -1;
                    var expected = step == 7 ? ids.IndexOf(original.Value) - 1 : ids.IndexOf(original.Value) + 1;
                    Log.Write($"Window drag diagnostic: new desktop {at + 1} of {ids.Count} holds the window; current desktop was {beforeDrop + 1}, now {ids.IndexOf(original.Value) + 1}; " +
                        $"placed {(step == 7 ? "just before" : "just after")} it: {landed is not null && landed != original && at == expected}.");
                    _switcher.TryMoveWindow(window, original.Value);
                    _mission.RefreshForDesktopBarDiagnostics();
                    Next(); return;
                default: Finish(); return;
            }
        };
        timer.Start();
    }
}
