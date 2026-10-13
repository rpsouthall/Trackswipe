namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    public void RunBarRevealDiagnostics(Action done)
    {
        BeginMission(CaptureScene());
        var start = Clock.Now;
        var phase = 0;
        var timer = new UiTimer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            var t = Clock.Now - start;
            switch (phase)
            {
                case 0 when t < 0.3: _mission.Render(t / 0.3); break;
                case 0: _mission.SetOpen(); phase++; break;
                case 1 when t >= 0.7: _mission.HoverWindowForDiagnostics(0); phase++; break;
                case 2 when t >= 1.2: _mission.HoverWindowForDiagnostics(1); phase++; break;
                case 3 when t >= 1.7: _mission.HoverWindowForDiagnostics(-1); phase++; break;
                case 4 when t >= 2: _mission.HoverDesktopBarForDiagnostics(true); phase++; break;
                case 5 when t >= 2.7: _mission.HoverDesktopBarForDiagnostics(false); phase++; break;
                case 6 when t >= 3.2: _mission.HoverWindowForDiagnostics(0); phase++; break;
                case 7 when t >= 3.8: _mission.HoverWindowForDiagnostics(-1); phase++; break;
                case 8 when t >= 4.2:
                    timer.Dispose();
                    _mission.Hide();
                    Log.Write("Desktop bar reveal diagnostic: compact labels, hovered previews, retained previews; sparse highlights painted, moved, cleared and released; done.");
                    done();
                    break;
            }
        };
        timer.Start();
    }
}
