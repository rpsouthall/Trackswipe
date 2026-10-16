using System.Runtime.InteropServices;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

internal sealed partial class OverlayController
{
    /// <summary>
    /// For --diagnose-taskbar-jump &lt;hwnd&gt;: puts that window (another app's) on a temporary desktop, finds its
    /// taskbar button, and really clicks it (synthesised mouse input, through the same click watch as a real click),
    /// checking the slide is prepared and that Windows lands on that desktop. Then puts everything back: the window,
    /// the desktop, the pointer, and removes only the desktop this test made. Needs the taskbar to show windows from
    /// all desktops.
    /// </summary>
    public void RunTaskbarJumpDiagnostics(IntPtr window, Action done)
    {
        var original = _switcher?.CurrentDesktopId();
        if (original is null || _switcher is null) { Log.Write("Taskbar jump diagnostic: shell unavailable."); done(); return; }
        GetCursorPos(out var cursor);
        Guid? temporary = null;
        Rectangle button = default;
        var step = 0;
        var started = Clock.Now;
        var nextAt = started;
        var timer = new UiTimer { Interval = 20 };
        void Next(double delay) { step++; nextAt = Clock.Now + delay; }
        void Finish()
        {
            timer.Dispose();
            if (_switcher.CurrentDesktopId() != original) _switcher.TrySwitch(original.Value);
            _switcher.TryMoveWindow(window, original.Value);
            if (temporary is { } id && VirtualDesktops.Read()?.Ids.Contains(id) == true) _switcher.TryRemoveDesktop(id, original.Value);
            SetCursorPos(cursor.X, cursor.Y);
            Log.Write($"Taskbar jump diagnostic: done; back on the original desktop {_switcher.CurrentDesktopId() == original}, " +
                $"window there too {_windowDesktops.GetDesktopId(window) == original}.");
            done();
        }
        timer.Tick += (_, _) =>
        {
            var now = Clock.Now;
            if (now - started > 15) { Log.Write("Taskbar jump diagnostic: timed out."); Finish(); return; }
            if (now < nextAt) return;
            switch (step)
            {
                case 0:
                    temporary = _switcher.TryCreateDesktop();
                    if (temporary is null || !_switcher.TryMoveWindow(window, temporary.Value)) { Log.Write("Taskbar jump diagnostic: couldn't set up a desktop."); Finish(); return; }
                    Next(0.8); return;
                case 1:
                    // A fresh lookup that includes the moved window, as a click would find it.
                    TaskbarButtons.Refresh(IdleScene().Items.Where(item => item.Kind == SceneKind.Window && item.IsAppWindow).Select(item => item.Window).Append(window));
                    Next(0.1); return;
                case 2 when TaskbarButtons.Looking: return;
                case 2:
                    if (TaskbarButtons.Latest is not { } known || !known.TryGetValue(window, out button))
                    {
                        Log.Write("Taskbar jump diagnostic: the window's taskbar button wasn't found.");
                        Finish(); return;
                    }
                    var target = VirtualDesktops.Read()?.Ids.ToList().IndexOf(temporary!.Value) ?? -1;
                    Log.Write($"Taskbar jump diagnostic: window's button at {button.X},{button.Y} {button.Width}x{button.Height}; window on desktop {target + 1}.");
                    var point = new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
                    Click(point);
                    Next(1.5); return;
                case 3:
                    Log.Write($"Taskbar jump diagnostic: Windows went to the window's desktop: {_switcher.CurrentDesktopId() == temporary}.");
                    Finish(); return;
            }
        };
        timer.Start();
    }

    /// <summary>
    /// A left click at a screen point, as real input: the pointer arrives with a few small moves (the taskbar only
    /// treats a button as pressed once it has seen the pointer over it), rests, then presses and releases.
    /// </summary>
    private static unsafe void Click(Point point)
    {
        var input = new MouseInput { Type = 0, Flags = 0x0001 /* MOVE */ };
        SetCursorPos(point.X - 6, point.Y + 3);
        for (var i = 0; i < 3; i++)
        {
            input.Dx = 2; input.Dy = -1;
            SendInput(1, &input, sizeof(MouseInput));
            Thread.Sleep(30);
        }
        Thread.Sleep(250);
        input = new MouseInput { Type = 0, Flags = 0x0002 /* LEFTDOWN */ };
        SendInput(1, &input, sizeof(MouseInput));
        Thread.Sleep(80); // a press and release apart, as a finger makes them
        input = new MouseInput { Type = 0, Flags = 0x0004 /* LEFTUP */ };
        SendInput(1, &input, sizeof(MouseInput));
    }

    /// <summary>INPUT holding a MOUSEINPUT: the union starts pointer-aligned, and INPUT is 40 bytes on 64-bit.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern unsafe uint SendInput(uint count, MouseInput* inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}
