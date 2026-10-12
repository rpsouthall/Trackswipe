using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TrackSwipe.Core;
using TrackSwipe.Interop;

namespace TrackSwipe;

/// <summary>The one timebase shared by touch frames and display frames, in seconds.</summary>
internal static class Clock
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>Local diagnostics log. Never records touch positions.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackSwipe", "trackswipe.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}

/// <summary>
/// Frame statistics for one animation (from the display clock starting to stopping), logged as a single
/// line so smoothness can be checked after the fact: delivered frame rate, per-frame work, DWM calls.
/// </summary>
internal sealed class FramePerf
{
    private readonly List<double> _work = new();
    private readonly List<double> _gaps = new();
    private double _previous = double.NaN;
    private long _updatesAtStart;

    /// <summary>Thumbnail property updates sent to DWM, across all sprites.</summary>
    public static long ThumbnailUpdates;

    public void Frame(double started, double finished)
    {
        if (double.IsNaN(_previous)) _updatesAtStart = ThumbnailUpdates;
        else _gaps.Add(started - _previous);
        _previous = started;
        _work.Add(finished - started);
    }

    public void Flush()
    {
        if (_work.Count > 2)
        {
            var span = _gaps.Sum();
            var sorted = _gaps.OrderBy(gap => gap).ToArray();
            Log.Write($"Frames: {_work.Count} in {span * 1000:0} ms ({(_work.Count - 1) / span:0} fps), " +
                $"gap median {sorted[sorted.Length / 2] * 1000:0.0} ms / worst {sorted[^1] * 1000:0.0} ms, " +
                $"work avg {_work.Average() * 1000:0.0} ms / worst {_work.Max() * 1000:0.0} ms, " +
                $"{(ThumbnailUpdates - _updatesAtStart) / (double)_work.Count:0} thumbnail updates per frame");
        }
        _work.Clear();
        _gaps.Clear();
        _previous = double.NaN;
    }
}

internal sealed record VirtualDesktopInfo(IReadOnlyList<Guid> Ids, int Index, IReadOnlyList<string?> Names)
{
    public int Count => Ids.Count;

    /// <summary>The name the user gave the desktop, or Windows' default "Desktop N".</summary>
    public string Label(int index) => index < Names.Count && !string.IsNullOrWhiteSpace(Names[index]) ? Names[index]! : $"Desktop {index + 1}";

    /// <summary>The desktop <paramref name="step"/> places from the current one, or null if there isn't one or the current one is unknown.</summary>
    public Guid? Neighbour(int step)
    {
        var index = Index + step;
        return Index >= 0 && index >= 0 && index < Ids.Count ? Ids[index] : null;
    }
}

internal static class VirtualDesktops
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>
    /// Reads Explorer's desktop list from the registry. This isn't a documented API, so callers
    /// treat null (or Index -1) as "unknown" and allow both directions.
    /// </summary>
    public static VirtualDesktopInfo? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length == 0 || ids.Length % 16 != 0) return null;

            var desktops = Enumerable.Range(0, ids.Length / 16).Select(i => new Guid(ids.AsSpan(i * 16, 16))).ToArray();
            var index = key.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } current
                ? Array.IndexOf(desktops, new Guid(current))
                : -1;
            var names = desktops.Select(id =>
            {
                using var desktop = key.OpenSubKey($@"Desktops\{id:B}");
                return desktop?.GetValue("Name") as string;
            }).ToArray();
            return new VirtualDesktopInfo(desktops, index, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>Performs gesture actions with the same shortcuts a user would press.</summary>
internal static class KeySender
{
    private const ushort VK_TAB = 0x09, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44, VK_LWIN = 0x5B, VK_LCONTROL = 0xA2;

    public static void Send(SwipeAction action)
    {
        switch (action)
        {
            case SwipeAction.NextDesktop: Chord(VK_LWIN, VK_LCONTROL, VK_RIGHT); break;
            case SwipeAction.PreviousDesktop: Chord(VK_LWIN, VK_LCONTROL, VK_LEFT); break;
            case SwipeAction.SwipeUp: Chord(VK_LWIN, VK_TAB); break;   // Task View
            case SwipeAction.SwipeDown: Chord(VK_LWIN, VK_D); break;   // show desktop
        }
    }

    /// <summary>Presses the keys in order and releases them in reverse, as one atomic SendInput batch.</summary>
    private static void Chord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], down: true);
            inputs[^(i + 1)] = Key(keys[i], down: false);
        }
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Write($"SendInput was blocked (error {Marshal.GetLastWin32Error()}); an elevated window may have focus.");
    }

    private static INPUT Key(ushort vk, bool down)
    {
        var flags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP;
        if (vk is VK_LEFT or VK_RIGHT or VK_LWIN) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Union = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = flags } },
        };
    }
}

/// <summary>
/// Calls back on the UI thread once per compositor frame, paced by DwmFlush rather than a timer.
/// Sleeps on an event when stopped, so idle cost is zero.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Action _onFrame;
    private readonly ManualResetEventSlim _running = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private int _pending;

    public FrameClock(SynchronizationContext ui, Action onFrame)
    {
        _ui = ui;
        _onFrame = onFrame;
        _thread = new Thread(Run) { IsBackground = true, Name = "TrackSwipe frame clock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Start() => _running.Set();

    public void Stop() => _running.Reset();

    public void Dispose()
    {
        _disposed = true;
        _running.Set();
        _thread.Join(500);
    }

    private void Run()
    {
        while (true)
        {
            _running.Wait();
            if (_disposed) return;
            if (NativeMethods.DwmFlush() != 0) Thread.Sleep(8);

            // Never queue more than one frame: if the UI thread is busy, skip rather than pile up.
            if (_running.IsSet && Interlocked.Exchange(ref _pending, 1) == 0)
            {
                _ui.Post(_ =>
                {
                    Volatile.Write(ref _pending, 0);
                    if (_running.IsSet && !_disposed) _onFrame();
                }, null);
            }
        }
    }
}
