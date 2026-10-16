#if DIAGNOSTICS
using System.Runtime.InteropServices;
using TrackSwipe.Core;
using TrackSwipe.Touchpad;

namespace TrackSwipe;

internal sealed partial class TrayApp
{
    /// <summary>
    /// For --diagnose-tap: plays synthetic three-finger taps through the real input path (the touchpad reader's filter,
    /// the gesture engine and the tap detector) and checks that the first opens Start and the second closes it. Quits after.
    /// </summary>
    private void RunTapDiagnostics()
    {
        var wasOn = _tapForStart;
        _tapForStart = true;
        var taps = 0;
        _tap.Tapped += () => taps++;
        string Start()
        {
            var window = FindWindowW("Windows.UI.Core.CoreWindow", "Search");
            if (window == IntPtr.Zero) return "no Start window";
            DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int));
            return cloaked == 0 ? "open" : "closed";
        }
        void Tap() => _compositionThread.Invoke(() =>
        {
            // Three fingers down for 0.1 s at 125 Hz, then all lifted, through the same filter the reader applies.
            var engaged = false;
            var started = Clock.Now;
            var fingers = new[] { new Contact(0, 40, 35, true, true), new Contact(1, 52, 33, true, true), new Contact(2, 64, 35, true, true) };
            for (var i = 0; i <= 13; i++)
            {
                var contacts = i < 13 ? fingers : Array.Empty<Contact>();
                var frame = new TouchFrame(started + i / 125.0, contacts, 100, 70) { TouchStarted = started };
                if (TouchFilter.Deliver(contacts.Length, ReportedFingers(_engine.Options.Fingers), false, ref engaged)) ProcessFrame(frame);
            }
        });

        var step = 0;
        var timer = new UiTimer { Interval = 900 };
        Log.Write($"Tap diagnostic: Start {Start()} before.");
        timer.Tick += (_, _) =>
        {
            switch (step++)
            {
                case 0: Tap(); return;
                case 1: Log.Write($"Tap diagnostic: after the first tap ({taps} recognised), Start {Start()}."); Tap(); return;
                case 2:
                    Log.Write($"Tap diagnostic: after the second tap ({taps} recognised), Start {Start()}.");
                    _tapForStart = wasOn;
                    timer.Dispose();
                    ExitOnShell();
                    return;
            }
        };
        timer.Start();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? className, string? title);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
#endif
