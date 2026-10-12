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
        var testForm = new Form { Text = "TrackSwipe desktop removal verification", Size = new Size(600, 380),
            StartPosition = FormStartPosition.CenterScreen, BackColor = Color.FromArgb(35, 55, 85) };
        testForm.Controls.Add(new Label { Text = "This test window must survive desktop removal.", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White });
        testForm.Show();
        var testWindow = testForm.Handle;
        var windowDesktop = _windowDesktops.GetDesktopId(testWindow);
        var expectedFallback = VirtualDesktops.Read()?.Ids.LastOrDefault();
        bool MoveTestWindow(Guid desktop)
        {
            if (!Interop.NativeMethods.IsWindow(testWindow)) return false;
            try
            {
                var manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), true)!)!;
                manager.MoveWindowToDesktop(testWindow, ref desktop);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Write($"Desktop bar diagnostic: test-window move failed (0x{ex.HResult:X8}).");
                return false;
            }
        }
        var step = 0;
        var started = Clock.Now;
        var nextAt = started;
        BeginMission(CaptureScene());
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        void Next(double delay = 0.5) { step++; nextAt = Clock.Now + delay; }
        void Finish()
        {
            timer.Dispose();
            _mission.Hide();
            // Never remove an existing user desktop, even if a previous stage failed.
            foreach (var id in created)
                if (VirtualDesktops.Read()?.Ids.Contains(id) == true) _switcher.TryRemoveDesktop(id, original.Value);
            if (windowDesktop is { } home) MoveTestWindow(home);
            testForm.Dispose();
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
}
