using System.Runtime.InteropServices;
using TrackSwipe.Interop;

namespace TrackSwipe.Overlay;

/// <summary>
/// Tells whether a captured scene may have gone stale, at almost no cost while idle. Hooks top-level
/// window changes only until the first one arrives: after that the scene is known to be stale, so further
/// events (window moves fire constantly) would only wake this thread for nothing. <see cref="Arm"/>
/// re-hooks just before the next capture. Nothing is rebuilt here; the next gesture recaptures.
/// </summary>
internal sealed class SceneInvalidation : IDisposable
{
    private static readonly uint[] Kinds = { 0x0003, 0x8000, 0x8001, 0x8002, 0x8003, 0x8004, 0x800B, 0x8017, 0x8018 };

    private readonly List<IntPtr> _hooks = new();
    private readonly WinEvent _callback;
    private readonly uint _process = (uint)Environment.ProcessId;
    private readonly UiTimer _unhook = new() { Interval = 1 };

    public SceneInvalidation()
    {
        _callback = (_, kind, window, objectId, child, _, _) =>
        {
            if (Dirty || window == IntPtr.Zero || objectId != 0 || child != 0) return;
            NativeMethods.GetWindowThreadProcessId(window, out var process);
            if (process == _process) return;
            Dirty = true;
            _unhook.Start(); // not from inside the hook callback itself
        };
        _unhook.Tick += (_, _) => { _unhook.Stop(); if (Dirty) Unhook(); };
    }

    /// <summary>True until a capture is armed, and again once any window changes after it.</summary>
    public bool Dirty { get; private set; } = true;

    /// <summary>Whether changes can be detected at all; without hooks every gesture recaptures.</summary>
    public bool Available { get; private set; }

    /// <summary>Call right before capturing a scene: changes from here on mark it stale.</summary>
    public void Arm()
    {
        _unhook.Stop();
        if (_hooks.Count == 0)
        {
            foreach (var kind in Kinds)
            {
                var hook = SetWinEventHook(kind, kind, IntPtr.Zero, _callback, 0, 0, 2 /* SKIPOWNPROCESS, OUTOFCONTEXT */);
                if (hook != IntPtr.Zero) _hooks.Add(hook);
            }
            Available = _hooks.Count == Kinds.Length;
        }
        Dirty = !Available;
    }

    private void Unhook()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }

    public void Dispose() { _unhook.Dispose(); Unhook(); }

    private delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
