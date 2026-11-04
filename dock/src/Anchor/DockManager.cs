using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;

namespace Anchor;

/// <summary>
/// Owns everything there is exactly one of, however many docks are on screen: the configuration
/// file, the notification-area icon, the global shortcut, the running-app poll, and the Settings
/// and Add windows. It creates a <see cref="DockWindow"/> per <see cref="DockProfile"/> and keeps
/// the two lists in step.
/// <para>
/// This exists because those things are app-wide but used to live on the single dock window. With
/// more than one dock, "the dock that owns the tray icon" would be an arbitrary choice that
/// breaks the moment that dock is the one you remove — so they moved up here instead, and a
/// <see cref="DockWindow"/> is now purely one strip and its own placement.
/// </para>
/// </summary>
public sealed class DockManager
{
    private readonly List<DockWindow> _docks = new();

    private MessageWindow? _messageWindow;
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private AddNewWindow? _addNewWindow;
    private EditWindow? _editWindow;
    private NewGroupWindow? _newGroupWindow;
    private SearchWindow? _searchWindow;

    /// <summary>True while the user has hidden the docks from the tray menu. Distinct from
    /// auto-hide: they stay gone until explicitly summoned back.</summary>
    private bool _hiddenByUser;

    private bool _shuttingDown;

    public DockManager(DockConfig config)
    {
        Config = config;
        Events = new WindowEvents();
        Running = new RunningAppMonitor(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), Events);
        Running.Updated += ApplyRunningState;
    }

    public DockConfig Config { get; }

    /// <summary>Which pinned apps are open, shared by every dock.</summary>
    public RunningAppMonitor Running { get; }

    /// <summary>Other apps' windows coming, going and moving: the hooks every dock and the monitor share.</summary>
    public WindowEvents Events { get; }

    public IReadOnlyList<DockWindow> Docks => _docks;

    /// <summary>Raised when a dock is added or removed, so open windows can rebuild their lists.</summary>
    public event Action? DocksChanged;

    /// <summary>Raised whenever any dock's item set changes (add / remove / hide / reorder).</summary>
    public event Action? ItemsChanged;

    // ---- Startup / shutdown ------------------------------------------------

    /// <summary>Creates a window for every configured dock, then wires up the tray and shortcut.</summary>
    public void Start()
    {
        bool firstRun = !Config.Seeded;
        Config.Seeded = true;

        // TrackSwipe's dock starts the way a Mac's does: along the bottom, out of the way of the window in front
        // (auto-hide that dodges, which only applies to a dock snapped to an edge), rather than floating.
        if (firstRun && Hosting.ByTrackSwipe)
        {
            foreach (var profile in Config.Docks)
            {
                profile.Snapped = true;
                profile.Edge = DockEdge.Bottom;
                profile.AutoHide = true;
                profile.DodgeWindows = true;
            }
        }

        // Settle the geometry before the first window is built: the item template binds to it,
        // and a dock that lays out at the default density and then re-sizes reads as a flicker.
        DockMetrics.SetDensity(Config.Density);
        DockItemAnimations.SetShowLabels(Config.ShowItemLabels);

        foreach (var profile in Config.Docks.ToList())
            TryCreateAndAddWindow(profile, seedDefaults: firstRun && profile.Items.Count == 0);

        // A dock is a tool window with no taskbar button, so the tray icon is the only always-
        // available handle on a running Anchor — and, with the global shortcut, the way back to a
        // dock that is tucked behind a screen edge.
        SetUpTrayAndHotkey();
        Running.SetEnabled(Config.ShowRunningIndicators || Config.ShowRunningApps);
        // Which AppUserModelID each program's jump list is filed under, for right-click menus: read
        // once, in the background, well after the dock is up.
        StartMenuApps.Warm(TimeSpan.FromSeconds(5));

        foreach (var dock in _docks)
            dock.Activate();

        // Materialize the default dock on first run, and rewrite a config that was still in the
        // pre-multi-dock shape — once, here, rather than re-converting it on every launch until
        // the user happens to change something.
        if (firstRun || Config.WasMigrated)
            Save();

        // Strictly opt-in, and deliberately last: nothing above it touches the network, and a
        // slow or unreachable GitHub must never delay the dock appearing. A packaged copy skips it
        // outright — the Store updates itself (see UpdateChecksSupported).
        if (Config.CheckForUpdates && UpdateChecksSupported)
            _ = CheckForUpdatesAsync(promptOnly: true);
    }

    private DockWindow CreateWindow(DockProfile profile, bool seedDefaults)
    {
        var window = new DockWindow(this, profile, seedDefaults);
        // A dock closed by any means (its own menu, a crash in its content) must not leave a
        // stale entry behind that the tray and Settings still think exists.
        // Weakly, not a closure over the local: a strong reference from a handler on the window's own
        // event is held by the native window for the life of the process, which would keep every
        // closed dock alive (see WindowEventLifetimeTests).
        var weak = new WeakReference<DockWindow>(window);
        window.Closed += (_, _) =>
        {
            if (weak.TryGetTarget(out var closed))
                _docks.Remove(closed);
        };
        return window;
    }

    /// <summary>
    /// Creates a dock window for <paramref name="profile"/> and adds it to <see cref="_docks"/>,
    /// but never lets a single bad profile take the rest of the app with it.
    /// <para>
    /// <see cref="DockStore.Load"/> already sanitizes the shapes a corrupt <c>dock.json</c> is
    /// known to produce, but this runs before <see cref="App"/>'s own
    /// <c>UnhandledException</c> handler has any window to blame the failure on — an exception
    /// here would otherwise propagate straight out of <c>OnLaunched</c> and crash the process on
    /// every subsequent launch, since the same profile is still first in the list next time. One
    /// dock failing to build is recoverable (the rest still come up, and Settings ▸ Docks can
    /// still remove the bad one); an unguarded crash loop before any window exists is not.
    /// </para>
    /// </summary>
    private void TryCreateAndAddWindow(DockProfile profile, bool seedDefaults)
    {
        try
        {
            _docks.Add(CreateWindow(profile, seedDefaults));
        }
        catch (Exception ex)
        {
            Diag.Log($"DockManager: failed to create a window for dock '{profile.Id}' — skipping it: {ex}");
        }
    }

    public void Save() => DockStore.Save(Config);

    /// <summary>
    /// Removes the tray icon and releases the global shortcut. Idempotent, so any shutdown path
    /// can call it without checking whether another already has — the shell leaves a dead icon
    /// behind (until it is next hovered) if the process goes away without a <c>NIM_DELETE</c>.
    /// </summary>
    public void ReleaseShellIntegration()
    {
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _messageWindow?.Dispose();
        _tray = null;
        _hotkeys = null;
        _messageWindow = null;
    }

    /// <summary>Shuts Anchor down cleanly: shell integration first, then the app.</summary>
    public void Quit()
    {
        _shuttingDown = true;
        Running.Dispose();
        Events.Dispose();
        _launchZoom?.Dispose();
        ReleaseShellIntegration();
        Application.Current.Exit();
    }

    /// <summary>
    /// Relaunches Anchor and shuts this instance down. Some things — the acrylic theme switch
    /// chief among them — don't always finish repainting live, so this is the reliable escape
    /// hatch: a fresh process always renders clean.
    /// <para>
    /// The packaged (MSIX) build asks the OS to do it via <c>RequestRestartAsync</c>, the
    /// supported path there, which handles terminating and relaunching itself. The portable exe
    /// starts its own replacement and exits — releasing the single-instance lock first, or the new
    /// copy would find it still held and immediately bow out (see
    /// <see cref="App.ReleaseInstanceLock"/>).
    /// </para>
    /// </summary>
    public async void Restart()
    {
        _shuttingDown = true;
        Running.Dispose();
        Events.Dispose();
        ReleaseShellIntegration();

        if (PackagedRuntime.IsPackaged)
        {
            App.ReleaseInstanceLock();
            // On success the OS terminates this process before the call returns; any return
            // value here is therefore always a failure reason.
            var result = await Windows.ApplicationModel.Core.CoreApplication.RequestRestartAsync(string.Empty);
            Diag.Log("Restart: RequestRestartAsync did not relaunch — " + result);
            Application.Current.Exit();
            return;
        }

        try
        {
            if (Environment.ProcessPath is { Length: > 0 } exe)
            {
                App.ReleaseInstanceLock();
                System.Diagnostics.Process.Start(exe);
            }
            else
            {
                Diag.Log("Restart: failed to relaunch — Environment.ProcessPath was unavailable");
            }
        }
        catch (Exception ex)
        {
            Diag.Log("Restart: failed to relaunch — " + ex.Message);
        }
        Application.Current.Exit();
    }

    private void SetUpTrayAndHotkey()
    {
        try
        {
            _messageWindow = new MessageWindow();
            // TrackSwipe's Mission Control: keep every dock in view for as long as it is open, as the
            // Mac's Dock stays through Mission Control (TrackSwipe keeps its windows clear of it).
            _messageWindow.MessageReceived += (msg, wParam, lParam) =>
            {
                if (msg == TrackSwipeBridge.MissionControlMessage)
                {
                    foreach (var dock in _docks)
                        dock.HoldInView(wParam != 0);
                }
                else if (msg == Hosting.AppMenuMessage)
                {
                    ShowAppMenu(wParam, (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                }
                else if (msg == Hosting.CommandMessage && Hosting.ByTrackSwipe)
                {
                    // TrackSwipe's menu: turning the dock off, its settings, showing it.
                    switch ((int)wParam)
                    {
                        case Hosting.QuitCommand: Quit(); break;
                        case Hosting.SettingsCommand: OpenSettings(); break;
                        case Hosting.ShowCommand: BringToFront(); break;
                    }
                }
            };

            // TrackSwipe's own notification-area icon is the way in to a dock it hosts; a second
            // icon for the same app would be one too many.
            if (Hosting.ByTrackSwipe)
            {
                _hotkeys = new HotkeyService(_messageWindow);
                _hotkeys.Pressed += OnHotkeyPressed;
                ApplyHotkey();
                ApplySearchHotkey();
                ApplyItemHotkeys();
                return;
            }

            _tray = new TrayIconService(_messageWindow)
            {
                IsDockVisible = () => !_hiddenByUser,
            };
            _tray.Activated += BringToFront;
            _tray.ShowHideRequested += ToggleVisibility;
            _tray.AddNewRequested += () => OpenAddNew();
            _tray.SettingsRequested += OpenSettings;
            _tray.SearchRequested += OpenSearch;
            _tray.QuitRequested += Quit;
            _tray.HasUpdate = () => PendingUpdate is not null;
            // Settings shows the release as a banner on its General page; opening it is the whole
            // action, since Anchor never downloads or installs anything itself.
            _tray.UpdateRequested += () =>
            {
                OpenSettings();
                if (PendingUpdate is { } release)
                    UpdateAvailable?.Invoke(release);
            };

            _hotkeys = new HotkeyService(_messageWindow);
            _hotkeys.Pressed += OnHotkeyPressed;
            ApplyHotkey();
            ApplySearchHotkey();
            ApplyItemHotkeys();
        }
        catch (Exception ex)
        {
            // Anchor running without its tray icon is degraded, not broken — never let this take
            // the app down at startup.
            Diag.Log("Tray/hotkey setup failed: " + ex);
        }
    }

    // ---- Adding and removing docks ----------------------------------------

    /// <summary>
    /// Adds a dock. With no <paramref name="displayIndex"/> it lands on the monitor under the
    /// cursor; otherwise on the display at that index in <see cref="Displays"/>. New docks start
    /// floating and empty, so they are visible and obviously new rather than tucked behind an
    /// edge somewhere the user has to go looking for.
    /// </summary>
    public DockWindow AddDock(int? displayIndex = null)
    {
        // Name is left empty rather than baked in here: LabelFor derives the positional fallback
        // ("Dock 2") on demand, so it keeps renumbering correctly as other docks are added or
        // removed. Stamping the computed name in now would freeze it as if the user had typed it.
        var profile = new DockProfile();
        PlaceOnDisplay(profile, displayIndex ?? DisplayIndexUnderCursor());

        Config.Docks.Add(profile);
        var window = CreateWindow(profile, seedDefaults: false);
        _docks.Add(window);
        Save();

        window.Activate();
        DocksChanged?.Invoke();
        return window;
    }

    /// <summary>
    /// Removes a dock and everything pinned to it. Refuses to remove the last one: Anchor with no
    /// dock is a tray icon and nothing else, and there would be no obvious way back.
    /// </summary>
    public bool RemoveDock(DockProfile profile)
    {
        if (Config.Docks.Count <= 1 || !Config.Docks.Remove(profile))
            return false;

        // Take the window down before saving, so a failure to close can't leave the config
        // claiming a dock that is still on screen.
        var window = _docks.FirstOrDefault(d => d.Profile == profile);
        if (window is not null)
        {
            _docks.Remove(window);
            window.Close();
        }

        Save();
        DocksChanged?.Invoke();
        ItemsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Back to a first-run dock: every extra dock is closed and the remaining one is emptied and
    /// re-seeded. App-wide settings (theme, language, shortcut) are deliberately left alone — the
    /// command is "reset the dock", and losing your language on the way to a clean dock would be
    /// a surprise.
    /// </summary>
    public void ResetToDefaults()
    {
        foreach (var profile in Config.Docks.Skip(1).ToList())
            RemoveDock(profile);

        PrimaryWindow?.ResetToDefaults();
        Save();
        DocksChanged?.Invoke();
    }

    public DockWindow? WindowFor(DockProfile profile) =>
        _docks.FirstOrDefault(d => d.Profile == profile);

    /// <summary>The dock app-wide actions fall back to — the first one still on screen.</summary>
    public DockWindow? PrimaryWindow => _docks.Count > 0 ? _docks[0] : null;

    // ---- Monitors ----------------------------------------------------------

    /// <summary>
    /// Every display, in the order Windows enumerates them.
    /// <para>
    /// Copied out by index rather than with LINQ: <c>DisplayArea.FindAll()</c> hands back a WinRT
    /// vector view whose <c>IEnumerable&lt;T&gt;</c> projection throws
    /// <see cref="InvalidCastException"/> — so <c>.ToList()</c> on it takes the Settings window
    /// down as it builds the Docks page. Indexing works fine.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Microsoft.UI.Windowing.DisplayArea> Displays
    {
        get
        {
            try
            {
                var found = Microsoft.UI.Windowing.DisplayArea.FindAll();
                var list = new List<Microsoft.UI.Windowing.DisplayArea>(found.Count);
                for (int i = 0; i < found.Count; i++)
                    list.Add(found[i]);
                return list;
            }
            catch (Exception ex)
            {
                // Losing the monitor list costs the Docks page its monitor picker; it must not
                // cost the user the whole Settings window.
                Diag.Log("DockManager: could not enumerate displays: " + ex.Message);
                return Array.Empty<Microsoft.UI.Windowing.DisplayArea>();
            }
        }
    }

    private static int DisplayIndexUnderCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var p))
                return 0;
            var under = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(p.X, p.Y),
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var all = Displays;
            for (int i = 0; i < all.Count; i++)
                if (all[i].DisplayId.Value == under.DisplayId.Value)
                    return i;
        }
        catch (Exception ex)
        {
            Diag.Log("DockManager: could not resolve the display under the cursor: " + ex.Message);
        }
        return 0;
    }

    /// <summary>
    /// Points a profile at a display by writing coordinates on it. That is all a dock's "monitor"
    /// is (see <see cref="DockProfile"/>): the display its stored position falls on.
    /// </summary>
    public static void PlaceOnDisplay(DockProfile profile, int displayIndex)
    {
        var all = Displays;
        if (all.Count == 0)
            return;
        var work = all[Math.Clamp(displayIndex, 0, all.Count - 1)].WorkArea;

        // Roughly centred: the exact size isn't known until the window lays out, and
        // UpdateSizeAndPosition clamps whatever lands outside the work area anyway.
        profile.FreeX = work.X + work.Width / 2 - 180;
        profile.FreeY = work.Y + work.Height / 2 - 48;
    }

    /// <summary>Which display a profile currently sits on, as an index into <see cref="Displays"/>.</summary>
    public static int DisplayIndexOf(DockProfile profile)
    {
        var all = Displays;
        if (profile.FreeX is not int x || profile.FreeY is not int y || all.Count == 0)
            return 0;
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(x, y),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            for (int i = 0; i < all.Count; i++)
                if (all[i].DisplayId.Value == area.DisplayId.Value)
                    return i;
        }
        catch (Exception ex)
        {
            Diag.Log("DockManager: could not resolve a dock's display: " + ex.Message);
        }
        return 0;
    }

    /// <summary>Moves a dock onto another monitor and re-lays it out there.</summary>
    public void MoveDockToDisplay(DockProfile profile, int displayIndex)
    {
        PlaceOnDisplay(profile, displayIndex);
        Save();
        WindowFor(profile)?.RelayoutAfterExternalMove();
    }

    // ---- Summoning ---------------------------------------------------------

    /// <summary>
    /// Brings every dock into view and to the front: un-hides them if the tray menu hid them,
    /// cancels an auto-hide slide, re-asserts top-most Z-order and focuses the first. This is
    /// what the tray icon's click and the global shortcut do.
    /// </summary>
    public void BringToFront()
    {
        if (_hiddenByUser)
        {
            _hiddenByUser = false;
            foreach (var dock in _docks)
                dock.ShowAfterUserHide();
        }

        // Only one window can hold focus, so the rest are revealed without stealing it.
        for (int i = 0; i < _docks.Count; i++)
            _docks[i].BringToFront(takeFocus: i == 0);
    }

    /// <summary>Tray menu "Show dock" / "Hide dock" — applies to every dock at once.</summary>
    public void ToggleVisibility()
    {
        if (_hiddenByUser)
        {
            BringToFront();
            return;
        }

        _hiddenByUser = true;
        foreach (var dock in _docks)
            dock.HideByUser();
    }

    // ---- Child windows -----------------------------------------------------

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Opens the Add window, adding to <paramref name="target"/> — or, with none given, to the
    /// first dock. With several docks the caller almost always knows which one it meant (the one
    /// whose menu was used); the fallback is only for the tray, which belongs to no dock.
    /// </summary>
    public void OpenAddNew(DockWindow? target = null)
    {
        target ??= PrimaryWindow;
        if (target is null)
            return;

        if (_addNewWindow is not null)
        {
            _addNewWindow.Activate();
            return;
        }
        _addNewWindow = new AddNewWindow(this, target);
        _addNewWindow.Closed += (_, _) => _addNewWindow = null;
        _addNewWindow.Activate();
    }

    /// <summary>
    /// Opens the editor for one pinned item — its name, target and icon — from an icon's own menu
    /// or from a row in Settings ▸ Apps &amp; links.
    /// <para>
    /// One editor at a time. Asking for the item already being edited brings that window forward
    /// rather than opening a second copy of it; asking for a different one replaces it, since two
    /// identical-looking windows editing different items is a trap.
    /// </para>
    /// </summary>
    public void OpenItemEditor(DockWindow dock, DockItem item)
    {
        if (_editWindow is { } open)
        {
            if (ReferenceEquals(open.Item, item))
            {
                open.Activate();
                return;
            }
            _editWindow = null;
            open.Close();
        }

        var window = new EditWindow(this, dock, item);
        _editWindow = window;
        // Guarded: a window replaced above closes after its successor is already the current one,
        // and must not null it out on the way past.
        var weak = new WeakReference<EditWindow>(window);
        window.Closed += (_, _) =>
        {
            if (weak.TryGetTarget(out var closed) && ReferenceEquals(_editWindow, closed))
                _editWindow = null;
        };
        window.Activate();
    }

    /// <summary>
    /// Opens the "create a group" window, adding to <paramref name="dock"/>. Given
    /// <paramref name="pendingItem"/> (opened via an item's "Move to group ▸ New group…"), that
    /// item is filed into the group the moment it's created.
    /// <para>
    /// One at a time, like every other child window here: asking again just brings the form
    /// already open back to the front rather than stacking a second one beside it.
    /// </para>
    /// </summary>
    public void OpenNewGroupWindow(DockWindow dock, DockItem? pendingItem)
    {
        if (_newGroupWindow is { } open)
        {
            if (ReferenceEquals(open.PendingItem, pendingItem) && ReferenceEquals(open.Dock, dock))
            {
                open.Activate();
                return;
            }
            _newGroupWindow = null;
            open.Close();
        }
        _newGroupWindow = new NewGroupWindow(this, dock, pendingItem);
        _newGroupWindow.Closed += (_, _) => _newGroupWindow = null;
        _newGroupWindow.Activate();
    }

    /// <summary>
    /// Opens quick-launch search, or dismisses it if it is already up — the shortcut is a toggle,
    /// so the same keystroke that summoned the card puts it away again.
    /// </summary>
    public void OpenSearch()
    {
        if (_searchWindow is not null)
        {
            _searchWindow.Close();
            return;
        }

        _searchWindow = new SearchWindow(this);
        _searchWindow.Closed += (_, _) => _searchWindow = null;
        _searchWindow.Activate();
        _searchWindow.FocusQuery();
    }

    public void NotifyItemsChanged()
    {
        if (_shuttingDown)
            return;
        ItemsChanged?.Invoke();
        // A new or retargeted item may now match a running process; don't leave its dot until
        // the next poll tick.
        ApplyRunningState();
        // An item that was removed, moved or filed into a group must not leave a system-wide
        // combination registered to something that is no longer there.
        ApplyItemHotkeys();
    }

    /// <summary>
    /// Opens an item: brings an already-running app's window forward if there is one, otherwise
    /// launches it. Shared by the dock strip, the fly-out bars, quick-launch search and the
    /// per-item shortcuts, so "click it" and "press its shortcut" can't drift apart.
    /// </summary>
    /// <returns>True when it launched; false when it brought an open window forward instead.</returns>
    public bool LaunchOrFocus(DockItem item, bool forceNewInstance)
    {
        // A pinned app focuses its window only with the indicators on (the switch promises
        // "click focuses instead of relaunching"); an open app's own icon always does — that is
        // all it is there for.
        if (!forceNewInstance && (item.IsTransient || Config.ShowRunningIndicators) && Running.TryFocus(item))
            return false;
        Launcher.Launch(item);
        return true;
    }

    private LaunchZoom? _launchZoom;

    /// <summary>The window the launch animation plays in, shared by every dock and made on first use.</summary>
    public LaunchZoom LaunchZoom => _launchZoom ??=
        new LaunchZoom(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

    /// <summary>A dock's name, or a positional fallback ("Dock 2") when it hasn't been given one.
    /// The one place that decides what a dock is called, so the Settings pages, the "move to
    /// dock" menu and quick-launch search all agree.</summary>
    public string LabelFor(DockProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Name)
            ? Loc.Format("Docks.DefaultName", Config.Docks.IndexOf(profile) + 1)
            : profile.Name;

    // ---- App-wide settings -------------------------------------------------

    public void SetTheme(DockTheme theme)
    {
        Config.Theme = theme;
        Save();
        foreach (var dock in _docks)
            dock.ApplyTheme();
        _settingsWindow?.ApplyTheme(theme);
        _addNewWindow?.ApplyTheme(theme);
        _editWindow?.ApplyTheme(theme);
        _newGroupWindow?.ApplyTheme(theme);
    }

    /// <summary>
    /// Turns "start with Windows" on or off, and records what it actually became. On the packaged
    /// build Windows can refuse to re-enable an entry the user disabled themselves, so the config
    /// follows the returned state rather than the request — otherwise the Settings switch would
    /// claim an autostart that isn't going to happen.
    /// </summary>
    public async Task<StartupService.StartupState> SetLaunchAtStartupAsync(bool on)
    {
        var state = await StartupService.SetEnabledAsync(on);
        Config.LaunchAtStartup = state == StartupService.StartupState.Enabled;
        Save();
        return state;
    }

    public void SetShowRunningIndicators(bool on)
    {
        Config.ShowRunningIndicators = on;
        Save();
        Running.SetEnabled(Config.ShowRunningIndicators || Config.ShowRunningApps);
        ApplyRunningState();
    }

    /// <summary>Turns the macOS-style section of open, unpinned apps on or off.</summary>
    public void SetShowRunningApps(bool on)
    {
        Config.ShowRunningApps = on;
        Save();
        Running.SetEnabled(Config.ShowRunningIndicators || Config.ShowRunningApps);
        ApplyRunningState();
    }

    /// <summary>Changes the icon/cell size of every dock at once.</summary>
    public void SetDensity(DockDensity density)
    {
        Config.Density = density;
        DockMetrics.SetDensity(density);
        Save();
        foreach (var dock in _docks)
            dock.ApplyDensity();
    }

    /// <summary>Tints the glass with the Windows accent color, or back to the neutral default.</summary>
    public void SetAccentTint(bool on)
    {
        Config.AccentTint = on;
        Save();
        foreach (var dock in _docks)
            dock.ApplyGlass();
    }

    /// <summary>Turns the cursor-follows magnification on or off across every dock.</summary>
    public void SetMagnify(bool on)
    {
        Config.Magnify = on;
        Save();
        foreach (var dock in _docks)
            dock.ApplyMagnifySetting();
    }

    /// <summary>
    /// Switches every dock's group fly-outs between opening on hover and opening only on a click.
    /// Opening reads the setting live on the next pointer move, so only the closing side needs
    /// re-applying: a bar that a click opened before hover mode came on has no hover watch on it,
    /// and would otherwise go on ignoring the cursor until it was closed and opened again.
    /// </summary>
    public void SetGroupOpenOnHover(bool on)
    {
        Config.GroupOpenOnHover = on;
        Save();
        foreach (var dock in _docks)
            dock.ApplyGroupOpenOnHoverSetting();
    }

    /// <summary>Shows or hides every dock's gear; Settings stays on the right-click menu and the tray.</summary>
    public void SetShowSettingsButton(bool on)
    {
        Config.ShowSettingsButton = on;
        Save();
        foreach (var dock in _docks)
            dock.QueueRelayoutPublic(); // the layout pass shows or collapses the gear, and resizes for it
    }

    public void SetSettingsPosition(SettingsPosition position)
    {
        Config.SettingsPosition = position;
        Save();
        foreach (var dock in _docks)
        {
            dock.ApplyStripLayout();
            dock.QueueRelayoutPublic();
        }
    }

    public void SetShowItemLabels(bool on)
    {
        Config.ShowItemLabels = on;
        DockItemAnimations.SetShowLabels(on);
        Save();
        foreach (var dock in _docks)
            dock.RefreshItemLabels();
    }

    /// <summary>Turns the opt-in update check on or off. Nothing is contacted until it is on.</summary>
    public void SetCheckForUpdates(bool on)
    {
        Config.CheckForUpdates = on;
        Save();
    }

    // ---- Update check ------------------------------------------------------

    /// <summary>
    /// Whether Anchor checks GitHub for a newer release at all. False for the Microsoft Store
    /// build, where the whole feature is hidden rather than merely defaulted off.
    /// <para>
    /// Two reasons, and either would be enough. It is <i>redundant</i>: the Store updates a
    /// packaged app itself, so a banner offering a GitHub download would send someone to install a
    /// second, unmanaged copy of the app they already have. And it is a <i>policy risk</i>: a Store
    /// listing that routes users to a build distributed elsewhere is exactly the pattern Store
    /// review looks for. The portable zip has no such updater, which is why the feature exists
    /// there at all.
    /// </para>
    /// </summary>
    public static bool UpdateChecksSupported => !PackagedRuntime.IsPackaged && !Hosting.ByTrackSwipe; // hosted, it updates with TrackSwipe

    /// <summary>Raised when a release should be shown to the user. The Settings window listens
    /// and renders it as a banner on its General page.</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    /// <summary>
    /// The newer release the last check found, if any. Held so the startup check — which puts
    /// nothing on screen — can still be surfaced later, through the tray menu and through the
    /// Settings window whenever it is next opened.
    /// </summary>
    public ReleaseInfo? PendingUpdate { get; private set; }

    /// <summary>
    /// Asks GitHub whether there is a newer release. Silent about everything except success: a
    /// failed check is a non-event (see <see cref="UpdateService"/>), and a background check that
    /// popped an error dialog because the network was down would be worse than no check at all.
    /// </summary>
    /// <param name="promptOnly">True for the automatic check at startup, which honors a release
    /// the user chose to skip. A check the user asked for explicitly passes false, so it reports
    /// what is actually out there even if they skipped it before.</param>
    public async Task<ReleaseInfo?> CheckForUpdatesAsync(bool promptOnly)
    {
        var release = await UpdateService.CheckAsync(UpdateService.CurrentVersion);
        if (release is null)
            return null;

        if (promptOnly &&
            string.Equals(Config.SkippedUpdate, release.Version.ToString(), StringComparison.Ordinal))
            return null;

        PendingUpdate = release;
        UpdateAvailable?.Invoke(release);
        return release;
    }

    /// <summary>Remembers that the user doesn't want to be told about this release again.</summary>
    public void SkipUpdate(ReleaseInfo release)
    {
        Config.SkippedUpdate = release.Version.ToString();
        PendingUpdate = null;
        Save();
    }

    // ---- Import / export ---------------------------------------------------

    /// <summary>
    /// Writes the whole configuration to <paramref name="path"/> — every dock, every item and
    /// every app-wide setting, exactly as <c>dock.json</c> holds it.
    /// </summary>
    /// <returns>True on success; the reason for a failure goes to the log.</returns>
    public bool Export(string path) => DockStore.ExportTo(Config, path);

    /// <summary>
    /// Replaces the running configuration with the one in <paramref name="path"/>: every dock
    /// window is torn down and rebuilt from the imported profiles, and the result is saved.
    /// <para>
    /// Wholesale replacement rather than a merge, because the file is a backup of a dock, not a
    /// fragment of one — merging two configs would have to invent an answer for every item that
    /// exists in both, and "restore the dock I saved" is the thing people actually want.
    /// </para>
    /// </summary>
    /// <returns>False when the file could not be read as an Anchor configuration; the existing
    /// dock is left untouched in that case.</returns>
    public bool Import(string path)
    {
        var imported = DockStore.ImportFrom(path);
        if (imported is null)
            return false;

        Config.Docks.Clear();
        Config.Docks.AddRange(imported.Docks);
        Config.CopyAppSettingsFrom(imported);
        Save();

        // Re-apply everything app-wide the imported file may have changed, in the order Start()
        // applies it: geometry first (the rebuilt windows lay out against it), then the string
        // table, then the shortcuts. RebuildWindows takes the old windows down — they are backed
        // by DockProfiles that are no longer in the configuration.
        DockMetrics.SetDensity(Config.Density);
        Loc.Initialize(Config.Language);
        // Not awaited: an import rebuilds every window, and the startup entry is a side effect of
        // it rather than something the rebuild waits on. Failures are logged by the service.
        _ = StartupService.SetEnabledAsync(Config.LaunchAtStartup);
        Running.SetEnabled(Config.ShowRunningIndicators || Config.ShowRunningApps);

        RebuildWindows();

        ApplyHotkey();
        ApplySearchHotkey();
        ApplyItemHotkeys();
        ItemsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Persists the UI language, switches the string table over and puts it on screen immediately.
    /// <para>
    /// "Immediately" means every window is rebuilt in place, not that the loaded XAML re-reads its
    /// strings: <c>{loc:Localize}</c> resolves once, as the XAML loads, and re-resolving it at
    /// runtime would mean every translated property in the app becoming a live binding to an
    /// observable table — a great deal of machinery for something a window can do by being built
    /// again. Rebuilding costs a frame and keeps every dock's items, position and snap state
    /// (those live on the <see cref="DockProfile"/>s, which are untouched here), which is why this
    /// replaced the restart prompt that used to follow a language change.
    /// </para>
    /// </summary>
    public void SetLanguage(string code)
    {
        Config.Language = code ?? string.Empty;
        Save();
        Loc.Initialize(Config.Language);
        _tray?.RefreshTooltip(); // the tray menu is built per-open, so it needs nothing else
        RebuildWindows();
    }

    /// <summary>
    /// Closes and re-creates every dock window against the same profiles. Used after a language
    /// change and after an import, both of which change things that are baked in as a window is
    /// built.
    /// </summary>
    private void RebuildWindows()
    {
        foreach (var window in _docks.ToList())
        {
            _docks.Remove(window);
            window.Close();
        }

        foreach (var profile in Config.Docks)
            TryCreateAndAddWindow(profile, seedDefaults: false);

        foreach (var dock in _docks)
            dock.Activate();

        // The Add, Edit and New-group windows are built from the string table too, and there is
        // nothing in any of them worth preserving across the change — all three are forms that
        // have not been submitted.
        _addNewWindow?.Close();
        _editWindow?.Close();
        _newGroupWindow?.Close();

        DocksChanged?.Invoke();
    }

    /// <summary>
    /// Re-opens the Settings window on <paramref name="page"/>. Settings itself calls this after
    /// changing something it was built from (the language, an imported config): the window has to
    /// be replaced, and it can't do that from inside its own event handler, so the close and the
    /// re-open are deferred to the next turn of the dispatcher.
    /// </summary>
    public void ReopenSettings(string page)
    {
        var window = _settingsWindow;
        if (window is null)
            return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            _settingsWindow = null;
            window.Close();

            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
            _settingsWindow.Navigate(page);
        });
    }

    // ---- Global shortcuts --------------------------------------------------
    //
    // Three kinds share one HotkeyService, keyed by id: the shortcut that summons every dock, the
    // one that opens quick-launch search, and one per item that has been given one. Item ids are
    // allocated from a base and rebuilt whenever the item set changes, so an item that is removed
    // takes its registration with it rather than leaving a combination held by nothing.

    private const int SummonHotkeyId = 0x4143; // 'A','C'
    private const int SearchHotkeyId = 0x4144;
    private const int FirstItemHotkeyId = 0x4200;

    private readonly Dictionary<int, DockItem> _itemHotkeys = new();

    private void OnHotkeyPressed(int id)
    {
        if (id == SummonHotkeyId)
            BringToFront();
        else if (id == SearchHotkeyId)
            OpenSearch();
        else if (_itemHotkeys.TryGetValue(id, out var item))
            LaunchOrFocus(item, forceNewInstance: false);
    }

    /// <summary>The configured summon shortcut, or null when none is set or it can't be parsed.</summary>
    public HotkeyGesture? ConfiguredHotkey =>
        HotkeyGesture.TryParse(Config.Hotkey, out var gesture) ? gesture : null;

    /// <summary>The configured quick-launch-search shortcut, or null when there isn't one.</summary>
    public HotkeyGesture? ConfiguredSearchHotkey =>
        HotkeyGesture.TryParse(Config.SearchHotkey, out var gesture) ? gesture : null;

    /// <summary>Registers (or releases) the summon shortcut to match the current config.</summary>
    /// <returns>False when the shortcut is enabled and valid but Windows refused it — i.e.
    /// another app already owns that combination.</returns>
    public bool ApplyHotkey()
    {
        if (_hotkeys is null)
            return true;
        if (!Config.HotkeyEnabled || ConfiguredHotkey is not { } gesture)
        {
            _hotkeys.Unregister(SummonHotkeyId);
            return true;
        }
        return _hotkeys.Register(SummonHotkeyId, gesture);
    }

    /// <summary>Registers (or releases) the quick-launch-search shortcut.</summary>
    public bool ApplySearchHotkey()
    {
        if (_hotkeys is null)
            return true;
        if (ConfiguredSearchHotkey is not { } gesture)
        {
            _hotkeys.Unregister(SearchHotkeyId);
            return true;
        }
        return _hotkeys.Register(SearchHotkeyId, gesture);
    }

    /// <summary>
    /// Re-registers every per-item shortcut from scratch: releases the previous set, then walks
    /// every dock (groups included — a shortcut is exactly how you reach an item that lives inside
    /// one without opening it first) registering the items that have one.
    /// <para>
    /// Rebuilt wholesale rather than diffed because the set changes on any add, remove, move,
    /// group or ungroup, and a stale registration is a system-wide combination held by an item
    /// that no longer exists.
    /// </para>
    /// </summary>
    /// <returns>Every item whose combination Windows refused, so the caller can say which.</returns>
    public IReadOnlyList<DockItem> ApplyItemHotkeys()
    {
        var refused = new List<DockItem>();
        if (_hotkeys is null)
            return refused;

        foreach (var id in _itemHotkeys.Keys.ToList())
            _hotkeys.Unregister(id);
        _itemHotkeys.Clear();

        if (!Config.ItemHotkeysEnabled)
            return refused;

        int nextId = FirstItemHotkeyId;
        foreach (var item in AllItems())
        {
            if (!HotkeyGesture.TryParse(item.Hotkey ?? string.Empty, out var gesture))
                continue;

            int id = nextId++;
            if (_hotkeys.Register(id, gesture))
                _itemHotkeys[id] = item;
            else
                refused.Add(item);
        }
        return refused;
    }

    /// <summary>Every item on every dock, a group's children included.</summary>
    public IEnumerable<DockItem> AllItems()
    {
        foreach (var profile in Config.Docks)
        {
            foreach (var item in profile.Items)
            {
                yield return item;
                foreach (var child in item.Children)
                    yield return child;
            }
        }
    }

    /// <summary>Persists a new summon shortcut (or clears it with null) and re-registers it.</summary>
    /// <returns>False if Windows refused the combination; the choice is still saved so the
    /// Settings UI can show what was attempted alongside the conflict message.</returns>
    public bool SetHotkey(HotkeyGesture? gesture)
    {
        Config.Hotkey = gesture?.ToString() ?? string.Empty;
        Save();
        return ApplyHotkey();
    }

    /// <summary>Turns the summon shortcut on or off without forgetting the combination.</summary>
    public bool SetHotkeyEnabled(bool on)
    {
        Config.HotkeyEnabled = on;
        Save();
        return ApplyHotkey();
    }

    /// <summary>Persists the quick-launch-search shortcut (null clears it) and re-registers it.</summary>
    public bool SetSearchHotkey(HotkeyGesture? gesture)
    {
        Config.SearchHotkey = gesture?.ToString() ?? string.Empty;
        Save();
        return ApplySearchHotkey();
    }

    /// <summary>Turns per-item shortcuts on or off as a group.</summary>
    public void SetItemHotkeysEnabled(bool on)
    {
        Config.ItemHotkeysEnabled = on;
        Save();
        ApplyItemHotkeys();
    }

    /// <summary>Gives one item a shortcut (null clears it) and re-registers the whole set.</summary>
    /// <returns>False if Windows refused this item's combination.</returns>
    public bool SetItemHotkey(DockItem item, HotkeyGesture? gesture)
    {
        item.Hotkey = gesture?.ToString();
        Save();
        var refused = ApplyItemHotkeys();
        ItemsChanged?.Invoke();
        return !refused.Contains(item);
    }

    // ---- Running-app state -------------------------------------------------

    /// <summary>
    /// The open-apps section's items by app key, in the order they joined it. Kept between
    /// refreshes so an app that stays open keeps its icon, its place and its cell rather than
    /// being rebuilt every time anything else opens or closes.
    /// </summary>
    private readonly Dictionary<string, DockItem> _openAppItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _openAppOrder = new();

    /// <summary>
    /// Pushes the latest snapshot onto every item on every dock, children included, and gives the
    /// first dock its section of open apps that aren't pinned.
    /// </summary>
    private void ApplyRunningState()
    {
        Probe.Count("applyRunning");
        if (_shuttingDown)
            return;

        // Resolved once per pass: the status text is identical for every item, and looking it up
        // per item would hit the string table dozens of times a second across a full dock.
        string status = Loc.Get("Dock.Running");
        bool dots = Config.ShowRunningIndicators;

        // An app counts as pinned when a visible top-level icon on any dock starts it. One filed
        // in a group or hidden still gets a section icon: otherwise an open app could be on screen
        // with nothing on any strip to show it or switch to it.
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Config.Docks)
        {
            foreach (var item in profile.Items)
            {
                var app = Running.Find(item);
                item.SetRunning(dots && app is not null, status);
                if (app is not null && !item.Hidden && !item.IsGroup)
                    pinned.Add(app.Key);
                foreach (var child in item.Children)
                    child.SetRunning(dots && Running.IsRunning(child), status);
            }
        }

        var open = Config.ShowRunningApps
            ? Running.Apps.Where(app => !pinned.Contains(app.Key)).ToList()
            : new List<RunningApp>();

        // Leavers go; newcomers join at the end, as on a Mac. Several arriving in one pass (the
        // apps already open when Anchor starts) line up in the order they were started.
        var still = open.Select(app => app.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _openAppOrder.Where(key => !still.Contains(key)).ToList())
        {
            _openAppOrder.Remove(gone);
            _openAppItems.Remove(gone);
        }
        foreach (var app in open.Where(app => !_openAppItems.ContainsKey(app.Key)).OrderBy(app => app.Started))
        {
            _openAppItems[app.Key] = DockItem.CreateOpenApp(app.Name, app.LaunchTarget, app.Key);
            _openAppOrder.Add(app.Key);
        }

        var section = _openAppOrder.Select(key => _openAppItems[key]).ToList();
        foreach (var item in section)
            item.SetRunning(dots, status);

        // One section, on the first dock, as the Mac has one Dock; the others are told they have
        // none so a dock that stops being first gives its section up.
        for (int i = 0; i < _docks.Count; i++)
            _docks[i].SetOpenApps(i == 0 ? section : Array.Empty<DockItem>());
        PublishIconsSoon();
    }

    /// <summary>
    /// The menu for the app owning <paramref name="window"/> (TrackSwipe's taskbar label was clicked),
    /// at a screen point: its dock icon's menu if it has one, otherwise the same menu for the open app.
    /// </summary>
    private void ShowAppMenu(nint window, int x, int y)
    {
        if (_docks.Count == 0)
            return;
        var root = NativeMethods.GetAncestor(window, 3 /* GA_ROOTOWNER */);
        var app = Running.Apps.FirstOrDefault(a => a.Windows.Contains(window) || a.Windows.Contains(root));
        DockItem? item = null;
        if (app is not null)
        {
            item = Config.Docks.SelectMany(p => p.Items)
                       .FirstOrDefault(i => !i.Hidden && !i.IsGroup && Running.Find(i)?.Key == app.Key)
                   ?? _openAppItems.GetValueOrDefault(app.Key)
                   ?? DockItem.CreateOpenApp(app.Name, app.LaunchTarget, app.Key);
        }
        if (item is not null)
            _docks[0].ShowTaskbarMenuAt(item, x, y);
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _iconMapTimer;

    /// <summary>
    /// Tells TrackSwipe (if running) where each open app's icon is, shortly after anything that moves
    /// icons or changes which apps are open: a burst of changes (an animation's frames) is one message.
    /// </summary>
    public void PublishIconsSoon()
    {
        if (_shuttingDown)
            return;
        if (_iconMapTimer is null)
        {
            _iconMapTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _iconMapTimer.Interval = TimeSpan.FromMilliseconds(150);
            _iconMapTimer.IsRepeating = false;
            _iconMapTimer.Tick += (_, _) => PublishIcons();
        }
        _iconMapTimer.Stop();
        _iconMapTimer.Start();
    }

    private void PublishIcons()
    {
        var map = new List<(nint, Windows.Graphics.RectInt32)>();
        foreach (var dock in _docks)
        {
            foreach (var (item, icon) in dock.IconRects())
            {
                if (Running.Find(item) is { } app)
                    foreach (var window in app.Windows)
                        map.Add((window, icon));
            }
        }
        TrackSwipeBridge.SendIconMap(map);
    }

    /// <summary>Turns the divider between pinned icons and open apps on or off.</summary>
    public void SetOpenAppsDivider(bool on)
    {
        Config.OpenAppsDivider = on;
        Save();
        ApplyRunningState();
        foreach (var dock in _docks)
            dock.ResyncOpenApps();
    }

    /// <summary>
    /// Pins an open app from the section: a real item on its dock, at the given visible slot. From
    /// the menu it grows in as the open copy shrinks away; dropped by a drag, it is simply there,
    /// since the dragged icon was already standing in that slot.
    /// </summary>
    public void KeepInDock(DockWindow dock, DockItem openApp, int visibleIndex, bool grow = true)
    {
        var pinned = new DockItem
        {
            Kind = DockItemKind.Application,
            DisplayName = openApp.DisplayName,
            Target = openApp.Target,
            // Shown straight away rather than blank until the icon lookup the insert starts.
            IconImage = openApp.IconImage,
        };
        if (grow)
            pinned.BeginAppearing();
        dock.InsertDockItem(visibleIndex, pinned);
    }
}
