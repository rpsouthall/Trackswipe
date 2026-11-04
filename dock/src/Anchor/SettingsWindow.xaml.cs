using Anchor.Controls;
using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Anchor;

/// <summary>
/// A Windows-Settings-style window: a left NavigationView with a <b>General</b> page (theme,
/// language, shortcut, start-with-Windows, maintenance), a <b>Docks</b> page listing every dock
/// with its position, monitor and hide behavior, and an <b>Apps &amp; links</b> page listing every
/// entry with a show/hide switch and a remove button. Mica-backed, and matches the docks' own
/// topmost state so it stays above them when they are topmost (snapped, or floating with "always
/// on top" on) without needlessly outranking every other app otherwise.
/// <para>
/// It talks to the <see cref="DockManager"/> rather than to a single dock: the General page's
/// settings are app-wide, and the other two pages span every dock.
/// </para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly DockManager _manager;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    /// <summary>
    /// Suppresses the "user changed this" handlers while controls are being populated from the
    /// config, so filling the page in cannot be mistaken for editing it.
    /// <para>
    /// Starts <b>true</b>, before any control exists, and <see cref="LoadGeneral"/> clears it when
    /// the page is loaded. That is not belt-and-braces: <c>InitializeComponent</c> itself can
    /// provoke a change — a control whose XAML-declared bounds coerce its default Value away from
    /// the property default raises its ValueChanged during parsing, and with the flag defaulting to
    /// false that would run the real handler and write a bogus value to the config before the page
    /// ever shows it. Every open of this window would silently reset that setting.
    /// </para>
    /// </summary>
    private bool _initializing = true;

    /// <summary>The theme in force when this window (or its General/Appearance load) last ran —
    /// the baseline <see cref="ThemeChoice_SelectionChanged"/> compares against to decide whether
    /// the restart button should show at all.</summary>
    private DockTheme _openedTheme;

    public SettingsWindow(DockManager manager)
    {
        _manager = manager;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("Settings.Title");
        SystemBackdrop = new MicaBackdrop();

        // Extend the Mica backdrop under the caption so the title bar matches a native Windows 11
        // window instead of showing an opaque strip. Must happen before the chrome theming below:
        // ExtendsContentIntoTitleBar re-extends the DWM frame into the client area, which resets
        // any border color already applied — set it any later and HideWindowBorder's effect
        // gets silently clobbered, leaving the default rim visible along the top edge.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Match the dock's chosen Light/Dark/System theme so the Settings window reads the same,
        // and keep the caption buttons and window frame in step if the OS theme changes while
        // System mode is on.
        ApplyTheme(manager.Config.Theme);
        RootGrid.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        // Re-assert on activation: DWM otherwise restores its default rim on some state changes.
        Activated += OnActivated;

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            // Only outrank other apps when a dock itself currently does — otherwise this window
            // would needlessly float above everything (full-screen apps, video calls) even though
            // a floating, non-topmost dock doesn't need that.
            p.IsAlwaysOnTop = manager.Config.Docks.Any(d => d.Snapped || d.AlwaysOnTop);
        }
        _appWindow.IsShownInSwitchers = true;

        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 880, 640);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        // The two bars the user can dismiss themselves; the rest are only ever closed in code.
        StartupBlockedBar.Closed += OnBarClosed;
        BackupBar.Closed += OnBarClosed;

        BuildLanguageList();
        BuildShortcutCaptures();
        LoadGeneral();
        RebuildDocks();
        RebuildApps();
        RebuildItemHotkeys();
        VersionText.Text = Loc.Format("About.Version", GetAppVersion());

        // A check that ran at startup put nothing on screen; if it found something, this is the
        // first chance to say so.
        if (_manager.PendingUpdate is { } pending)
            ShowUpdateAvailable(pending);

        // Keep the lists in step if something changes elsewhere (a drag reorder, a per-item menu,
        // a dock added from a dock's own menu). Deferred so a change we initiate from here doesn't
        // rebuild the tree mid-handler.
        _manager.ItemsChanged += OnDockItemsChanged;
        _manager.DocksChanged += OnDocksChanged;
        _manager.UpdateAvailable += OnUpdateAvailable;
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e) => ApplyChromeTheme();

    // Named, and every handler of the window's own removed here (this one included): a handler on
    // the window's own event that references the window is held by the native window for the life
    // of the process, so a lambda here kept every closed SettingsWindow and its whole visual tree
    // alive (about 33 GDI objects, 40 handles and 5 MB per open). WindowEventLifetimeTests pins the rule.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        _manager.ItemsChanged -= OnDockItemsChanged;
        _manager.DocksChanged -= OnDocksChanged;
        _manager.UpdateAvailable -= OnUpdateAvailable;

        foreach (var (item, handler) in _rowSubscriptions)
            item.PropertyChanged -= handler;
        _rowSubscriptions.Clear();

        NativeReclaim.Request();
    }

    private void OnDockItemsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        RebuildApps();
        RebuildItemHotkeys();
    });

    private void OnUpdateAvailable(ReleaseInfo release) =>
        DispatcherQueue.TryEnqueue(() => ShowUpdateAvailable(release));

    private void OnDocksChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        RebuildDocks();
        RebuildApps();
    });

    /// <summary>Applies the given app theme to this window's root (called on open and whenever
    /// the choice changes elsewhere), and re-themes the window chrome to match.</summary>
    internal void ApplyTheme(DockTheme theme)
    {
        RootGrid.RequestedTheme = DockWindow.ResolveTheme(theme);
        ApplyChromeTheme();
    }

    /// <summary>
    /// Tracks the caption buttons to the effective theme, and hides the DWM window rim by painting
    /// it this theme's surface color. This window extends its content into the title bar, leaving
    /// only the rim's top edge visible — where anything that doesn't match the surface reads as a
    /// stray line above the title bar (see <see cref="WindowChrome.HideWindowBorder"/>).
    /// </summary>
    private void ApplyChromeTheme()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        WindowChrome.SetTitleBarTheme(_appWindow, dark);
        WindowChrome.HideWindowBorder(_hwnd, dark);
    }

    // The two dimmed-text styles the code-built rows use, taken from this window's own resources
    // rather than the application's. See the comment on them in SettingsWindow.xaml: the brush
    // they carry has to be resolved against THIS window's theme, which is the user's Light/Dark
    // choice and not necessarily the application's.
    private Style SecondaryCaptionStyle => (Style)RootGrid.Resources["SecondaryCaption"];

    private Style SecondaryTextStyle => (Style)RootGrid.Resources["SecondaryText"];

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---- Navigation --------------------------------------------------------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (GeneralPanel is null || AppearancePanel is null || ShortcutsPanel is null ||
            DocksPanel is null || AppsPanel is null || AboutPanel is null)
            return;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        DocksPanel.Visibility = tag == "docks" ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = tag == "apps" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Selects a page by its nav tag. Used when the window is re-opened after a language
    /// change, so the user lands back where they were rather than on General.</summary>
    internal void Navigate(string tag)
    {
        foreach (var candidate in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if ((candidate.Tag as string) == tag)
            {
                Nav.SelectedItem = candidate;
                return;
            }
        }
    }

    /// <summary>The page currently showing, as its nav tag.</summary>
    internal string CurrentPage => (Nav.SelectedItem as NavigationViewItem)?.Tag as string ?? "general";

    // ---- General page ------------------------------------------------------

    private void LoadGeneral()
    {
        _initializing = true;

        var cfg = _manager.Config;
        _openedTheme = cfg.Theme;
        RestartButton.Visibility = Visibility.Collapsed;
        ThemeChoice.SelectedIndex = cfg.Theme switch
        {
            DockTheme.Light => 0,
            DockTheme.Dark => 1,
            DockTheme.System => 2,
            _ => 1,
        };
        // Index 0 is "Match Windows"; the rest follow Loc.Available in order.
        int languageIndex = string.IsNullOrEmpty(cfg.Language)
            ? 0
            : Loc.Available.ToList().FindIndex(
                l => string.Equals(l.Code, cfg.Language, StringComparison.OrdinalIgnoreCase)) + 1;
        LanguageChoice.SelectedIndex = languageIndex > 0 ? languageIndex : 0;

        HotkeySwitch.IsOn = cfg.HotkeyEnabled;

        RunningIndicatorsSwitch.IsOn = cfg.ShowRunningIndicators;
        RunningAppsSwitch.IsOn = cfg.ShowRunningApps;
        OpenAppsDividerSwitch.IsOn = cfg.OpenAppsDivider;
        OpenAppsDividerSwitch.IsEnabled = cfg.ShowRunningApps;

        // Asked of Windows rather than read from the config, because the user can change it
        // outside Anchor (Task Manager ▸ Startup apps) — and on the packaged build that answer is
        // an async WinRT call, so the switch settles just after the page rather than with it.
        _ = RefreshStartupSwitchAsync();
        // TrackSwipe starts a dock it hosts; a switch for starting it with Windows would only fight that.
        if (Hosting.ByTrackSwipe)
        {
            // The card is the switch's nearest Border, through the logical tree (the page isn't laid out yet).
            DependencyObject? element = StartupSwitch;
            while (element is FrameworkElement { Parent: { } parent } && element is not Border)
                element = parent;
            if (element is Border startupCard)
                startupCard.Visibility = Visibility.Collapsed;
        }

        DensityChoice.SelectedIndex = cfg.Density switch
        {
            DockDensity.Small => 0,
            DockDensity.Large => 2,
            _ => 1,
        };
        AccentTintSwitch.IsOn = cfg.AccentTint;
        MagnifySwitch.IsOn = cfg.Magnify;
        GroupOpenOnHoverSwitch.IsOn = cfg.GroupOpenOnHover;
        SettingsPositionChoice.SelectedIndex = cfg.SettingsPosition == SettingsPosition.Leading ? 1 : 0;
        ShowSettingsButtonSwitch.IsOn = cfg.ShowSettingsButton;
        SettingsPositionChoice.IsEnabled = cfg.ShowSettingsButton;
        ShowItemLabelsSwitch.IsOn = cfg.ShowItemLabels;
        ItemHotkeysSwitch.IsOn = cfg.ItemHotkeysEnabled;

        _initializing = false;
    }

    // ---- Appearance page ---------------------------------------------------

    private void DensityChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetDensity(DensityChoice.SelectedIndex switch
        {
            0 => DockDensity.Small,
            2 => DockDensity.Large,
            _ => DockDensity.Medium,
        });
    }

    private void AccentTintSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetAccentTint(AccentTintSwitch.IsOn);
    }

    private void MagnifySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetMagnify(MagnifySwitch.IsOn);
    }

    private void GroupOpenOnHoverSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetGroupOpenOnHover(GroupOpenOnHoverSwitch.IsOn);
    }

    private void ShowSettingsButtonSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // Where the gear sits means nothing without one.
        SettingsPositionChoice.IsEnabled = ShowSettingsButtonSwitch.IsOn;
        if (_initializing)
            return;
        _manager.SetShowSettingsButton(ShowSettingsButtonSwitch.IsOn);
    }

    private void SettingsPositionChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetSettingsPosition(SettingsPositionChoice.SelectedIndex == 1
            ? SettingsPosition.Leading
            : SettingsPosition.Trailing);
    }

    private void ShowItemLabelsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetShowItemLabels(ShowItemLabelsSwitch.IsOn);
    }

    // ---- Language ----------------------------------------------------------

    private void BuildLanguageList()
    {
        // "Match Windows" first, then every shipped language under its own name — so someone who
        // can't read the language currently in use can still recognize theirs in the list.
        LanguageChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Language.System") });
        foreach (var language in Loc.Available)
            LanguageChoice.Items.Add(new ComboBoxItem { Content = language.NativeName });
    }

    private void LanguageChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;

        int index = LanguageChoice.SelectedIndex;
        string code = index <= 0 ? string.Empty : Loc.Available[index - 1].Code;

        // SetLanguage rebuilds every dock so the new table takes effect immediately; this window
        // has to be rebuilt too — it is the one the user is looking at — and it re-opens on the
        // page they were on. Deferred so the rebuild doesn't run inside this handler, which would
        // be tearing down the very ComboBox that raised it.
        _manager.SetLanguage(code);
        _manager.ReopenSettings(CurrentPage);
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        var theme = ThemeChoice.SelectedIndex switch
        {
            0 => DockTheme.Light,
            2 => DockTheme.System,
            _ => DockTheme.Dark,
        };
        _manager.SetTheme(theme);
        // The live switch doesn't always finish repainting the glass cleanly (see
        // DockWindow.ApplyTheme) — offer the reliable fallback only once there is actually
        // something to restart for, and hide it again if the user flips back to where they
        // started.
        RestartButton.Visibility = theme != _openedTheme ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) => _manager.Restart();

    private void RunningIndicatorsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetShowRunningIndicators(RunningIndicatorsSwitch.IsOn);
    }

    private void RunningAppsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetShowRunningApps(RunningAppsSwitch.IsOn);
        OpenAppsDividerSwitch.IsEnabled = RunningAppsSwitch.IsOn;
    }

    private void OpenAppsDividerSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetOpenAppsDivider(OpenAppsDividerSwitch.IsOn);
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        var state = await _manager.SetLaunchAtStartupAsync(StartupSwitch.IsOn);

        // Windows refuses to let an app re-enable a startup entry its user turned off, so a switch
        // left showing "on" would be a lie. Put it back and name the place they can undo it.
        bool blocked = state is StartupService.StartupState.BlockedByUser
                            or StartupService.StartupState.BlockedByPolicy;
        SetBarOpen(StartupBlockedBar, blocked);
        if (blocked)
            SetStartupSwitchSilently(false);
    }

    /// <summary>Reads the real startup state back from Windows and shows it, without the write-back
    /// that setting the switch would otherwise trigger.</summary>
    private async Task RefreshStartupSwitchAsync()
    {
        bool enabled = await StartupService.IsEnabledAsync();
        SetStartupSwitchSilently(enabled);
    }

    /// <summary>Moves the startup switch to match reality. <see cref="_initializing"/> is saved and
    /// restored rather than simply cleared: this also runs from inside
    /// <see cref="LoadGeneral"/>'s initializing block, which is not finished with it.</summary>
    private void SetStartupSwitchSilently(bool on)
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        StartupSwitch.IsOn = on;
        _initializing = wasInitializing;
    }

    // ---- Shortcuts page ----------------------------------------------------
    //
    // The two capture buttons are built here rather than in XAML because HotkeyCaptureButton is a
    // code-only control (see Controls/HotkeyCaptureButton.cs) — it carries the whole "arm capture,
    // read the next combination, reject the ones Windows would refuse" behavior that both of these
    // need and that used to live, in one copy, in this file.

    private HotkeyCaptureButton? _summonCapture;
    private HotkeyCaptureButton? _searchCapture;

    private void BuildShortcutCaptures()
    {
        _summonCapture = new HotkeyCaptureButton
        {
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Label = Loc.Get("Settings.Hotkey"),
            Gesture = _manager.ConfiguredHotkey,
        };
        _summonCapture.NeedsModifier += () => ShowBar(HotkeyBar, "Hotkey.NeedModifier");
        _summonCapture.Assigned += gesture =>
        {
            bool registered = _manager.SetHotkey(gesture);
            // A cleared shortcut can't clash, so only a real assignment can fail here — and only
            // when the switch is on, since nothing is registered while it is off.
            if (gesture is not null && !registered && HotkeySwitch.IsOn)
                ShowBar(HotkeyBar, "Hotkey.Taken");
            else
                SetBarOpen(HotkeyBar, false);
        };
        HotkeyColumn.Children.Add(_summonCapture);

        _searchCapture = new HotkeyCaptureButton
        {
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
            Label = Loc.Get("Settings.SearchHotkey"),
            Gesture = _manager.ConfiguredSearchHotkey,
        };
        _searchCapture.NeedsModifier += () => ShowBar(SearchHotkeyBar, "Hotkey.NeedModifier");
        _searchCapture.Assigned += gesture =>
        {
            // Same shape as the summon shortcut above: clearing can't clash, so only a real
            // assignment can come back refused.
            bool registered = _manager.SetSearchHotkey(gesture);
            if (gesture is not null && !registered)
                ShowBar(SearchHotkeyBar, "Hotkey.Taken");
            else
                SetBarOpen(SearchHotkeyBar, false);
        };
        SearchHotkeyColumn.Children.Add(_searchCapture);
    }

    private void HotkeySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        if (!_manager.SetHotkeyEnabled(HotkeySwitch.IsOn))
            ShowBar(HotkeyBar, "Hotkey.Taken");
        else
            SetBarOpen(HotkeyBar, false);
    }

    private void ItemHotkeysSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetItemHotkeysEnabled(ItemHotkeysSwitch.IsOn);
        RebuildItemHotkeys();
    }

    /// <summary>
    /// Lists the per-item shortcuts that are currently assigned, each with a way to drop it.
    /// Assignment itself happens on an icon's own right-click menu — that is where you know which
    /// item you mean — but a shortcut you assigned three weeks ago is otherwise invisible until
    /// you happen to press it, so they are gathered here.
    /// </summary>
    private void RebuildItemHotkeys()
    {
        if (ItemHotkeyList is null)
            return;
        ItemHotkeyList.Children.Clear();

        var assigned = _manager.AllItems()
            .Where(i => HotkeyGesture.TryParse(i.Hotkey, out _))
            .ToList();

        if (assigned.Count == 0)
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            hint.Children.Add(new FontIcon
            {
                Glyph = "", // Info
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 14,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            });
            hint.Children.Add(new TextBlock
            {
                Text = Loc.Get("Shortcuts.NoneAssigned"),
                Style = SecondaryCaptionStyle,
                TextWrapping = TextWrapping.Wrap,
            });
            ItemHotkeyList.Children.Add(hint);
            return;
        }

        foreach (var item in assigned)
        {
            var row = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.DisplayName) ? Loc.Get("Apps.Unnamed") : item.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var gesture = new TextBlock
            {
                Text = item.Hotkey ?? string.Empty,
                Style = SecondaryCaptionStyle,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(gesture, 1);
            row.Children.Add(gesture);

            var clear = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "", // Cancel
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 12,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(clear, Loc.Get("Menu.ShortcutClear"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clear, Loc.Get("Menu.ShortcutClear"));
            var captured = item;
            clear.Click += (_, _) =>
            {
                _manager.SetItemHotkey(captured, null);
                RebuildItemHotkeys();
            };
            Grid.SetColumn(clear, 2);
            row.Children.Add(clear);

            ItemHotkeyList.Children.Add(row);
        }
    }

    private static void ShowBar(InfoBar bar, string key)
    {
        bar.Message = Loc.Get(key);
        SetBarOpen(bar, true);
    }

    // ---- Update check ------------------------------------------------------
    //
    // There is no manual "check now" control anymore — whether Anchor checks at all is decided by
    // DockConfig.CheckForUpdates (see DockManager's startup check). This just surfaces a release
    // that check already found.

    /// <summary>Offers a found release: a link to the download page, and a way to be left alone
    /// about this one. Nothing is downloaded or installed — Anchor is a portable zip.</summary>
    private void ShowUpdateAvailable(ReleaseInfo release)
    {
        UpdateBar.Message = Loc.Format("Update.Available", release.Version.ToString());
        UpdateBar.Severity = InfoBarSeverity.Informational;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new HyperlinkButton
        {
            Content = Loc.Get("Update.Get"),
            NavigateUri = new Uri(release.Url),
        });
        var skip = new Button { Content = Loc.Get("Update.Skip") };
        skip.Click += (_, _) =>
        {
            _manager.SkipUpdate(release);
            SetBarOpen(UpdateBar, false);
        };
        actions.Children.Add(skip);

        UpdateBar.Content = actions;
        SetBarOpen(UpdateBar, true);
    }

    /// <summary>
    /// Opens or closes one of the page's info bars.
    /// <para>
    /// <see cref="InfoBar.IsOpen"/> alone is not enough. A closed bar collapses its own template
    /// but stays a <em>visible</em> child of the card's <see cref="StackPanel"/>, which goes on
    /// spending its <c>Spacing</c> on it — so every card carrying one sat with a strip of dead
    /// space under its controls, whether or not there was anything to say. Collapsing the element
    /// itself takes the row out of the layout entirely.
    /// </para>
    /// </summary>
    private static void SetBarOpen(InfoBar bar, bool open)
    {
        bar.IsOpen = open;
        bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps a bar the user dismissed themselves out of the layout too — closing it from
    /// its own X clears <see cref="InfoBar.IsOpen"/> without going through
    /// <see cref="SetBarOpen"/>.</summary>
    private static void OnBarClosed(InfoBar sender, InfoBarClosedEventArgs args) =>
        sender.Visibility = Visibility.Collapsed;

    // ---- Import / export ---------------------------------------------------

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            picker.SuggestedFileName = "anchor-dock";

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            if (_manager.Export(file.Path))
                ShowBackupResult(Loc.Format("Backup.Exported", file.Path), InfoBarSeverity.Success);
            else
                ShowBackupResult(Loc.Get("Backup.ExportFailed"), InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            Diag.Log("Export failed: " + ex.Message);
            ShowBackupResult(Loc.Get("Backup.ExportFailed"), InfoBarSeverity.Error);
        }
    }

    // Import replaces every dock and every pinned item with no undo, so — like Reset — it gets a
    // confirmation, with the safe choice as the default button.
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            var confirm = new ContentDialog
            {
                XamlRoot = Nav.XamlRoot,
                Title = Loc.Get("Backup.ConfirmTitle"),
                Content = Loc.Get("Backup.ConfirmBody"),
                PrimaryButtonText = Loc.Get("Backup.ConfirmButton"),
                CloseButtonText = Loc.Get("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (!_manager.Import(file.Path))
            {
                ShowBackupResult(Loc.Get("Backup.ImportFailed"), InfoBarSeverity.Error);
                return;
            }

            // An import can change the language and the theme, so this window has to be rebuilt
            // rather than merely refreshed — same path a language change takes.
            _manager.ReopenSettings(CurrentPage);
        }
        catch (Exception ex)
        {
            Diag.Log("Import failed: " + ex.Message);
            ShowBackupResult(Loc.Get("Backup.ImportFailed"), InfoBarSeverity.Error);
        }
    }

    private void ShowBackupResult(string message, InfoBarSeverity severity)
    {
        BackupBar.Message = message;
        BackupBar.Severity = severity;
        SetBarOpen(BackupBar, true);
    }

    // Resetting wipes every pinned app/file/folder/link with no undo, so — unlike the
    // low-stakes, easily-re-added per-item "Remove" — it gets a confirmation dialog, per the
    // Fluent guidance to confirm destructive, hard-to-recover actions. The safe choice (Cancel)
    // is the default button so an accidental Enter doesn't wipe the dock.
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Nav.XamlRoot,
            Title = Loc.Get("Reset.Title"),
            Content = Loc.Get("Reset.Body"),
            PrimaryButtonText = Loc.Get("Reset.Confirm"),
            CloseButtonText = Loc.Get("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _manager.ResetToDefaults();
    }

    // ---- Docks page --------------------------------------------------------
    //
    // One card per dock, built imperatively: the number of docks — and of monitors to offer each
    // one — is only known at runtime. These are the settings that lived on the General page when
    // Anchor had exactly one dock to apply them to.

    private void AddDock_Click(object sender, RoutedEventArgs e) => _manager.AddDock();

    private void RebuildDocks()
    {
        DocksList.Children.Clear();
        foreach (var profile in _manager.Config.Docks)
            DocksList.Children.Add(BuildDockCard(profile));
    }

    private Border BuildDockCard(DockProfile profile)
    {
        var dock = _manager.WindowFor(profile);
        var panel = new StackPanel { Spacing = 12 };

        // Header: the dock's name (editable in place) and, unless it's the last one, Remove.
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Text = profile.Name,
            PlaceholderText = DockLabel(profile),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, Loc.Get("Docks.Name"));
        // Commit on every keystroke: this is a label, not a form to submit, and the alternative
        // (commit on focus loss) silently drops the edit when the window is closed while focused.
        nameBox.TextChanged += (_, _) =>
        {
            profile.Name = nameBox.Text;
            _manager.Save();
        };
        Grid.SetColumn(nameBox, 0);
        header.Children.Add(nameBox);

        var remove = new Button
        {
            Content = new FontIcon
            {
                Glyph = "", // Delete
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 14,
            },
            VerticalAlignment = VerticalAlignment.Center,
            // Anchor with no dock at all is a tray icon and no obvious way back, so the last one
            // can't be removed.
            IsEnabled = _manager.Config.Docks.Count > 1,
        };
        ToolTipService.SetToolTip(remove, Loc.Get("Docks.Remove"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, Loc.Get("Docks.Remove"));
        remove.Click += (_, _) => _manager.RemoveDock(profile);
        Grid.SetColumn(remove, 1);
        header.Children.Add(remove);

        panel.Children.Add(header);

        // Monitor. Rebuilt on every RebuildDocks, so unplugging a display is reflected the next
        // time a dock is added or removed rather than being cached for the session.
        var displays = DockManager.Displays;
        if (displays.Count > 1)
        {
            var monitors = new ComboBox { MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < displays.Count; i++)
            {
                var bounds = displays[i].OuterBounds;
                monitors.Items.Add(new ComboBoxItem
                {
                    Content = Loc.Format("Docks.Monitor", i + 1, bounds.Width, bounds.Height),
                });
            }
            monitors.SelectedIndex = DockManager.DisplayIndexOf(profile);
            monitors.SelectionChanged += (_, _) =>
            {
                if (_initializing || monitors.SelectedIndex < 0)
                    return;
                _manager.MoveDockToDisplay(profile, monitors.SelectedIndex);
            };
            panel.Children.Add(Row(Loc.Get("Docks.MonitorLabel"), Loc.Get("Docks.MonitorDesc"), monitors));
        }

        // Position.
        var edges = new ComboBox { MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var key in new[]
                 {
                     "Position.Floating", "Position.Bottom", "Position.Top",
                     "Position.Left", "Position.Right",
                 })
            edges.Items.Add(new ComboBoxItem { Content = Loc.Get(key) });
        edges.SelectedIndex = !profile.Snapped ? 0 : profile.Edge switch
        {
            DockEdge.Bottom => 1,
            DockEdge.Top => 2,
            DockEdge.Left => 3,
            DockEdge.Right => 4,
            _ => 0,
        };
        edges.SelectionChanged += (_, _) =>
        {
            if (_initializing)
                return;
            dock?.SetSnap(edges.SelectedIndex switch
            {
                1 => DockEdge.Bottom,
                2 => DockEdge.Top,
                3 => DockEdge.Left,
                4 => DockEdge.Right,
                _ => (DockEdge?)null, // Floating
            });
        };
        panel.Children.Add(Row(Loc.Get("Settings.Position"), Loc.Get("Settings.PositionDesc"), edges));

        panel.Children.Add(Row(
            Loc.Get("Settings.KeepCentered"), Loc.Get("Settings.KeepCenteredDesc"),
            Switch(profile.KeepCentered, Loc.Get("Settings.KeepCentered"), on => dock?.SetKeepCentered(on))));

        panel.Children.Add(Row(
            Loc.Get("Settings.Transpose"), Loc.Get("Settings.TransposeDesc"),
            Switch(profile.VerticalWhenSideSnapped, Loc.Get("Settings.Transpose"),
                on => dock?.SetVerticalWhenSideSnapped(on))));

        panel.Children.Add(Row(
            Loc.Get("Settings.AutoHide"), Loc.Get("Settings.AutoHideDesc"),
            Switch(profile.AutoHide, Loc.Get("Settings.AutoHide"), on => dock?.SetAutoHide(on))));

        panel.Children.Add(Row(
            Loc.Get("Settings.DodgeWindows"), Loc.Get("Settings.DodgeWindowsDesc"),
            Switch(profile.DodgeWindows, Loc.Get("Settings.DodgeWindows"), on => dock?.SetDodgeWindows(on))));

        panel.Children.Add(Row(
            Loc.Get("Settings.AlwaysOnTop"), Loc.Get("Settings.AlwaysOnTopDesc"),
            Switch(profile.AlwaysOnTop, Loc.Get("Settings.AlwaysOnTop"), on => dock?.SetAlwaysOnTop(on))));

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 14, 14),
            Child = panel,
        };

        ToggleSwitch Switch(bool isOn, string name, Action<bool> apply)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = isOn,
                OnContent = null,
                OffContent = null,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
            toggle.Toggled += (_, _) =>
            {
                if (!_initializing)
                    apply(toggle.IsOn);
            };
            return toggle;
        }
    }

    /// <summary>A title/description pair with a control on the right — the Settings row shape.
    /// An instance method because the description's style has to come from this window's own
    /// resources, so that it picks up this window's theme.</summary>
    private Grid Row(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Style = SecondaryCaptionStyle,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // ---- Apps page ---------------------------------------------------------

    /// <summary>Which siblings list (top-level or a group's children) a row's item belongs to, and
    /// the dock/insideGroup context needed to commit a reorder once it's dropped. Stashed on each
    /// row's Tag so the drag handlers below don't need to re-derive it from the visual tree.</summary>
    private sealed record RowDragInfo(DockWindow Dock, DockItem Item, IList<DockItem> Siblings, bool InsideGroup);

    /// <summary>The item currently being dragged, and the siblings list it came from — a drop is
    /// only honored onto another row from that same list, since dragging between a top-level order
    /// and a group's own children isn't a supported gesture here (use "Add to group" / the group's
    /// own fly-out for that instead).</summary>
    private DockItem? _dragItem;
    private IList<DockItem>? _dragSiblings;

    private void AddNew_Click(object sender, RoutedEventArgs e) => _manager.OpenAddNew();

    /// <summary>
    /// Every row's subscription to its item's PropertyChanged, so closing the window can drop them all.
    /// The DockItem is owned by the config and outlives every Settings window; a handler left on it
    /// holds the row's closure and, through it, the whole closed window (gcroot on a retained
    /// SettingsWindow: DockItem -> PropertyChangedEventHandler -> closure -> SettingsWindow).
    /// </summary>
    private readonly List<(DockItem Item, System.ComponentModel.PropertyChangedEventHandler Handler)> _rowSubscriptions = new();

    private void RebuildApps()
    {
        AppsList.Children.Clear();

        var docks = _manager.Config.Docks;
        if (docks.All(d => d.Items.Count == 0))
        {
            AppsList.Children.Add(new TextBlock
            {
                Text = Loc.Get("Apps.Empty"),
                Style = SecondaryTextStyle,
                Margin = new Thickness(2, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var profile in docks)
        {
            // With one dock the heading would be noise; with several, "which dock is this pinned
            // to?" is the first thing you need to know about a row.
            if (docks.Count > 1)
            {
                AppsList.Children.Add(new TextBlock
                {
                    Text = DockLabel(profile),
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    Margin = new Thickness(2, 8, 0, 0),
                });
            }

            var dock = _manager.WindowFor(profile);
            if (dock is null)
                continue;

            foreach (var item in profile.Items)
            {
                AppsList.Children.Add(BuildRow(dock, item, profile.Items));
                // A group's children never appear on the strip themselves, so list them under it —
                // indented — rather than leaving them invisible outside the fly-out. Collapsing the
                // group hides this sub-list without touching the children themselves.
                if (!item.IsGroup || !item.IsCollapsed)
                {
                    foreach (var child in item.Children)
                        AppsList.Children.Add(BuildRow(dock, child, item.Children, insideGroup: true));
                }
            }
        }
    }

    /// <summary>A dock's name, or a positional fallback when the user hasn't given it one.</summary>
    private string DockLabel(DockProfile profile) => _manager.LabelFor(profile);

    private FrameworkElement BuildRow(DockWindow dock, DockItem item, IList<DockItem> siblings, bool insideGroup = false)
    {
        var grid = new Grid { ColumnSpacing = 14, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon (bitmap if resolved, else the kind glyph). Built imperatively rather than via
        // a binding, so it needs its own refresh: icons often resolve asynchronously (shell
        // thumbnail / favicon fetch) after this row is already on screen, and this keeps it
        // in sync with that instead of freezing on whatever was resolved (or not) at build time.
        var iconHost = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        void RenderIcon()
        {
            iconHost.Children.Clear();
            if (item.IconImage is not null)
            {
                iconHost.Children.Add(new Image
                {
                    Source = item.IconImage,
                    Width = 28,
                    Height = 28,
                    Stretch = Stretch.Uniform,
                });
            }
            else
            {
                var glyph = new FontIcon
                {
                    Glyph = item.Glyph,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 18,
                };
                // Only when there is one: a null Foreground would hide the glyph, not untint it.
                if (item.GlyphTint is { } tint)
                    glyph.Foreground = tint;
                iconHost.Children.Add(glyph);
            }
        }
        RenderIcon();

        void OnItemPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DockItem.IconImage) or nameof(DockItem.IconColor))
                RenderIcon();
        }
        System.ComponentModel.PropertyChangedEventHandler onItemChanged = OnItemPropertyChanged;
        item.PropertyChanged += onItemChanged;
        _rowSubscriptions.Add((item, onItemChanged));

        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        // Name + subtitle (kind • target).
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.DisplayName)
                ? Loc.Get("Apps.Unnamed")
                : item.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = SubtitleFor(item),
            Style = SecondaryCaptionStyle,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // Actions: the show/hide switch sits directly to the left of the delete button.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Collapse/expand: a group's own disclosure control, hiding its indented children below
        // it in this list. No caption — the chevron direction alone conveys expanded vs. collapsed,
        // the same way the show/hide switch below needs none. Only meaningful for a group that
        // actually has children to hide.
        if (item.IsGroup && item.Children.Count > 0)
        {
            var collapseToggle = new Button
            {
                Content = new FontIcon
                {
                    Glyph = item.IsCollapsed ? "\uE76C" : "\uE70D", // ChevronRight / ChevronDown
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            var collapseTip = Loc.Get(item.IsCollapsed ? "Apps.ExpandGroup" : "Apps.CollapseGroup");
            ToolTipService.SetToolTip(collapseToggle, collapseTip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(collapseToggle, collapseTip);
            collapseToggle.Click += (_, _) => dock.SetGroupCollapsed(item, !item.IsCollapsed);
            actions.Children.Add(collapseToggle);
        }

        // Edit: the same panel the dock's own right-click menu opens, carrying the item's name,
        // its target and its icon. A separator has none of the three, so it gets no button.
        if (!item.IsSeparator)
        {
            var edit = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "", // Edit
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(edit, Loc.Get("Flyout.Edit"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, Loc.Get("Flyout.Edit"));
            edit.Click += (_, _) => _manager.OpenItemEditor(dock, item);
            actions.Children.Add(edit);
        }

        // Lift a grouped item back out onto the dock. Only meaningful inside a group, so it isn't
        // built at all for a top-level row.
        if (insideGroup)
        {
            var lift = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "\uE7A7", // Undo \u2014 put it back where it came from
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(lift, Loc.Get("Menu.RemoveFromGroup"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(lift, Loc.Get("Menu.RemoveFromGroup"));
            lift.Click += (_, _) => dock.RemoveFromGroup(item);
            actions.Children.Add(lift);
        }

        // Add to group. Only for a top-level item that isn't itself a group or separator \u2014 a
        // child is already homed (it has its own "move out of group" button above instead), and
        // grouping a group or a separator isn't supported (see DockWindow.Groups.cs).
        if (!insideGroup && !item.IsGroup && !item.IsSeparator)
        {
            var addToGroup = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "\ue838", // FolderOpen \u2014 matches the Group kind's own icon
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(addToGroup, Loc.Get("Menu.MoveToGroup"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(addToGroup, Loc.Get("Menu.MoveToGroup"));
            addToGroup.Click += (_, _) => ShowAddToGroupMenu(addToGroup, dock, item);
            actions.Children.Add(addToGroup);
        }

        // Show/hide switch (On = shown on the dock). No on/off caption — the switch state alone
        // conveys it; the accessible name/tooltip carry the meaning for AT users.
        var toggle = new ToggleSwitch
        {
            IsOn = !item.Hidden,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(toggle, Loc.Get("Apps.ShowOnDock"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, Loc.Get("Apps.ShowOnDock"));
        toggle.Toggled += (s, _) =>
        {
            if (s is ToggleSwitch ts)
                dock.SetItemHidden(item, !ts.IsOn);
        };
        actions.Children.Add(toggle);

        // Remove.
        var remove = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE74D", // Delete
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 14,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(remove, Loc.Get("Apps.RemoveFromDock"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, Loc.Get("Apps.RemoveFromDock"));
        remove.Click += (_, _) =>
        {
            if (insideGroup)
                dock.RemoveGroupChild(item);
            else
                dock.RemoveDockItem(item);
        };
        actions.Children.Add(remove);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var row = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 12, 10),
            // Indent a group's children so the list reads as a hierarchy at a glance.
            Margin = insideGroup ? new Thickness(28, 0, 0, 0) : new Thickness(0),
            Child = grid,
        };
        // The DockItem outlives this row (it's rebuilt wholesale on every RebuildApps), so the
        // subscription above must be torn down explicitly or it leaks a handler per rebuild.
        // Unloaded covers the rebuild; it does NOT reliably fire when the whole window closes, so
        // OnClosed drops whatever is left (see _rowSubscriptions).
        row.Unloaded += (_, _) =>
        {
            item.PropertyChanged -= onItemChanged;
            _rowSubscriptions.Remove((item, onItemChanged));
        };

        // Drag-to-reorder: a hand-rolled press/poll/release gesture — the same pattern
        // DockWindow.xaml.cs (Dock_PointerPressed/DragTick) already uses for the dock's own strip,
        // and for the same reason: routed PointerMoved only keeps arriving at the pressed row while
        // the cursor stays over it, so a real (even moderately fast) drag can leave the row's bounds
        // before the move threshold is ever reached, and the drag silently never starts. Polling the
        // global cursor and button state on a timer sidesteps routed pointer delivery entirely. It
        // also has to avoid WinUI's built-in CanDrag/DragStarting, which swallows the click on any
        // button nested inside the dragged element — that's why the group collapse toggle stopped
        // responding once this was CanDrag-based. Never marking a pointer event handled or capturing
        // the pointer means a plain click still reaches the row's buttons exactly as if none of this
        // were here; only crossing the move threshold ever calls Reorder*.
        row.Tag = new RowDragInfo(dock, item, siblings, insideGroup);
        row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Row_PointerPressed), handledEventsToo: true);

        return row;
    }

    private const int RowDragThreshold = 8;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _rowDragTimer;
    private bool _rowDragging;
    private NativeMethods.POINT _rowDragStartCursor;

    /// <summary>The floating copy of the row being dragged — shown at the cursor, the same way
    /// DockWindow's own strip shows <c>DragGhost</c> while reordering. Sized once, from the row that
    /// was actually pressed, at the moment the drag starts.</summary>
    private Border? _dragGhost;
    private double _dragGhostHalfHeight;

    private void Row_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
            return;

        var row = (FrameworkElement)sender;
        var info = (RowDragInfo)row.Tag!;
        _dragItem = info.Item;
        _dragSiblings = info.Siblings;
        _rowDragging = false;
        NativeMethods.GetCursorPos(out _rowDragStartCursor);

        _rowDragTimer ??= CreateRowDragTimer();
        if (!_rowDragTimer.IsRunning)
            _rowDragTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateRowDragTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(8);
        t.Tick += (_, _) => RowDragTick();
        return t;
    }

    private void RowDragTick()
    {
        // Button released -> end the gesture. The list is already in the right order — every slot
        // change happened live, below — so there's nothing left to resolve here.
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0)
        {
            _rowDragTimer?.Stop();
            bool wasDragging = _rowDragging;
            _rowDragging = false;
            HideDragGhost();

            var dragItem = _dragItem;
            _dragItem = null;
            _dragSiblings = null;

            if (wasDragging && dragItem is not null)
            {
                var finalRow = FindRowForItem(dragItem);
                if (finalRow is not null)
                    finalRow.Opacity = 1;
            }
            return;
        }

        if (_dragItem is null)
            return;

        NativeMethods.GetCursorPos(out var cur);

        if (!_rowDragging)
        {
            int dx = cur.X - _rowDragStartCursor.X;
            int dy = cur.Y - _rowDragStartCursor.Y;
            if (Math.Abs(dx) <= RowDragThreshold && Math.Abs(dy) <= RowDragThreshold)
                return; // still a potential click

            _rowDragging = true;
            var sourceRow = FindRowForItem(_dragItem);
            if (sourceRow is not null)
                ShowDragGhost(_dragItem, sourceRow);
        }

        // The row representing the dragged item gets rebuilt from scratch — a new instance — every
        // time a live reorder below actually moves it, so it has to be re-found and re-dimmed every
        // tick rather than held onto across ticks.
        var currentRow = FindRowForItem(_dragItem);
        if (currentRow is not null)
            currentRow.Opacity = 0.35; // the real row sits low-visibility at its live position; the ghost is what reads as "held"

        UpdateDragGhostPosition(cur);
        TryLiveReorder(_dragItem, _dragSiblings!);
    }

    /// <summary>Converts the cursor's current screen position into <c>AppsList</c>'s own coordinate
    /// space — screen -> this window's client area via <see cref="NativeMethods.ScreenToClient"/>,
    /// physical pixels -> DIPs via the window's DPI, then window content -> AppsList via
    /// <see cref="UIElement.TransformToVisual"/> — and finds the row under it, the same way a
    /// routed pointer point would have.</summary>
    private bool TryFindDropTarget(DockItem dragItem, IList<DockItem> dragSiblings,
        out FrameworkElement target, out RowDragInfo targetInfo, out bool after)
    {
        target = null!;
        targetInfo = null!;
        after = false;

        NativeMethods.GetCursorPos(out var cur);
        if (!NativeMethods.ScreenToClient(_hwnd, ref cur))
            return false;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var rootPoint = new Windows.Foundation.Point(cur.X / scale, cur.Y / scale);
        var dropPoint = RootGrid.TransformToVisual(AppsList).TransformPoint(rootPoint);

        var found = FindRowAt(dropPoint);
        if (found is null || found.Tag is not RowDragInfo foundInfo)
            return false;
        if (!ReferenceEquals(foundInfo.Siblings, dragSiblings) || ReferenceEquals(foundInfo.Item, dragItem))
            return false;

        // Above or below the target row depending on which half of it the cursor is over, so
        // crossing into either half moves the dragged item to the slot that's visually implied.
        double targetTop = found.TransformToVisual(AppsList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        target = found;
        targetInfo = foundInfo;
        after = dropPoint.Y - targetTop > found.ActualHeight / 2;
        return true;
    }

    /// <summary>
    /// Moves the dragged item to wherever the cursor implies <em>right now</em> — called every tick
    /// while dragging, not just at drop, so the list reflows live around the cursor exactly the way
    /// the dock's own strip (and the Windows taskbar) already do. <see cref="DockWindow.ReorderTopLevelItem"/>
    /// and <see cref="DockWindow.ReorderGroupChild"/> already no-op when the target slot hasn't
    /// actually changed, so calling this unconditionally every tick is cheap.
    /// </summary>
    private void TryLiveReorder(DockItem dragItem, IList<DockItem> dragSiblings)
    {
        if (!TryFindDropTarget(dragItem, dragSiblings, out _, out var targetInfo, out bool after))
            return;

        int fromIndex = targetInfo.Siblings.IndexOf(dragItem);
        int toIndex = targetInfo.Siblings.IndexOf(targetInfo.Item) + (after ? 1 : 0);
        if (fromIndex < toIndex)
            toIndex--;
        if (toIndex == fromIndex)
            return;

        if (targetInfo.InsideGroup)
            targetInfo.Dock.ReorderGroupChild(dragItem, toIndex);
        else
            targetInfo.Dock.ReorderTopLevelItem(dragItem, toIndex);
    }

    /// <summary>Builds the floating drag ghost — a compact icon+name copy of <paramref name="item"/>,
    /// sized to match <paramref name="sourceRow"/> — and drops it into <c>DragGhostCanvas</c>.</summary>
    private void ShowDragGhost(DockItem item, FrameworkElement sourceRow)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var iconHost = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        if (item.IconImage is not null)
        {
            iconHost.Children.Add(new Image
            {
                Source = item.IconImage,
                Width = 28,
                Height = 28,
                Stretch = Stretch.Uniform,
            });
        }
        else
        {
            var glyph = new FontIcon
            {
                Glyph = item.Glyph,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 18,
            };
            if (item.GlyphTint is { } tint)
                glyph.Foreground = tint;
            iconHost.Children.Add(glyph);
        }
        content.Children.Add(iconHost);

        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.DisplayName) ? Loc.Get("Apps.Unnamed") : item.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        _dragGhost = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Width = sourceRow.ActualWidth,
            Child = content,
        };
        _dragGhostHalfHeight = sourceRow.ActualHeight / 2;

        DragGhostCanvas.Children.Add(_dragGhost);
        UpdateDragGhostPosition(_rowDragStartCursor);
    }

    /// <summary>Keeps the ghost centered on the cursor vertically, pinned to the list's left edge
    /// horizontally — the same "slides along one axis only" feel as the dock's own drag ghost.</summary>
    private void UpdateDragGhostPosition(NativeMethods.POINT cursorScreen)
    {
        if (_dragGhost is null)
            return;
        if (!NativeMethods.ScreenToClient(_hwnd, ref cursorScreen))
            return;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var rootPoint = new Windows.Foundation.Point(cursorScreen.X / scale, cursorScreen.Y / scale);
        var canvasPoint = RootGrid.TransformToVisual(DragGhostCanvas).TransformPoint(rootPoint);

        Canvas.SetLeft(_dragGhost, 0);
        Canvas.SetTop(_dragGhost, canvasPoint.Y - _dragGhostHalfHeight);
    }

    private void HideDragGhost()
    {
        if (_dragGhost is not null)
            DragGhostCanvas.Children.Remove(_dragGhost);
        _dragGhost = null;
    }

    /// <summary>The row currently representing <paramref name="item"/> in the Apps &amp; links list,
    /// if any — rows are rebuilt wholesale on every model change, so a row reference held across
    /// ticks goes stale the moment a live reorder rebuilds the list; this re-resolves it instead.</summary>
    private FrameworkElement? FindRowForItem(DockItem item)
    {
        foreach (var child in AppsList.Children)
            if (child is FrameworkElement row && row.Tag is RowDragInfo info && ReferenceEquals(info.Item, item))
                return row;
        return null;
    }

    /// <summary>Which row in the Apps &amp; links list, if any, contains the given point (in
    /// <c>AppsList</c>'s own coordinate space) — used to resolve a drag-reorder's drop target.</summary>
    private FrameworkElement? FindRowAt(Windows.Foundation.Point point)
    {
        foreach (var child in AppsList.Children)
        {
            if (child is not FrameworkElement row || row.Tag is not RowDragInfo)
                continue;
            var transform = row.TransformToVisual(AppsList);
            var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
            if (bounds.Contains(point))
                return row;
        }
        return null;
    }

    /// <summary>
    /// The second line of an Apps &amp; links row: what the item is, then the details that are
    /// otherwise invisible on this page — its target, an assigned shortcut, and whether a folder
    /// opens a stack rather than Explorer.
    /// <para>
    /// The shortcut matters most here. It is assigned from an icon's own right-click menu on the
    /// dock, so without this the only place it appears is Settings ▸ Shortcuts, and a row here
    /// would say nothing about an item that quietly owns a system-wide combination.
    /// </para>
    /// </summary>
    private static string SubtitleFor(DockItem item)
    {
        // A separator and a group have no target, so they get their kind alone rather than a
        // dangling "• " — a group's count is the useful detail in its place.
        var parts = new List<string> { KindLabel(item.Kind) };

        if (item.IsGroup)
            parts.Add(Loc.Format("Apps.GroupCount", item.Children.Count));
        else if (!item.IsSeparator)
            parts.Add(item.Target);

        if (item.Kind == DockItemKind.Folder && item.FolderFlyout)
            parts.Add(Loc.Get("Apps.ShowsContents"));

        if (HotkeyGesture.TryParse(item.Hotkey, out var gesture))
            parts.Add(gesture.ToString());

        return string.Join(" • ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// The "add to group" menu for an Apps & links row: every existing group on that item's own
    /// dock, plus "New group…" (opens the same modal the dock's own right-click menu uses).
    /// Mirrors <c>DockWindow.BuildMoveToGroupMenu</c> — same choices, same order — just reachable
    /// from Settings instead of a right-click on the dock itself.
    /// </summary>
    private static void ShowAddToGroupMenu(FrameworkElement anchor, DockWindow dock, DockItem item)
    {
        var menu = new MenuFlyout();

        foreach (var group in dock.Groups)
        {
            var entry = new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(group.DisplayName) ? Loc.Get("Kind.Group") : group.DisplayName,
            };
            var captured = group;
            entry.Click += (_, _) => dock.MoveItemToGroup(item, captured);
            menu.Items.Add(entry);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());

        var create = new MenuFlyoutItem { Text = Loc.Get("Menu.NewGroup") };
        create.Click += (_, _) => dock.Manager.OpenNewGroupWindow(dock, item);
        menu.Items.Add(create);

        menu.ShowAt(anchor);
    }

    private static string KindLabel(DockItemKind kind) => kind switch
    {
        DockItemKind.Application => Loc.Get("Kind.App"),
        DockItemKind.File => Loc.Get("Kind.File"),
        DockItemKind.Folder => Loc.Get("Kind.Folder"),
        DockItemKind.WebLink => Loc.Get("Kind.WebLink"),
        DockItemKind.Separator => Loc.Get("Kind.Separator"),
        DockItemKind.Group => Loc.Get("Kind.Group"),
        _ => kind.ToString(),
    };
}
