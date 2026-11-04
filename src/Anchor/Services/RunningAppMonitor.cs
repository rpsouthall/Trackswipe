using Anchor.Interop;
using Anchor.Models;
using Microsoft.UI.Dispatching;

namespace Anchor.Services;

/// <summary>
/// Keeps track of which apps have a window open — pinned or not — and focuses, minimizes or
/// closes those windows.
/// <para>
/// Windows announces every window shown, hidden, destroyed, cloaked or uncloaked (WinEvents), so
/// an app appears in the dock as soon as its first window does and leaves as soon as its last one
/// goes; a slow poll backs that up for anything the events miss, such as a window gaining its
/// title. The walk itself (<see cref="RunningAppService.Snapshot"/>) runs off the UI thread and
/// the result is published back on it. One monitor serves every dock — the answer is a property
/// of the machine, not of a strip.
/// </para>
/// </summary>
public sealed class RunningAppMonitor : IDisposable
{
    // The fallback poll: the events carry the work, so this only has to catch what they don't
    // (chiefly a window that was shown untitled and named itself later). A walk is ~2 ms.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // A burst of window events (an app opening shows a dozen surfaces) becomes one walk.
    private static readonly TimeSpan EventSettle = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherQueue _dispatcher;
    private readonly WindowEvents _events;
    private DispatcherQueueTimer? _timer, _settleTimer;
    private IReadOnlyList<RunningApp> _apps = Array.Empty<RunningApp>();
    private Dictionary<string, RunningApp> _byExe = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RunningApp> _byAppId = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RunningApp> _byName = new(StringComparer.CurrentCultureIgnoreCase);
    private HashSet<nint> _appWindows = new();
    private bool _refreshInFlight, _refreshAgain;

    /// <summary>Raised on the UI thread after each successful refresh.</summary>
    public event Action? Updated;

    public RunningAppMonitor(DispatcherQueue dispatcher, WindowEvents events)
    {
        _dispatcher = dispatcher;
        _events = events;
    }

    public bool IsEnabled { get; private set; }

    /// <summary>Every open app, most recently used first.</summary>
    public IReadOnlyList<RunningApp> Apps => _apps;

    /// <summary>Starts or stops watching. Turning it off clears the last snapshot.</summary>
    public void SetEnabled(bool enabled)
    {
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;

        if (!enabled)
        {
            _timer?.Stop();
            _settleTimer?.Stop();
            _events.WindowChanged -= OnWindowChanged;
            Publish(Array.Empty<RunningApp>());
            return;
        }

        _timer ??= CreateTimer(PollInterval, repeat: true);
        _timer.Start();
        _events.WindowChanged += OnWindowChanged;
        Refresh(); // don't make the first frame wait for its dots
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, bool repeat)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeat;
        timer.Tick += (_, _) => Refresh();
        return timer;
    }

    /// <summary>
    /// A top-level window came or went. Most such windows (tooltips, menus, popups, the shell's own)
    /// can never be an app's, so they're dismissed here with a check or two rather than costing a
    /// walk of every window: one that appears must look like an app window, and one that goes must
    /// have been one of the app windows already known.
    /// </summary>
    private void OnWindowChanged(nint hwnd, uint eventType)
    {
        bool relevant = eventType is NativeMethods.EVENT_OBJECT_SHOW or NativeMethods.EVENT_OBJECT_UNCLOAKED
            ? CouldBeAppWindow(hwnd)
            : _appWindows.Contains(hwnd);
        if (!relevant)
            return;
        _settleTimer ??= CreateTimer(EventSettle, repeat: false);
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private static bool CouldBeAppWindow(nint hwnd)
    {
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_APPWINDOW) != 0)
            return true;
        return (ex & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) == 0 &&
               NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == nint.Zero;
    }

    private void Refresh()
    {
        if (!IsEnabled)
            return;
        if (_refreshInFlight)
        {
            _refreshAgain = true; // something changed while the walk ran; it may have missed it
            return;
        }
        _refreshInFlight = true;

        // The enumeration touches every top-level window and opens a handle per owning process.
        // That is fast, but it is not something to do on the thread that draws the dock.
        _ = Task.Run(() =>
        {
            Probe.Count("snapshot");
            var snapshot = RunningAppService.Snapshot();
            _dispatcher.TryEnqueue(() =>
            {
                _refreshInFlight = false;
                if (!IsEnabled)
                    return;
                Publish(snapshot);
                if (_refreshAgain)
                {
                    _refreshAgain = false;
                    Refresh();
                }
            });
        });
    }

    private void Publish(IReadOnlyList<RunningApp> apps)
    {
        // The same apps as last time (their windows' order changes with every click, which no
        // dock shows) is nothing to tell the docks about; just keep the fresh windows for clicks.
        bool sameApps = apps.Count == _apps.Count &&
            apps.Select(app => app.Key).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_apps.Select(app => app.Key));

        var byExe = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        var byAppId = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, RunningApp>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var app in apps)
        {
            if (app.ExePath is not null)
                byExe.TryAdd(app.ExePath, app);
            if (app.AppId is not null)
                byAppId.TryAdd(app.AppId, app);
            byName.TryAdd(app.Name, app);
        }
        (_apps, _byExe, _byAppId, _byName) = (apps, byExe, byAppId, byName);
        _appWindows = apps.SelectMany(app => app.Windows).ToHashSet();
        if (sameApps && apps.Count > 0)
            return;
        try
        {
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            Diag.Log("RunningAppMonitor: update handler failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The open app this item starts, if it is open: matched on its Start menu AppUserModelID or
    /// its exe, or failing both, on its name.
    /// <para>
    /// The name is for the launchers Windows keeps for apps that moved to the Store:
    /// <c>System32\notepad.exe</c> and <c>calc.exe</c> start the packaged Notepad and Calculator,
    /// whose windows belong to a different exe entirely, so an icon pinned from either would
    /// otherwise never light up — and the app would turn up a second time among the open ones.
    /// </para>
    /// </summary>
    public RunningApp? Find(DockItem item)
    {
        if (!IsEnabled || item.Kind != DockItemKind.Application)
            return null;
        if (item.IsTransient && item.RunningAppKey is { } key)
            return _apps.FirstOrDefault(app => string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase));

        RunningApp? found = null;
        if (RunningAppService.AppIdOf(item) is { } appId)
            found = _byAppId.GetValueOrDefault(appId);
        else if (RunningAppService.ExecutablePathOf(item) is { } exe)
            found = _byExe.GetValueOrDefault(exe);
        return found ?? (string.IsNullOrWhiteSpace(item.DisplayName) ? null : _byName.GetValueOrDefault(item.DisplayName.Trim()));
    }

    /// <summary>True when this item's app has a window open right now.</summary>
    public bool IsRunning(DockItem item) => Find(item) is not null;

    /// <summary>
    /// If this item's app is already open, handles the click and reports true so the caller skips
    /// the launch: an app that was in front when the dock was clicked is minimized, as the
    /// taskbar does; any other is brought forward. False for everything else — a not-running app,
    /// a folder or link, a window that has closed since the last refresh, or an activation
    /// Windows refused.
    /// </summary>
    public bool TryFocus(DockItem item)
    {
        if (Find(item) is not { } app)
            return false;

        if (WasInFront(app))
        {
            RunningAppService.Minimize(app);
            return true;
        }

        foreach (var hwnd in app.Windows)
        {
            // On another desktop, TrackSwipe (if running) turns the switch this causes into its slide.
            TrackSwipeBridge.PrepareJumpTo(hwnd);
            if (RunningAppService.Activate(hwnd))
                return true;
        }

        // Every window went away between refreshes; look again so the next click is right too,
        // and let this one launch.
        Refresh();
        return false;
    }

    /// <summary>Closes the item's app, window by window, as its own close buttons would.</summary>
    public void Quit(DockItem item)
    {
        if (Find(item) is { } app)
            RunningAppService.Quit(app);
    }

    /// <summary>Ends the item's app outright (Task Manager's End task), for one that won't close.</summary>
    public void EndTask(DockItem item)
    {
        if (Find(item) is { } app)
            RunningAppService.EndTask(app);
    }

    /// <summary>Whether one of this app's windows was active before the dock took the click.</summary>
    private bool WasInFront(RunningApp app)
    {
        nint front = NativeMethods.GetForegroundWindow();
        if (front == nint.Zero ||
            (NativeMethods.GetWindowThreadProcessId(front, out uint pid) != 0 && pid == Environment.ProcessId))
            front = _events.Foreground;
        if (front == nint.Zero || NativeMethods.IsIconic(front))
            return false;
        // An app's dialog counts as the app: compare the window it belongs to.
        nint root = NativeMethods.GetAncestor(front, 3 /* GA_ROOTOWNER */);
        return app.Windows.Contains(front) || (root != nint.Zero && app.Windows.Contains(root));
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _settleTimer?.Stop();
        _settleTimer = null;
        _events.WindowChanged -= OnWindowChanged;
        IsEnabled = false;
    }
}
