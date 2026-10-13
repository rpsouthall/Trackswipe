using TrackSwipe.Desktops;

namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    /// <summary>Exercises add/remove using only desktops created by this diagnostic.</summary>
    public void RunDesktopBarDiagnostics(Action done)
    {
        var original = _switcher?.CurrentDesktopId();
        if (original is null || _switcher is null) { Log.Write("Desktop bar diagnostic: shell unavailable."); done(); return; }
        var created = new List<Guid>();
        var testForm = new TestWindow();
        var testWindow = testForm.Handle;
        var windowDesktop = _windowDesktops.GetDesktopId(testWindow);
        var expectedFallback = VirtualDesktops.Read()?.Ids.LastOrDefault();
        bool MoveTestWindow(Guid desktop)
        {
            if (!Interop.NativeMethods.IsWindow(testWindow)) return false;
            try
            {
                _windowDesktops.MoveWindowToDesktop(testWindow, desktop);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Write($"Desktop bar diagnostic: test-window move failed (0x{ex.HResult:X8}).");
                return false;
            }
        }
        var step = 0;
        var reorderStage = 0;
        Guid[]? beforeReorder = null;
        var started = Clock.Now;
        var nextAt = started;
        BeginMission(CaptureScene());
        var timer = new UiTimer { Interval = 10 };
        void Next(double delay = 0.5) { step++; nextAt = Clock.Now + delay; }
        void Finish()
        {
            timer.Dispose();
            _mission.Hide();
            // Never remove an existing user desktop, even if a previous stage failed.
            foreach (var id in created)
                if (VirtualDesktops.Read()?.Ids.Contains(id) == true) _switcher.TryRemoveDesktop(id, original.Value);
            if (windowDesktop is { } home) MoveTestWindow(home);
            testForm.DestroyHandle();
            if (_switcher.CurrentDesktopId() != original) _switcher.TrySwitch(original.Value);
            Log.Write("Desktop bar diagnostic: done; original desktop restored.");
            done();
        }
        timer.Tick += (_, _) =>
        {
            var now = Clock.Now;
            // A hidden diagnostic launch suppresses the first ShowWindow request. Show the
            // owned test window after the message loop starts so Explorer gives it a desktop.
            if (step == 0 && !Interop.NativeMethods.IsWindowVisible(testWindow))
                Interop.NativeMethods.ShowWindow(testWindow, 4); // SW_SHOWNOACTIVATE
            if (now - started > 18) { Log.Write("Desktop bar diagnostic: timed out."); Finish(); return; }
            if (now < nextAt) return;
            switch (step)
            {
                case 0 when now - started < 0.4: _mission.Render((now - started) / 0.4); return;
                case 0: _mission.SetOpen(); Next(); return;
                case 1 when !_mission.IsOpen: return;
                case 1:
                    if (_mission.AddDesktop() is not { } first) { Finish(); return; }
                    created.Add(first); Next(); return;
                case 2 when !_mission.IsOpen: return;
                case 2:
                    if (_mission.AddDesktop() is not { } second) { Finish(); return; }
                    if (!MoveTestWindow(second)) { created.Add(second); Finish(); return; }
                    created.Add(second); Next(); return;
                case 3 when !_mission.IsOpen: return;
                case 3:
                    var ids = VirtualDesktops.Read()?.Ids.ToArray();
                    if (ids is null) { Finish(); return; }
                    if (reorderStage == 0)
                    {
                        beforeReorder = ids;
                        if (!_mission.DragDesktopForDiagnostics(created[1], ids.Length - 2, cancel: true)) { Finish(); return; }
                        reorderStage++; nextAt = Clock.Now + 0.5; return;
                    }
                    if (reorderStage == 1)
                    {
                        Log.Write($"Desktop bar diagnostic: cancelled drag preserved order: {ids.SequenceEqual(beforeReorder!)}.");
                        if (!_mission.DragDesktopForDiagnostics(created[1], ids.Length - 2)) { Finish(); return; }
                        reorderStage++; nextAt = Clock.Now + 0.5; return;
                    }
                    if (reorderStage == 2)
                    {
                        var expected = DesktopReorder.Move(beforeReorder!, created[1], ids.Length - 2);
                        Log.Write($"Desktop bar diagnostic: drag persisted shell order: {ids.SequenceEqual(expected)}, window stayed on its desktop: {_windowDesktops.GetDesktopId(testWindow) == created[1]}.");
                        if (!_mission.DragDesktopForDiagnostics(created[1], ids.Length - 1)) { Finish(); return; }
                        reorderStage++; nextAt = Clock.Now + 0.5; return;
                    }
                    Log.Write($"Desktop bar diagnostic: reorder round trip restored order: {ids.SequenceEqual(beforeReorder!)}.");
                    if (!_mission.RemoveDesktop(created[0])) { Finish(); return; }
                    Next(); return;
                case 4 when !_mission.IsOpen: return;
                case 4:
                    // Test removing the actual current desktop, but only one we just created.
                    if (!_switcher.TrySwitch(created[1])) { Finish(); return; }
                    if (!MoveTestWindow(created[1])) { Finish(); return; }
                    Log.Write($"Desktop bar diagnostic: test window moved to {_windowDesktops.GetDesktopId(testWindow)}, expected {created[1]}, visible {Interop.NativeMethods.IsWindowVisible(testWindow)}.");
                    _mission.RefreshForDesktopBarDiagnostics();
                    if (!_mission.RemoveDesktop(created[1])) { Finish(); return; }
                    Next(); return;
                case 5 when !_mission.IsOpen: return;
                case 5:
                    if (testWindow != IntPtr.Zero)
                    {
                        var preserved = Interop.NativeMethods.IsWindow(testWindow) && _windowDesktops.GetDesktopId(testWindow) == expectedFallback;
                        Log.Write($"Desktop bar diagnostic: test window preserved on fallback desktop: {preserved}; actual {_windowDesktops.GetDesktopId(testWindow)}, expected {expectedFallback}, alive {Interop.NativeMethods.IsWindow(testWindow)}.");
                    }
                    Log.Write($"Desktop bar diagnostic: both temporary desktops removed; {_mission.Describe()}"); Next(); return;
                default: Finish(); return;
            }
        };
        timer.Start();
    }

    /// <summary>A plain window for the desktop removal test: it must end up on the fallback desktop, alive.</summary>
    private sealed class TestWindow : NativeWindow
    {
        public TestWindow()
        {
            var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            var dpi = Interop.NativeMethods.GetDpiForSystem();
            int width = (int)(600 * dpi / 96), height = (int)(380 * dpi / 96);
            CreateHandle(new CreateParams
            {
                Caption = "TrackSwipe desktop removal verification - this window must survive desktop removal",
                Style = 0x00CF0000, // WS_OVERLAPPEDWINDOW
                X = work.X + (work.Width - width) / 2, Y = work.Y + (work.Height - height) / 2, Width = width, Height = height,
            });
            Interop.NativeMethods.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        }
    }
}