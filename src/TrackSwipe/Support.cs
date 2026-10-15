using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private static readonly System.Collections.Concurrent.BlockingCollection<(string? Message, TaskCompletionSource? Barrier)> Pending = new(512);

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackSwipe", "trackswipe.log");
    private static readonly Thread Writer = StartWriter();

    /// <summary>Per-gesture detail (frame statistics, preparation timings). Off unless --verbose or TRACKSWIPE_VERBOSE=1.</summary>
    public static bool VerboseEnabled { get; set; }

    public static void Write(string message)
    {
        Pending.TryAdd(($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}", null));
    }

    /// <summary>Writes only when verbose logging is on. Callers building costly messages check <see cref="VerboseEnabled"/> first.</summary>
    public static void Verbose(string message)
    {
        if (VerboseEnabled) Write(message);
    }

    /// <summary>
    /// Writes only when verbose logging is on. An interpolated message isn't even formatted when it's off (the usual
    /// case), so these calls cost nothing in normal use: no string is built, nothing is allocated.
    /// </summary>
    public static void Verbose(ref VerboseMessage message)
    {
        if (message.Enabled) Write(message.ToStringAndClear());
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(() =>
        {
            foreach (var entry in Pending.GetConsumingEnumerable())
            {
                if (entry.Barrier is { } barrier) { barrier.SetResult(); continue; }
                Append(entry.Message!);
            }
        })
        { IsBackground = true, Name = "TrackSwipe log", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        return thread;
    }

    public static void Flush()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Pending.TryAdd((null, barrier), 1000)) barrier.Task.Wait(2000);
    }

    private static void Append(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Bounded: past 2 MB the log becomes trackswipe.log.old (replacing the previous one) and starts afresh.
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > 2 * 1024 * 1024) File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, message);
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
    private readonly AnimationStatistics _statistics = new();
    private long _updatesAtStart, _changesAtStart;

    /// <summary>Thumbnail resizes sent to DWM, across all sprites: the only per-thumbnail cross-process calls.</summary>
    public static long ThumbnailUpdates;

    private static int _shownAt = -1;
    private static int _frames;

    /// <summary>The overlay just appeared: frames before this (fingers resting, preparation) aren't seen.</summary>
    public static void MarkShown()
    {
        if (_shownAt < 0) _shownAt = _frames;
    }

    public void Frame(double started, double finished)
    {
        _frames++;
        if (_statistics.Frames == 0)
        {
            _updatesAtStart = ThumbnailUpdates;
            _changesAtStart = Composition.Changes;
        }
        _statistics.Add(started, finished);
    }

    public void Flush()
    {
        if (_statistics.Frames > 2 && Log.VerboseEnabled)
        {
            var slow = new System.Text.StringBuilder();
            foreach (var (frame, gap) in _statistics.SlowSamples)
            {
                if (slow.Length > 0) slow.Append(',');
                slow.Append($"{frame}:{gap * 1000:0}");
            }
            if (_statistics.SlowGaps > _statistics.SlowSamples.Length) slow.Append($" (+{_statistics.SlowGaps - _statistics.SlowSamples.Length} more)");
            Log.Verbose($"Frames: {_statistics.Frames} in {_statistics.Span * 1000:0} ms ({(_statistics.Frames - 1) / _statistics.Span:0} fps), " +
                $"gap median ~{_statistics.MedianGap * 1000:0.0} ms / worst {_statistics.WorstGap * 1000:0.0} ms, " +
                $"work avg {_statistics.AverageWork * 1000:0.0} ms / worst {_statistics.WorstWork * 1000:0.0} ms, " +
                $"per frame {(ThumbnailUpdates - _updatesAtStart) / (double)_statistics.Frames:0} DWM resizes and {(Composition.Changes - _changesAtStart) / (double)_statistics.Frames:0} visual changes in one commit; " +
                $"gaps over 10 ms at frames {slow}" +
                (_shownAt >= 0 ? $"; overlay shown from frame {_shownAt + 1}" : ""));
        }
        _shownAt = -1;
        _frames = 0;
        _statistics.Reset();
    }
}

internal sealed record VirtualDesktopInfo(IReadOnlyList<Guid> Ids, int Index, IReadOnlyList<string?> Names)
{
    public int Count => Ids.Count;

    /// <summary>The name the user gave the desktop, or Windows' default "Desktop N".</summary>
    public string Label(int index) => Overlay.DesktopReorder.Label(index, index < Names.Count ? Names[index] : null);

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
            case SwipeAction.SwipeDown: if (!ToggleDesktop()) Chord(VK_LWIN, VK_D); break; // show desktop
        }
    }

    /// <summary>
    /// Show desktop through Explorer itself: the command behind the taskbar's show-desktop corner and
    /// Shell.Application's ToggleDesktop. Unlike a synthesised Win+D, keyboard remappers can't intercept it (PowerToys
    /// Keyboard Manager turning the Windows key into another key would otherwise swallow it). Posted, so a busy Explorer
    /// never holds up a frame. False if there's no taskbar to ask.
    /// </summary>
    private static bool ToggleDesktop()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        return taskbar != IntPtr.Zero && NativeMethods.PostMessageW(taskbar, 0x0111 /* WM_COMMAND */, 407 /* toggle desktop */, IntPtr.Zero);
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
/// A message-only window on the UI thread through which other threads reach it: display frames arrive
/// as <em>sent</em> messages, which Windows delivers ahead of anything posted, so a frame never waits
/// behind other work; touch input and other notifications arrive posted.
/// </summary>
internal sealed class UiPump : NativeWindow, IDisposable
{
    private const int WM_FRAME = NativeMethods.WM_APP + 1, WM_POSTED = NativeMethods.WM_APP + 2;

    private readonly Action<double> _onFrame;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();
    private bool _inFrame;

    public UiPump(Action<double> onFrame)
    {
        _onFrame = onFrame;
        CreateHandle(new CreateParams { Caption = "TrackSwipe pump", Parent = NativeMethods.HWND_MESSAGE });
        UiThread = Thread.CurrentThread;
    }

    public Thread UiThread { get; }

    /// <summary>From the frame thread: starts a frame for <paramref name="presentTime"/> on the UI thread, waiting until it begins.</summary>
    public void RunFrame(double presentTime) =>
        NativeMethods.SendMessageTimeoutW(Handle, WM_FRAME, IntPtr.Zero, new IntPtr(BitConverter.DoubleToInt64Bits(presentTime)),
            NativeMethods.SMTO_NORMAL, 250, out _);

    /// <summary>From any thread: runs <paramref name="action"/> on the UI thread soon.</summary>
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        NativeMethods.PostMessageW(Handle, WM_POSTED, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll")]
    private static extern bool ReplyMessage(IntPtr result);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_FRAME:
                // Release the frame thread before running the frame. While a message sent from another thread
                // is unreplied, COM refuses calls out of this thread (RPC_E_CANTCALLOUT_ININPUTSYNCCALL), and
                // a frame may need to call Explorer: the instant desktop switch would fail and fall back to the
                // shortcut, with Windows' own animation.
                ReplyMessage(IntPtr.Zero);
                // Released, the frame thread may send the next frame while this one runs, and any call this frame
                // makes to another thread or process lets sent messages through. Never start a frame inside one:
                // it would see half-updated state. Skip that tick; the next one comes a refresh later.
                if (_inFrame) return;
                _inFrame = true;
                try
                {
                    _onFrame(BitConverter.Int64BitsToDouble(m.LParam.ToInt64()));
                }
                finally
                {
                    _inFrame = false;
                }
                return;
            case WM_POSTED:
                // Input callbacks can build compositor animation curves. Don't deliver a display
                // frame inside one while its gesture and visual state are only partly updated.
                var wasInFrame = _inFrame;
                _inFrame = true;
                try { while (_posted.TryDequeue(out var action)) action(); }
                finally { _inFrame = wasInFrame; }
                return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Runs animation frames on the UI thread, one per display refresh, from a dedicated thread that waits
/// on the Windows 11 compositor clock. That clock ticks every refresh whether or not anything changed;
/// the old DwmFlush only returned after a composition, so each frame waited on the one before and the
/// rate halved. Each frame is evaluated for its predicted display time, so motion stays even when a
/// wake-up is late. While running it boosts the compositor to the display's highest refresh rate (120 Hz
/// on a dynamic-refresh panel); stopped, it releases the boost and sleeps on an event: zero idle cost.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    private const int TimingInfoSize = 292, RefreshPeriodOffset = 12, VBlankOffset = 28;
    private const uint StatusSuccess = 0;

    private readonly UiPump _pump;
    private readonly ManualResetEventSlim _running = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private bool _compositorClock = true;
    private bool _boosted;
    private bool _boostFailureLogged;
    private bool _boostAvailable = true;

    public FrameClock(UiPump pump)
    {
        _pump = pump;
        _thread = new Thread(Run) { IsBackground = true, Name = "TrackSwipe frame clock", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public bool IsRunning => _running.IsSet;

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
        while (!_disposed)
        {
            if (!_running.IsSet)
            {
                Boost(false);
                _running.Wait();
                continue;
            }
            Boost(true);
            WaitForNextFrame();
            if (_running.IsSet && !_disposed) _pump.RunFrame(double.NaN); // the frame asks the compositor when it will show
        }
        Boost(false);
    }

    private void WaitForNextFrame()
    {
        if (_compositorClock)
        {
            try
            {
                // This API returns NTSTATUS, not the WAIT_* codes used by WaitForMultipleObjects.
                var status = NativeMethods.DCompositionWaitForCompositorClock(0, IntPtr.Zero, 100);
                if (status == StatusSuccess) return;
                if (status == 0x00000102 /* STATUS_TIMEOUT */ || status == 0xC01E0006 /* STATUS_GRAPHICS_PRESENT_OCCLUDED */)
                {
                    Thread.Sleep(8);
                    return;
                }
                Log.Write($"Compositor clock failed: 0x{status:X8}.");
            }
            catch (EntryPointNotFoundException)
            {
            }
            _compositorClock = false; // Windows 10: fall back to DWM's composition pacing
            Log.Write("Compositor clock unavailable; pacing frames with DwmFlush.");
        }
        if (NativeMethods.DwmFlush() != 0) Thread.Sleep(8);
    }

    /// <summary>Asks Windows to run the display at its top refresh rate while we animate, and to stop afterwards.</summary>
    private void Boost(bool enable)
    {
        if (_boosted == enable || !_boostAvailable || (enable && !_compositorClock)) return;
        try
        {
            var hr = NativeMethods.DCompositionBoostCompositorClock(enable);
            if (hr < 0)
            {
                if (!_boostFailureLogged) Log.Write($"Compositor refresh boost failed: 0x{hr:X8}.");
                _boostFailureLogged = true;
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
            _boostAvailable = false;
            Log.Write("Compositor refresh boost unavailable.");
            return;
        }
        _boosted = enable;
    }

    /// <summary>
    /// The next vblank, in <see cref="Clock"/> seconds: when the frame being built will start to show. Only a
    /// fallback: frames normally take DirectComposition's own estimate, so this isn't queried every frame.
    /// </summary>
    public static unsafe double PredictPresentTime()
    {
        var now = Stopwatch.GetTimestamp();
        var timing = stackalloc byte[TimingInfoSize];
        *(int*)timing = TimingInfoSize;
        if (NativeMethods.DwmGetCompositionTimingInfo(IntPtr.Zero, (IntPtr)timing) != 0) return Clock.Now;
        var period = *(long*)(timing + RefreshPeriodOffset);
        var vblank = *(long*)(timing + VBlankOffset);
        if (period <= 0 || vblank <= 0) return Clock.Now;
        var refreshes = Math.Max(1, (long)Math.Ceiling((now - vblank) / (double)period));
        return (vblank + refreshes * period) / (double)Stopwatch.Frequency;
    }
}

/// <summary>Builds a verbose log message only when verbose logging is on (see <see cref="Log.Verbose(ref VerboseMessage)"/>).</summary>
[InterpolatedStringHandler]
internal ref struct VerboseMessage
{
    private DefaultInterpolatedStringHandler _text;

    public VerboseMessage(int literalLength, int formattedCount, out bool enabled)
    {
        Enabled = enabled = Log.VerboseEnabled;
        _text = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public bool Enabled { get; }
    public void AppendLiteral(string value) => _text.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _text.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _text.AppendFormatted(value, alignment, format);
    public void AppendFormatted(string? value) => _text.AppendFormatted(value);
    public string ToStringAndClear() => _text.ToStringAndClear();
}
