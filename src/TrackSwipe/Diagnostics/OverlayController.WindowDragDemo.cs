namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    /// <summary>
    /// For --diagnose-window-drag-demo &lt;hwnd&gt; &lt;desktop&gt;: a paced, recordable run of dragging a window onto
    /// another desktop in Mission Control. Opens, picks the window up, glides it onto the desktop's preview, lets
    /// it land, then closes. Really moves the window.
    /// </summary>
    public void RunWindowDragDemo(IntPtr window, int desktopIndex, Action done)
    {
        BeginMission(CaptureScene());
        var step = 0;
        var start = Clock.Now;
        Point from = default, to = default;
        double dragStart = 0, droppedAt = 0;
        var timer = new UiTimer { Interval = 8 };
        static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
        static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        void Finish() { timer.Dispose(); _mission.Hide(); done(); }
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            switch (step)
            {
                case 0 when t < 0.45: _mission.Render(EaseOut(t / 0.45)); return;
                case 0: _mission.SetOpen(); step++; return;
                case 1 when t < 1.3: return;
                case 1:
                    if (!_mission.PressWindowForDemo(window, desktopIndex, out from, out to)) { Log.Write("Window drag demo: window or desktop not found."); Finish(); return; }
                    dragStart = Clock.Now; step++; return;
                case 2:
                    var p = Math.Min(1, (Clock.Now - dragStart) / 1.1);
                    var e = EaseInOut(p);
                    // A slight arc, as a hand would move.
                    var point = new Point((int)(from.X + (to.X - from.X) * e), (int)(from.Y + (to.Y - from.Y) * e - Math.Sin(e * Math.PI) * 40));
                    _mission.MovePointerForDemo(point);
                    if (p < 1) return;
                    Log.Write($"Window drag demo: dropped, taken {_mission.ReleasePointerForDemo(to)}.");
                    droppedAt = Clock.Now; step++; return;
                case 3 when Clock.Now - droppedAt < 1.6: return;
                case 3: _mission.Dismiss(); droppedAt = Clock.Now; step++; return;
                case 4 when _mission.IsVisible && Clock.Now - droppedAt < 3: return;
                default: Finish(); return;
            }
        };
        timer.Start();
    }
}
