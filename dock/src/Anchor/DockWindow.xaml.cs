using System.Collections.ObjectModel;
using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Anchor;

/// <summary>
/// One dock strip: the always-on, borderless glass window that holds the app/file/folder/link
/// icons plus the settings gear. Owns window chrome and theming, the acrylic backdrop, the
/// item layout (horizontal or, when side-snapped, vertical), drag-to-move / drag-to-reorder
/// gestures, the per-item and background context menus, and its own placement. Snap + auto-hide
/// behavior lives in the <see cref="DockWindow"/> partial in <c>DockWindow.AutoHide.cs</c>, and
/// groups in <c>DockWindow.Groups.cs</c>.
/// <para>
/// Anchor can show several of these at once. Everything app-wide — the config file, the tray
/// icon, the global shortcut, the running-app poll, the Settings and Add windows — belongs to
/// <see cref="DockManager"/>; this class knows only about its own <see cref="DockProfile"/>.
/// </para>
/// </summary>
public sealed partial class DockWindow : Window
{
    private readonly nint _hwnd;
    private readonly WindowId _windowId;
    private readonly AppWindow _appWindow;
    private readonly AcrylicBackdropManager? _backdrop;
    private readonly DockManager _manager;
    private readonly DockProfile _profile;

    /// <summary>The visible items rendered on the dock (a projection of the master list that
    /// excludes hidden items). Reordering operates on this collection.</summary>
    public ObservableCollection<DockItem> Items { get; } = new();

    /// <summary>The full, ordered item list (including hidden items) — the persisted source
    /// of truth, surfaced to the Settings window.</summary>
    public IReadOnlyList<DockItem> AllItems => _profile.Items;

    /// <summary>This strip's persisted state: its items, edge, placement and hide behavior.</summary>
    public DockProfile Profile => _profile;

    /// <summary>App-wide settings, shared with every other dock.</summary>
    public DockConfig Config => _manager.Config;

    public DockManager Manager => _manager;

    /// <summary>
    /// The dock window's title. It never appears in a caption (the dock is borderless) or in the
    /// taskbar, but it is the top-level window's UI Automation name — which is how the UI smoke
    /// tests find the dock, and how it shows up in Spy++ / Task Manager.
    /// </summary>
    internal const string WindowTitle = "Anchor Dock";

    public DockWindow(DockManager manager, DockProfile profile, bool seedDefaults)
    {
        _manager = manager;
        _profile = profile;

        InitializeComponent();
        Title = WindowTitle;

        // Content fills the whole window (no reserved title bar). This also makes WinUI
        // size the content island's INPUT site to the full client area — without it, a
        // borderless window can end up with a 0x0 input site that silently swallows all
        // pointer input (no clicks / hover / drag reach the content).
        ExtendsContentIntoTitleBar = true;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);

        // Dock-like chrome: borderless, topmost, off the taskbar & Alt-Tab, rounded corners.
        // The DWM border is suppressed (and the immersive-dark-mode flag tracked to the theme) in
        // ApplyWindowBorder, applied below once the theme is known and re-asserted on every
        // activation because DWM otherwise restores the default (contrasting) rim.
        WindowChrome.MakeBorderlessToolWindow(_appWindow, _hwnd);
        WindowChrome.StripFrame(_hwnd);
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_DONOTROUND); // the outline is the region (ApplyShape)
        Activated += OnActivated;

        // Seed the starter items only on the very first run — never after the user has
        // intentionally emptied the dock, and never for a dock they added themselves.
        if (seedDefaults)
            SeedDefaults();

        // Apply the chosen Light/Dark/System theme to the dock's root. A High Contrast theme
        // always wins (ApplyTheme resolves to ElementTheme.Default), in which case the acrylic
        // "glass" is also suppressed below so the shell's high-contrast system colors come
        // through and the dock stays legible.
        ApplyTheme();

        // Match the rounded DWM border to the effective theme now, and keep it in step when the
        // theme changes — either the user's choice, or the OS light/dark setting while in System
        // mode (ActualThemeChanged covers both).
        ApplyWindowChrome();
        // The glass is re-mixed for the new theme first, then the rim painted from it: this handler runs before the
        // backdrop's own, so painting first read the outgoing theme's tint — a light line across the top of a dock
        // that had just gone dark, until something happened to repaint it.
        RootGrid.ActualThemeChanged += (_, _) => { ApplyGlass(); ApplyWindowChrome(); ReloadIconsForTheme(); };

        DockItemAnimations.ReducedMotion = !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        DockItemAnimations.HighContrast = IsHighContrast();
        DockItemAnimations.SetShowLabels(_manager.Config.ShowItemLabels);
        _showLabelsHandler = RefreshItemLabels;
        DockItemAnimations.ShowLabelsChanged += _showLabelsHandler;
        HookFastTooltips();
        HookVisualAnimationTimer();

        if (IsHighContrast())
        {
            if (Application.Current.Resources.TryGetValue(
                    "SolidBackgroundFillColorBaseBrush", out var bg) && bg is Brush brush)
                RootGrid.Background = brush; // opaque, since there is no backdrop behind it
        }
        else
        {
            // The Windows 11 taskbar "glass". Follows RootGrid's theme via its own
            // ActualThemeChanged subscription, so a later SetTheme re-tints it automatically.
            _backdrop = new AcrylicBackdropManager(this);
            _backdrop.Retinted += ApplyWindowBorder; // the rim keeps matching the glass
            _backdrop.TryApply();
            ApplyGlass(); // the user's accent-tint choice on top of the base recipe
        }

        ItemsHost.ItemsSource = Items;
        RebuildVisible();
        ApplyMetrics();
        ApplyStripLayout();
        // One subscription for the life of the window: it drives the cell highlight always, and
        // the magnify swell when that setting is on (see DockWindow.Magnify.cs).
        HookStripPointer();

        // ContextRequested (rather than RightTapped) so the dock menu is reachable by the
        // keyboard too (Menu key / Shift+F10), not only by right-click.
        DockStrip.ContextRequested += DockBackground_ContextRequested;
        // Items are Buttons now and mark PointerPressed handled for their own press visual;
        // subscribe with handledEventsToo so a press that starts on an icon still begins a
        // gesture (item reorder for icons, window drag for the background).
        DockStrip.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Dock_PointerPressed), handledEventsToo: true);
        RootGrid.Loaded += (_, _) =>
        {
            ApplyWindowChrome();
            QueueRelayout();
        };

        Closed += OnClosed;

        // Modest initial size so the first frame isn't full-screen before relayout.
        _appWindow.Resize(new SizeInt32(360, 96));

        _ = LoadIconsAsync();
    }

    /// <summary>Set once the window has closed, for a late timer tick or lazily created timer to see.</summary>
    private bool _closed;

    private void OnActivated(object sender, WindowActivatedEventArgs e) => ApplyWindowChrome();

    // Named, and every handler of the window's own removed here (this one included): a handler on
    // the window's own event that references the window is held by the native window for the life
    // of the process, so lambdas here kept every dock that was ever closed (removed, or rebuilt by
    // a language change or an import) alive with its whole visual tree. WindowEventLifetimeTests
    // pins the rule.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Activated -= OnActivated;
        Closed -= OnClosed;
        _pollTimer?.Stop();
        _slideTimer?.Stop();
        _dragTimer?.Stop();
        _barCellDragTimer?.Stop();
        _groupCloseTimer?.Stop();
        _visualAnimTimer?.Stop();
        _dropShellWatchdog?.Stop();
        _presenceTimer?.Stop();
        UnhookAutoHideEvents();
        TrackSwipeBridge.RemoveDockRect(_hwnd);
        DockItemAnimations.ShowLabelsChanged -= _showLabelsHandler;
        _backdrop?.Dispose();
        NativeReclaim.Request();
    }

    // ---- Master / visible list sync ---------------------------------------

    /// <summary>Rebuilds the visible collection from the master list, honoring Hidden flags.</summary>
    private void RebuildVisible()
    {
        Items.Clear();
        foreach (var it in _profile.Items)
            if (!it.Hidden)
                Items.Add(it);
        SyncOpenApps();
    }

    /// <summary>
    /// Pushes a visible reorder back into the master list. Hidden items stay anchored at their
    /// absolute master indices; each visible slot is refilled, in order, from the (reordered)
    /// visible collection. Deterministic and stable.
    /// </summary>
    private void SyncMasterFromVisible()
    {
        // The drop shell is a visual, not an item: it lives in the visible collection for the
        // life of a drag (see DockWindow.DropTargets) and has nothing to write back.
        var q = new Queue<DockItem>(Items.Where(i => !i.IsPlaceholder && !i.IsTransient));
        for (int i = 0; i < _profile.Items.Count && q.Count > 0; i++)
            if (!_profile.Items[i].Hidden)
                _profile.Items[i] = q.Dequeue();
    }

    // ---- Public API (used by the Settings / Add windows) ------------------

    public void AddDockItem(DockItem item) => InsertDockItem(Items.Count, item);

    /// <summary>
    /// Adds an item at a given slot in the <em>visible</em> strip — which is what a drop knows,
    /// since that is the strip the drop shell opened a slot in. The slot is mapped back onto the
    /// master list, so a hidden neighbour doesn't quietly shift where the item lands.
    /// </summary>
    public void InsertDockItem(int visibleIndex, DockItem item)
    {
        // Never into the open-apps section: that stays the strip's tail.
        int pinned = PinnedCount;
        visibleIndex = Math.Clamp(visibleIndex, 0, pinned);

        // Past the last visible item means the end of the master list, hidden tail and all;
        // otherwise the new item goes exactly where the visible item it displaces sits.
        int master = visibleIndex < pinned ? _profile.Items.IndexOf(Items[visibleIndex]) : -1;
        _profile.Items.Insert(master < 0 ? _profile.Items.Count : master, item);

        if (!item.Hidden)
            Items.Insert(visibleIndex, item);
        PersistAndRelayout();
        RaiseItemsChanged();
        EnsurePresenceAnimation(); // one kept in the dock from its menu grows in
        _ = LoadOneIconAsync(item);
    }

    public void RemoveDockItem(DockItem item)
    {
        _profile.Items.Remove(item);
        Items.Remove(item);
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    public void SetItemHidden(DockItem item, bool hidden)
    {
        if (item.Hidden == hidden)
            return;
        item.Hidden = hidden;
        RebuildVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    // ---- Drag & drop onto the dock ---------------------------------------
    //
    // Drop apps, shortcuts (.lnk), files or folders from Explorer / the desktop straight onto
    // the dock to add them; a dropped URL (from a browser) becomes a web link. Apps dragged out
    // of the Start menu count too, though they are not files and arrive in a different shape
    // entirely — Services/ShellDrop.cs is what makes both look the same from here.

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var data = e.DataView;
        if (ShellDrop.HasShellItems(data) ||
            data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink) ||
            data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (e.DragUIOverride is { } ui)
            {
                ui.Caption = Loc.Get("Dock.DropCaption");
                ui.IsCaptionVisible = true;
                ui.IsGlyphVisible = true;
            }

            // Part the strip around the slot this drop would land in — the same cue a reorder
            // gives, and the slot the drop below then actually uses.
            TrackDropShell(e);
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var data = e.DataView;
            if (ShellDrop.HasShellItems(data))
            {
                // The shell stays open across the read and is then replaced by what the read
                // resolved, so the strip parts once instead of closing and re-opening.
                AddDroppedItems(await ShellDrop.ReadAsync(data), TakeDropSlot());
            }
            else if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
            {
                AddWebLinkFromDrop((await data.GetWebLinkAsync())?.ToString(), TakeDropSlot());
            }
            else if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                AddWebLinkFromDrop(await data.GetTextAsync(), TakeDropSlot());
            }
        }
        catch (Exception ex)
        {
            Diag.Log("Drop failed: " + ex.Message);
        }
        finally
        {
            // The drag is over: what it was carrying must not answer for the next one, and the
            // slot it opened closes even when nothing came of it (a drop of unusable text).
            _dragPayload = null;
            ClearDropShell();
            deferral.Complete();
        }
    }

    /// <summary>
    /// Adds everything a drop resolved to, in the order it was dropped, starting at the slot the
    /// drop shell was holding open — so a drop lands where it was aimed rather than on the end of
    /// the strip. Several files dropped together fill the slots after it, keeping their order.
    /// </summary>
    private void AddDroppedItems(IReadOnlyList<ShellDrop.DroppedItem> dropped, int at)
    {
        foreach (var item in dropped)
            InsertDockItem(at++, item.ToDockItem());
    }

    private void AddWebLinkFromDrop(string? text, int at)
    {
        if (DockItemFactory.TryNormalizeWebUrl(text) is not { } url)
            return;

        InsertDockItem(at, new DockItem
        {
            Kind = DockItemKind.WebLink,
            DisplayName = DockItemFactory.SuggestName(url),
            Target = url,
        });
    }

    /// <summary>Opens the Add window targeting <em>this</em> dock, whichever one it is.</summary>
    public void OpenAddNew() => _manager.OpenAddNew(this);

    public void OpenSettings() => _manager.OpenSettings();

    private void RaiseItemsChanged() => _manager.NotifyItemsChanged();

    private void PersistAndRelayout()
    {
        SaveConfig();
        QueueRelayout();
    }

    // ---- Seed content -----------------------------------------------------

    private void SeedDefaults()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Add(DockItemKind.Application, "File Explorer", Path.Combine(win, "explorer.exe"));
        Add(DockItemKind.Application, "Notepad", Path.Combine(sys, "notepad.exe"));
        Add(DockItemKind.Application, "Calculator", Path.Combine(sys, "calc.exe"));
        Add(DockItemKind.Folder, "Home", profile);
        Add(DockItemKind.WebLink, "WinUI Gallery", "https://github.com/microsoft/WinUI-Gallery");

        void Add(DockItemKind kind, string name, string target) =>
            _profile.Items.Add(new DockItem { Kind = kind, DisplayName = name, Target = target });
    }

    /// <summary>Persists the whole configuration — every dock shares one file.</summary>
    private void SaveConfig() => _manager.Save();

    /// <summary>
    /// Resolves every item's icon, groups included: a group's children never appear on the strip
    /// but do appear in its fly-out, so they need icons too.
    /// <para>
    /// Resolved concurrently rather than one at a time: each icon is independent (a shell
    /// thumbnail lookup or a favicon fetch with its own timeout), and awaiting them in sequence
    /// means one slow or unreachable web link holds up every icon after it — on a dock with
    /// several web links and no connectivity, that is several times ten seconds before the last
    /// icon even starts resolving.
    /// </para>
    /// </summary>
    private Task LoadIconsAsync()
    {
        var items = _profile.Items.ToArray()
            .SelectMany(item => item.Children.ToArray().Prepend(item));
        return Task.WhenAll(items.Select(LoadOneIconAsync));
    }

    // ---- Size & position (bottom-center, above the taskbar) ---------------

    private bool _relayoutQueued;

    private void QueueRelayout()
    {
        if (_relayoutQueued)
            return;
        _relayoutQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _relayoutQueued = false;
            try
            {
                UpdateSizeAndPosition();
            }
            catch (Exception ex)
            {
                Diag.Log("UpdateSizeAndPosition failed: " + ex);
            }
        });
    }

    // Dock metrics — keep in sync with the item template, Strip and DockStrip in DockWindow.xaml.
    // Per-item cell sizes are NOT here: they vary by kind (a separator is a narrow slot), so they
    // live on DockItem.CellExtent, which the sizing and reorder math below both read.
    //
    // The two that scale with the density setting are properties over DockMetrics rather than
    // constants, so a density change moves the gear cell and the divider with the items instead
    // of leaving the window sized for the old geometry.
    internal static double CellSize => DockMetrics.Cell;          // gear cell (taskbar-ish)
    internal static double DividerLength => DockMetrics.DividerLength; // Divider long side
    internal const double CellSpacing = 4;    // StackLayout + Strip Spacing
    internal const double DividerWidth = 1;   // Divider Rectangle thickness (short side)
    // The same all round, so the glass's corner can run parallel to a cell's (see ApplyShape).
    internal const double StripPadX = 6;      // DockStrip Padding (left/right)
    internal const double StripPadY = 6;      // DockStrip Padding (top/bottom)
    internal const double AddNewWidth = 116;  // "+ Add New" empty-state pill: minimum width

    /// <summary>
    /// Pushes the current <see cref="DockMetrics"/> geometry onto the parts of the strip that are
    /// plain XAML rather than item bindings — the gear cell and the empty-state pill — and asks
    /// every item to re-read its own. Called at construction and whenever the density changes.
    /// </summary>
    private void ApplyMetrics()
    {
        SettingsButton.Width = DockMetrics.Cell;
        SettingsButton.Height = DockMetrics.Cell;
        SettingsButton.CornerRadius = new CornerRadius(DockMetrics.CellCorner);
        SettingsGlyph.FontSize = DockMetrics.Glyph;
        AddNewButton.Height = DockMetrics.Cell;

        foreach (var item in _profile.Items)
        {
            item.RefreshMetrics();
            foreach (var child in item.Children)
                child.RefreshMetrics();
        }
        RefreshOpenAppMetrics();
    }

    /// <summary>Re-applies the density (and re-sizes the window for it). Called by
    /// <see cref="DockManager.SetDensity"/> on every dock, since density is app-wide.</summary>
    public void ApplyDensity()
    {
        ApplyMetrics();
        QueueRelayout();
    }

    /// <summary>Reorders the gear/divider relative to user items per <see cref="DockConfig.SettingsPosition"/>.</summary>
    public void ApplyStripLayout()
    {
        bool leading = _manager.Config.SettingsPosition == SettingsPosition.Leading;
        int gearIndex = leading ? 0 : Strip.Children.Count - 1;
        int dividerIndex = leading ? 1 : Strip.Children.Count - 2;

        if (Strip.Children.IndexOf(SettingsButton) != gearIndex)
            Strip.Children.Move((uint)Strip.Children.IndexOf(SettingsButton), (uint)gearIndex);
        if (Strip.Children.IndexOf(Divider) != dividerIndex)
            Strip.Children.Move((uint)Strip.Children.IndexOf(Divider), (uint)dividerIndex);

        int itemsIndex = leading ? 2 : 1;
        if (Strip.Children.IndexOf(ItemsHost) != itemsIndex)
            Strip.Children.Move((uint)Strip.Children.IndexOf(ItemsHost), (uint)itemsIndex);

        int addNewIndex = leading ? Strip.Children.Count - 1 : 0;
        if (Strip.Children.IndexOf(AddNewButton) != addNewIndex)
            Strip.Children.Move((uint)Strip.Children.IndexOf(AddNewButton), (uint)addNewIndex);
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualAnimTimer;
    private Action? _showLabelsHandler;

    /// <summary>Re-reads label visibility after the app-wide show-labels setting changes.</summary>
    public void RefreshItemLabels()
    {
        foreach (var item in _profile.Items)
        {
            item.RefreshMetrics();
            foreach (var child in item.Children)
                child.RefreshMetrics();
        }
        RefreshOpenAppMetrics();
        QueueRelayout();
    }

    public void QueueRelayoutPublic() => QueueRelayout();

    private void HookVisualAnimationTimer()
    {
        if (DockItemAnimations.ReducedMotion)
            return;

        _visualAnimTimer = DispatcherQueue.CreateTimer();
        _visualAnimTimer.Interval = TimeSpan.FromMilliseconds(8);
        _visualAnimTimer.Tick += (_, _) =>
        {
            Probe.Count("visualFrame");
            bool any = false;
            const double hoverStep = 0.28;
            const double magnifyStep = 0.22;
            foreach (var item in Items)
                any |= item.AnimateVisuals(hoverStep, magnifyStep);
            if (!any)
                _visualAnimTimer?.Stop();
        };
    }

    private void EnsureVisualAnimationRunning()
    {
        if (DockItemAnimations.ReducedMotion || _visualAnimTimer is null)
            return;
        if (!_visualAnimTimer.IsRunning)
            _visualAnimTimer.Start();
    }

    // The pill's actual width. Measured rather than fixed at AddNewWidth because its caption is
    // translated, and "Hinzufügen" or "डॉक में जोड़ें" is wider than the English "Add New" that
    // constant was sized for — a fixed width would clip them.
    private double _addNewWidth = AddNewWidth;

    /// <summary>
    /// True when the dock should lay out vertically: the "vertical when side-snapped" option is
    /// on, the dock is snapped to the left or right edge, and it has at least one item (the
    /// empty-state "+ Add New" pill is always horizontal). Top/bottom and floating stay horizontal.
    /// </summary>
    private bool IsVertical =>
        _profile.VerticalWhenSideSnapped &&
        _profile.Snapped &&
        _profile.Edge is DockEdge.Left or DockEdge.Right &&
        Items.Count > 0;

    /// <summary>Flips the strip, the item layout and the divider between horizontal and vertical.</summary>
    private void ApplyOrientation(bool vertical)
    {
        Strip.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        if (ItemsHost.Layout is StackLayout stack)
            stack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        // Cell sizes are per-item template bindings (a separator's slot is narrow along the flow
        // and full-width across it), so the orientation has to reach the items themselves.
        foreach (var item in Items)
            item.SetFlowVertical(vertical);

        // The divider is a thin line laid across the strip's flow, so its long/short sides swap
        // with the orientation (a vertical bar between horizontal items, a horizontal bar between
        // vertical items), and it's centered on the cross axis.
        Divider.Width = vertical ? DividerLength : DividerWidth;
        Divider.Height = vertical ? DividerWidth : DividerLength;
        Divider.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Divider.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
    }

    /// <summary>Shows the "+ Add New" pill (and hides the item strip) when the dock is empty.</summary>
    private void UpdateEmptyState()
    {
        bool empty = Items.Count == 0;
        AddNewButton.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ItemsHost.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty)
            return;

        // Measure at natural size (a Collapsed element measures to zero, hence the early return
        // above), then pin the pill to that so the window sizing below has an exact number.
        AddNewButton.Width = double.NaN;
        AddNewButton.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        _addNewWidth = Math.Max(AddNewWidth, Math.Ceiling(AddNewButton.DesiredSize.Width));
        AddNewButton.Width = _addNewWidth;
    }

    private void UpdateSizeAndPosition()
    {
        Probe.Count("relayout");
        UpdateEmptyState();

        // Compute the strip size analytically from the (uniform) cell metrics. This is
        // deterministic and avoids the window-shrinks-then-clips-content feedback loop
        // that plagues "auto-size window to content" via ActualWidth.
        int n = Items.Count; // visible items
        bool empty = n == 0;
        bool vertical = IsVertical; // false when empty
        ApplyOrientation(vertical);

        // Along the strip's flow: [items OR add-new] [gap] [divider] [gap] [gear cell].
        // Across it: a single cell. Which of these is the window's width vs. height depends on
        // whether the dock is laid out vertically. Cells are summed rather than multiplied out:
        // they are not all the same size (a separator takes a narrow slot).
        // The gear and its divider are optional; collapsed, the strip's spacing skips them too.
        bool gear = _manager.Config.ShowSettingsButton;
        var gearVisibility = gear ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsButton.Visibility != gearVisibility)
        {
            SettingsButton.Visibility = gearVisibility;
            Divider.Visibility = gearVisibility;
        }

        double coreMain = empty ? _addNewWidth : ItemsExtent() + (n - 1) * CellSpacing;
        double contentMain = gear ? coreMain + CellSpacing + DividerWidth + CellSpacing + CellSize : coreMain;
        double dipW = vertical ? CellSize + 2 * StripPadX : contentMain + 2 * StripPadX;
        double dipH = vertical ? contentMain + 2 * StripPadY : CellSize + 2 * StripPadY;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)Math.Ceiling(dipW * scale);
        int h = (int)Math.Ceiling(dipH * scale);

        // Which monitor is the dock on? Prefer the display under its stored position (so a dock
        // dropped on a secondary screen stays and hides on THAT screen); otherwise the display
        // nearest the window. This is the fix for snap/hide "jumping" across monitors.
        var display = ResolveDisplay(w, h);
        var work = display.WorkArea;      // excludes the taskbar — where the dock shows
        _outer = display.OuterBounds;     // full monitor — where the dock hides (behind the taskbar)
        int margin = (int)Math.Round(8 * scale);
        int x, y;

        if (_profile.Snapped)
        {
            // Flush to the snapped edge; along the edge, keep the position the user placed it at
            // (falling back to centered only if it has never been positioned).
            // A little off the edge, as a Mac's Dock floats above the bottom of the screen. (Tucked away, it still
            // hides behind the edge itself: see HiddenCoord.)
            int gap = (int)Math.Round(FloatGap * scale);
            (x, y) = _profile.Edge switch
            {
                DockEdge.Top => (AlongX(work, w), work.Y + gap),
                DockEdge.Left => (work.X + gap, AlongY(work, h)),
                DockEdge.Right => (work.X + work.Width - w - gap, AlongY(work, h)),
                _ => (AlongX(work, w), work.Y + work.Height - h - gap), // Bottom
            };
        }
        else if (_profile.FreeX is int fx && _profile.FreeY is int fy)
        {
            // Kept centred, a floating dock grows and shrinks around its own middle. That takes
            // the size it was last laid out at, so only while it still sits where it was laid out:
            // a drag that just ended has moved it, and FreeX is then simply where it was dropped.
            if (_profile.KeepCentered && _shownRect.Width > 0 && _shownRect.Width != w && fx == _shownRect.X)
            {
                fx = _shownRect.X + (_shownRect.Width - w) / 2;
                _profile.FreeX = fx;
            }
            x = Math.Clamp(fx, work.X, work.X + work.Width - w);
            y = Math.Clamp(fy, work.Y, work.Y + work.Height - h);
        }
        else
        {
            // Default free position: bottom-center with a small margin, fully visible.
            x = work.X + (work.Width - w) / 2;
            y = work.Y + work.Height - h - margin;
        }

        // While the strip is parted around a drop shell, the dock grows from where it already is
        // instead of re-deriving its placement. A centered dock would otherwise slide half a cell
        // sideways the moment the gap opened — which moves every icon on the strip, and takes the
        // gap itself out from under the cursor that asked for it, rather than parting the two
        // cells the drop goes between. The dock settles back to its usual placement when the
        // shell closes, whether the drop landed or not. The cross axis is pinned along with it,
        // which is safe because a cell added to the flow never changes it.
        if (_dropShell is not null && _shownRect.Width > 0)
        {
            x = Math.Clamp(_shownRect.X, work.X, Math.Max(work.X, work.X + work.Width - w));
            y = Math.Clamp(_shownRect.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
        }

        _shownRect = new RectInt32(x, y, w, h);
        // Where it shows, for TrackSwipe's Mission Control to keep its windows clear of.
        TrackSwipeBridge.PublishDockRect(_hwnd, x, y, x + w, y + h);
        _manager.PublishIconsSoon(); // icons may have moved
        _work = work;
        // Placed where it is now rather than where it would show: tucked away, it is resized
        // there, instead of flashing into view for a moment on every relayout (which the open-apps
        // animation does each frame).
        _appWindow.MoveAndResize(PlacementFor(_shownRect));
        ApplyShape(w, h, scale);
        ApplyTopmost();
        ApplyWindowChrome();
        OnRelayoutApplied();
    }

    /// <summary>How far a snapped dock stands off its edge, in DIPs.</summary>
    internal const double FloatGap = 6;

    /// <summary>The glass's corner radius in DIPs: a cell's hover corner plus the padding around it, so the two curves run parallel.</summary>
    internal static double GlassCorner => DockMetrics.CellCorner + StripPadY;

    private (int W, int H, int R) _shape;

    /// <summary>
    /// The dock's outline: a rounded rectangle whose corner is a hovered cell's corner plus the padding around the
    /// cell (<see cref="GlassCorner"/>), so the highlight's curve and the glass's run parallel, as on a Mac. DWM's own
    /// window rounding is a fixed 8 px, too tight for that, so the window is given this shape as its region — which
    /// clips the glass too. Set only when the size or radius changes.
    /// </summary>
    private void ApplyShape(int w, int h, double scale)
    {
        int r = (int)Math.Round(GlassCorner * scale);
        if (_shape == (w, h, r))
            return;
        _shape = (w, h, r);
        nint region = NativeMethods.CreateRoundRectRgn(0, 0, w + 1, h + 1, 2 * r, 2 * r);
        if (region != 0 && NativeMethods.SetWindowRgn(_hwnd, region, true) == 0)
            NativeMethods.DeleteObject(region); // the window took it only if the call succeeded
    }

    /// <summary>Total DIPs the visible cells occupy along the strip's flow (gaps excluded).</summary>
    private double ItemsExtent()
    {
        double total = 0;
        foreach (var item in Items)
            total += item.CellExtent;
        return total;
    }

    // Along-edge coordinates: the middle of the edge when kept centred (or never placed), otherwise
    // derived from the stored placement, clamped to the work area.
    private int AlongX(RectInt32 work, int w) =>
        !_profile.KeepCentered && _profile.FreeX is int fx
            ? Math.Clamp(fx, work.X, work.X + Math.Max(0, work.Width - w))
            : work.X + (work.Width - w) / 2;

    private int AlongY(RectInt32 work, int h) =>
        !_profile.KeepCentered && _profile.FreeY is int fy
            ? Math.Clamp(fy, work.Y, work.Y + Math.Max(0, work.Height - h))
            : work.Y + (work.Height - h) / 2;

    /// <summary>Resolves the monitor the dock belongs to (multi-monitor safe).</summary>
    private DisplayArea ResolveDisplay(int w, int h)
    {
        DisplayArea? da = null;
        if (_profile.FreeX is int fx && _profile.FreeY is int fy)
        {
            var center = new PointInt32(fx + w / 2, fy + h / 2);
            da = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest);
        }
        da ??= DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest);
        return da;
    }

    // Last computed "shown" rect, the current work area, and the full monitor bounds (physical
    // px), shared with the auto-hide/snap controller (see DockWindow.AutoHide.cs). The dock shows
    // within the work area but hides against the outer (screen) edge so a bottom-snapped dock
    // tucks behind the taskbar.
    private RectInt32 _shownRect;
    private RectInt32 _work;
    private RectInt32 _outer;

    partial void OnRelayoutApplied();

    // ---- Interaction ------------------------------------------------------
    //
    // Hover, pressed and keyboard-focus visuals come from Button itself (the Windows 11
    // subtle-fill control states), so there is no hand-rolled hover animation here.

    // NOTE: ItemsRepeater does NOT set FrameworkElement.DataContext on realized items
    // (x:Bind resolves via generated code, not DataContext). We stash the item in Tag via
    // Tag="{x:Bind}" on the template's cell Grid and read it back here — walking up from the
    // sender, because the element that raised the event (the launch Button) is a child of the
    // cell that carries the Tag.
    private static DockItem? ItemOf(object sender) => FindItemFromSource(sender);

    // Button.Click fires for a pointer click AND a keyboard invoke (Space/Enter), so this one
    // handler covers mouse, touch and keyboard. Suppressed after a drag gesture.
    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (_dragOccurred)
        {
            Diag.Log("Item_Click suppressed: drag/reorder in progress");
            return; // the click that ends a drag/reorder, not a launch
        }
        var item = ItemOf(sender);
        Diag.Log($"Item_Click: tag={(item is null ? "NULL" : item.DisplayName)}");
        if (item is null)
            return;

        // A group has nothing to launch: a click toggles its fly-out over the icon that was
        // clicked — open if it wasn't already the one showing, closed if it was.
        if (item.IsGroup)
        {
            ToggleGroupFlyout((FrameworkElement)sender, item);
            return;
        }

        // A folder set to "show contents" opens the same bar over its own contents instead of
        // handing the folder to Explorer.
        if (item.Kind == DockItemKind.Folder && item.FolderFlyout)
        {
            ShowFolderFlyout((FrameworkElement)sender, item.Target);
            return;
        }

        LaunchOrFocus(item);
    }

    /// <summary>
    /// Opens an item: brings an already-running app's window forward if there is one, otherwise
    /// launches it. Holding Shift forces a fresh instance the way the Windows 11 taskbar does.
    /// Shared by the dock strip and the fly-out bars.
    /// </summary>
    private void LaunchOrFocus(DockItem item)
    {
        // Read the modifier from the keyboard rather than the event args: Button.Click carries no
        // modifier state, and fires for keyboard invokes too.
        bool forceNewInstance = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        // An app started from here gets Dash to Dock's launch animation; one already open, which
        // only comes forward, doesn't.
        if (_manager.LaunchOrFocus(item, forceNewInstance) && item.Kind == DockItemKind.Application && !ReducedMotion)
            PlayLaunchZoom(item);
    }

    /// <summary>
    /// Captures the item's icon as drawn on the strip, at twice its size, and hands it to the
    /// manager's <see cref="LaunchZoom"/> to zoom out over it.
    /// </summary>
    private async void PlayLaunchZoom(DockItem item)
    {
        try
        {
            int index = Items.IndexOf(item);
            if (index < 0 || ItemsHost.TryGetElement(index) is not FrameworkElement cell)
                return;
            FrameworkElement? icon = FindVisible<Image>(cell) ?? (FrameworkElement?)FindVisible<FontIcon>(cell);
            if (icon is null || icon.ActualWidth <= 0 || icon.ActualHeight <= 0)
                return;

            double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            double longest = Math.Max(icon.ActualWidth, icon.ActualHeight);
            var capture = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await capture.RenderAsync(icon, (int)Math.Round(icon.ActualWidth * 2 * scale), (int)Math.Round(icon.ActualHeight * 2 * scale));
            var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await capture.GetPixelsAsync());
            int w = capture.PixelWidth, h = capture.PixelHeight;
            if (w <= 0 || h <= 0 || pixels.Length < w * h * 4)
                return;

            // Square, centred: a glyph's box needn't be.
            int side = Math.Max(w, h);
            var square = new byte[side * side * 4];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(pixels, row * w * 4, square, ((row + (side - h) / 2) * side + (side - w) / 2) * 4, w * 4);

            var centre = icon.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(icon.ActualWidth / 2, icon.ActualHeight / 2));
            var origin = _appWindow.Position;
            _manager.LaunchZoom.Play(square, side,
                origin.X + (int)Math.Round(centre.X * scale), origin.Y + (int)Math.Round(centre.Y * scale));
        }
        catch (Exception ex)
        {
            Diag.Log("Launch animation failed: " + ex.Message);
        }

        static T? FindVisible<T>(DependencyObject root) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && match.Visibility == Visibility.Visible)
                    return match;
                if (child is UIElement { Visibility: Visibility.Collapsed })
                    continue;
                if (FindVisible<T>(child) is { } found)
                    return found;
            }
            return null;
        }
    }

    // ContextRequested fires for right-click and for the keyboard context-menu gesture
    // (Menu key / Shift+F10), so the per-item menu is reachable without a mouse.
    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var target = (FrameworkElement)sender;
        if (target.Tag is not DockItem item)
            return;
        if (item.IsTransient)
        {
            // An open app's icon has its own, Mac-like menu; the section's divider has none, so
            // the press falls through to the dock's.
            if (!item.IsSeparator)
                ShowOpenAppMenu(target, item, e);
            return;
        }

        int index = Items.IndexOf(item);
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = MenuStyle };

        // An app's menu is the taskbar's: its jump list, then the app itself, unpinning and
        // closing, with Anchor's own commands for the icon gathered under Options rather than
        // pushing those down the screen. Everything else (folders, files, links, groups,
        // separators) keeps the flat list of Anchor's commands.
        bool isApp = item.Kind == DockItemKind.Application && !item.IsGroup && !item.IsSeparator;
        var options = new MenuFlyoutSubItem { Text = Loc.Get("Menu.Options") };
        IList<MenuFlyoutItemBase> sink = isApp ? options.Items : menu.Items;
        if (isApp)
            AddTaskbarSection(menu, item, () => RemoveDockItem(item), Loc.Get("Menu.RemoveFromDock"));

        // A separator has no target to open, no name and no icon, so its menu is just the
        // placement/removal commands below. A group has both a name and an icon, so it is edited
        // here like anything else — the editor simply leaves out the target field it has no use
        // for.
        if (!item.IsSeparator)
        {
            if (!isApp)
            {
                sink.Add(item.IsGroup
                    ? Mi(Loc.Get("Menu.OpenGroup"), () => ShowGroupFlyout(target, item))
                    : Mi(Loc.Get("Menu.Open"), () => LaunchOrFocus(item)));
            }

            // Rename and "change icon" are not separate commands any more: both are fields of this
            // one editor, which is a single panel where the menu used to carry three entries
            // opening three of them. Dropping back to the item's own icon stays a command of its
            // own, since it is one action with no form to fill in.
            sink.Add(Mi(Loc.Get("Menu.Edit"), () => _manager.OpenItemEditor(this, item)));
            if (item.HasCustomIcon)
                sink.Add(Mi(Loc.Get("Menu.ResetIcon"), () => SetCustomIcon(item, null)));

            if (item.IsGroup)
            {
                sink.Add(Mi(Loc.Get("Menu.Ungroup"), () => Ungroup(item)));
            }
            else
            {
                // A folder can either hand itself to Explorer or open a stack of its contents.
                // One entry that always names the other state, and deliberately NOT a
                // ToggleMenuFlyoutItem: a single checkable entry makes WinUI reserve a check
                // column for *every* item in the same menu (the CheckPlaceholder visual state,
                // worth 28px), so a folder's menu sat noticeably further right than an app's for
                // the sake of one row. The label carries the state instead, and every item's menu
                // lines up the same way.
                if (item.Kind == DockItemKind.Folder)
                {
                    sink.Add(Mi(
                        Loc.Get(item.FolderFlyout
                            ? "Menu.OpenFolderInExplorer"
                            : "Menu.ShowFolderContents"),
                        () => SetFolderFlyout(item, !item.FolderFlyout)));
                }

                if (Launcher.SupportsFileLocation(item))
                    sink.Add(Mi(Loc.Get("Menu.OpenFileLocation"), () => Launcher.OpenFileLocation(item)));

                sink.Add(BuildMoveToGroupMenu(target, item));

                // Both of these are hidden rather than shown greyed out: with one dock there is
                // nowhere to move an item to, and with per-item shortcuts switched off nothing an
                // item is given here would fire. An entry that can only be disabled is one more
                // line to read past every time the menu opens.
                if (_manager.Docks.Count > 1)
                    sink.Add(BuildMoveToDockMenu(item));

                // Groups are excluded: a shortcut fires with the dock hidden and possibly
                // off-screen, and a group has nothing to do except open a fly-out that would have
                // nowhere to appear.
                if (_manager.Config.ItemHotkeysEnabled)
                    sink.Add(BuildItemHotkeyMenu(target, item));
            }

            sink.Add(new MenuFlyoutSeparator());
        }

        var moveLeft = Mi(Loc.Get("Menu.MoveLeft"), () => MoveItem(item, -1));
        moveLeft.IsEnabled = index > 0;
        sink.Add(moveLeft);

        var moveRight = Mi(Loc.Get("Menu.MoveRight"), () => MoveItem(item, +1));
        moveRight.IsEnabled = index >= 0 && index < PinnedCount - 1;
        sink.Add(moveRight);

        sink.Add(new MenuFlyoutSeparator());
        sink.Add(Mi(Loc.Get("Menu.Hide"), () => SetItemHidden(item, true)));
        if (isApp)
        {
            // Options sits with the app's own commands; closing it comes last, as on the taskbar.
            menu.Items.Add(options);
            AddCloseSection(menu, item);
        }
        else
        {
            menu.Items.Add(Mi(Loc.Get("Menu.Remove"), () => RemoveDockItem(item)));
        }

        // The cursor is about to sit over the menu, off the dock's own bounds; hold auto-hide out
        // while it is up (same pattern as ShowPanelFlyout) so the dock doesn't slide away mid-click.
        PauseAutoHideForDrag();
        menu.Closed += (_, _) => ResumeAutoHideAfterDrag();

        if (e.TryGetPosition(target, out var pos))
            menu.ShowAt(target, pos);
        else
            menu.ShowAt(target); // keyboard-invoked: let the platform place it on the element
        e.Handled = true;

        static MenuFlyoutItem Mi(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }
    }

    private void MoveItem(DockItem item, int direction)
    {
        int i = Items.IndexOf(item);
        int j = i + direction;
        if (i < 0 || j < 0 || j >= PinnedCount)
            return;
        Items.Move(i, j);
        SyncMasterFromVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Swaps a top-level item with its neighbor one slot earlier (-1) or later (+1) among ALL
    /// items — hidden ones included. Used by the Settings "Apps &amp; links" page, which lists
    /// (and can reorder) hidden items too; the dock's own "Move left/right" context menu uses
    /// <see cref="MoveItem"/> instead, since a right-click there only ever reaches a visible item
    /// and "left/right" means the visible strip order, not this full one.
    /// </summary>
    public void MoveTopLevelItem(DockItem item, int direction)
    {
        var list = _profile.Items;
        int i = list.IndexOf(item);
        int j = i + direction;
        if (i < 0 || j < 0 || j >= list.Count)
            return;
        (list[i], list[j]) = (list[j], list[i]);
        RebuildVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Moves a top-level item to an arbitrary slot among ALL items (hidden ones included), for the
    /// Settings "Apps &amp; links" page's drag-to-reorder rows. Unlike <see cref="MoveTopLevelItem"/>,
    /// which only swaps adjacent neighbors, this drops the item directly where the drag ended.
    /// </summary>
    public void ReorderTopLevelItem(DockItem item, int toIndex)
    {
        var list = _profile.Items;
        int from = list.IndexOf(item);
        if (from < 0)
            return;
        toIndex = Math.Clamp(toIndex, 0, list.Count - 1);
        if (from == toIndex)
            return;
        list.RemoveAt(from);
        list.Insert(toIndex, item);
        RebuildVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    private void DockBackground_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (e.TryGetPosition(fe, out var pos))
            ShowDockMenu(fe, pos);
        else
            ShowDockMenu(fe, new Windows.Foundation.Point(0, 0));
        e.Handled = true;
    }

    private void ShowDockMenu(FrameworkElement target, Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = MenuStyle };

        menu.Items.Add(MenuItem(Loc.Get("Menu.AddNew"), OpenAddNew));
        menu.Items.Add(MenuItem(Loc.Get("Menu.AddSeparator"), AddSeparator));
        menu.Items.Add(MenuItem(Loc.Get("Menu.NewGroup"), () => _manager.OpenNewGroupWindow(this, null)));
        // Search has no shortcut until the user assigns one, so the dock's own menu is the other
        // way in — the tray menu alone would leave it undiscoverable from the dock itself.
        menu.Items.Add(MenuItem(Loc.Get("Menu.Search"), _manager.OpenSearch));
        menu.Items.Add(MenuItem(Loc.Get("Menu.Settings"), OpenSettings));

        menu.Items.Add(new MenuFlyoutSeparator());

        var snap = new MenuFlyoutSubItem { Text = Loc.Get("Menu.Snap") };
        snap.Items.Add(SnapItem(Loc.Get("Edge.Bottom"), DockEdge.Bottom));
        snap.Items.Add(SnapItem(Loc.Get("Edge.Top"), DockEdge.Top));
        snap.Items.Add(SnapItem(Loc.Get("Edge.Left"), DockEdge.Left));
        snap.Items.Add(SnapItem(Loc.Get("Edge.Right"), DockEdge.Right));
        menu.Items.Add(snap);

        // Where the gear sits on the strip. One entry naming the end it will move to, rather than
        // a checkable pair: a single checkable entry makes WinUI indent every other item in the
        // menu by a check column (see the folder entry in Item_ContextRequested).
        // Only while there is a gear to move.
        bool gearLeading = _manager.Config.SettingsPosition == SettingsPosition.Leading;
        if (_manager.Config.ShowSettingsButton)
        {
            menu.Items.Add(MenuItem(
                Loc.Get(gearLeading ? "Menu.SettingsToEnd" : "Menu.SettingsToStart"),
                () => _manager.SetSettingsPosition(
                    gearLeading ? SettingsPosition.Trailing : SettingsPosition.Leading)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        // Only offer to remove this strip when there would still be one left; Anchor with no
        // dock at all is a tray icon and no obvious way back.
        if (_manager.Config.Docks.Count > 1)
            menu.Items.Add(MenuItem(Loc.Get("Menu.RemoveDock"), () => _manager.RemoveDock(_profile)));
        menu.Items.Add(MenuItem(Loc.Get("Menu.AddDock"), () => _manager.AddDock()));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(MenuItem(Loc.Get("Menu.Restart"), _manager.Restart));
        // Hosted by TrackSwipe, the dock is turned off from TrackSwipe's menu, which would only
        // start it again after a quit from here.
        if (!Hosting.ByTrackSwipe)
            menu.Items.Add(MenuItem(Loc.Get("Menu.Quit"), _manager.Quit));

        // Same reasoning as the per-item menu: keep the dock put while this one is open.
        PauseAutoHideForDrag();
        menu.Closed += (_, _) => ResumeAutoHideAfterDrag();

        menu.ShowAt(target, at);

        static MenuFlyoutItem MenuItem(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        MenuFlyoutItem SnapItem(string text, DockEdge edge)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => SetSnap(edge);
            return mi;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_dragOccurred)
            return; // the click that ends a drag, not a menu open
        OpenSettings();
    }

    private void AddNew_Click(object sender, RoutedEventArgs e)
    {
        if (_dragOccurred)
            return;
        OpenAddNew();
    }

    /// <summary>
    /// Loads one item's icon at this dock's own display scale. An instance method, not a static
    /// one, precisely so it can read that scale: the decode size has to follow the monitor the
    /// dock is on, and on a mixed-DPI setup two docks disagree about what it is.
    /// </summary>
    private async Task LoadOneIconAsync(DockItem item)
    {
        // Store apps have artwork made for light and for dark backgrounds: the one for how the dock looks now.
        var icon = await IconService.LoadIconAsync(item, NativeMethods.GetDpiForWindow(_hwnd) / 96.0,
            lightBackground: RootGrid.ActualTheme == ElementTheme.Light);
        if (icon is not null)
            item.IconImage = icon;
    }

    /// <summary>
    /// The dock turned light or dark (its own setting, or Windows' when it follows the system): every
    /// icon again, so Store apps show the artwork made for the new background.
    /// </summary>
    private void ReloadIconsForTheme()
    {
        if (_closed)
            return;
        _ = LoadIconsAsync();
        foreach (var item in Items)
            if (item.IsTransient && !item.IsSeparator)
                _ = LoadOneIconAsync(item);
    }

    // ---- Separators & custom icons ----------------------------------------

    /// <summary>Appends a divider to the end of the dock (dock menu, Settings, Add window).</summary>
    public void AddSeparator() => AddDockItem(new DockItem
    {
        Kind = DockItemKind.Separator,
        DisplayName = Loc.Get("Kind.Separator"),
    });

    /// <summary>
    /// Pins a user-supplied image onto an item, or clears it (null) so the shell/favicon icon
    /// comes back. The old bitmap is dropped first so the glyph shows while the new one loads.
    /// Clears any picked <see cref="DockItem.CustomGlyph"/> too — the two are mutually exclusive.
    /// </summary>
    public void SetCustomIcon(DockItem item, string? path)
    {
        item.CustomIconPath = string.IsNullOrWhiteSpace(path) ? null : path;
        item.CustomGlyph = null;
        item.IconImage = null;
        SaveConfig();
        RaiseItemsChanged();
        _ = LoadOneIconAsync(item);
    }

    /// <summary>
    /// Switches a folder between opening in Explorer and opening a fly-out of its contents.
    /// </summary>
    public void SetFolderFlyout(DockItem item, bool on)
    {
        if (item.Kind != DockItemKind.Folder || item.FolderFlyout == on)
            return;
        item.FolderFlyout = on;
        SaveConfig();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Pins a built-in glyph (from the icon picker) onto an item, or clears it (null) so the
    /// kind's default glyph comes back. Clears any custom image path too — mutually exclusive
    /// with <see cref="DockItem.CustomIconPath"/>. Needs no async resolution: the glyph renders
    /// the moment it's set.
    /// </summary>
    public void SetCustomGlyph(DockItem item, string? glyph)
    {
        item.CustomGlyph = string.IsNullOrWhiteSpace(glyph) ? null : glyph;
        item.CustomIconPath = null;
        item.IconImage = null;
        SaveConfig();
        RaiseItemsChanged();
    }

    // ---- Snap / free positioning -----------------------------------------

    public void SetSnap(DockEdge? edge)
    {
        if (edge is DockEdge e)
        {
            _profile.Snapped = true;
            _profile.Edge = e;
            // Keep the current on-screen position as the placement anchor so it snaps flush
            // without jumping to the screen center.
            _profile.FreeX = _shownRect.X;
            _profile.FreeY = _shownRect.Y;
        }
        else
        {
            // Unsnap: leave it visible where it currently shows.
            _profile.Snapped = false;
            _profile.FreeX = _shownRect.X;
            _profile.FreeY = _shownRect.Y;
        }
        SaveConfig();
        // Reposition (snap flush or clamp free) FIRST so _shownRect/_outer reflect the new
        // edge, THEN start/stop hide-behind — otherwise ApplyAutoHide computes its slide
        // target from the stale (pre-snap) geometry and the relayout that follows hard-jumps
        // the window to correct it, which reads as a reset-then-instant-hide instead of one
        // smooth slide.
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    public void SetAutoHide(bool on)
    {
        _profile.AutoHide = on;
        SaveConfig();
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    public void SetKeepCentered(bool on)
    {
        _profile.KeepCentered = on;
        SaveConfig();
        UpdateSizeAndPosition();
    }

    public void SetDodgeWindows(bool on)
    {
        _profile.DodgeWindows = on;
        SaveConfig();
        ApplyAutoHide();
    }

    public void SetAlwaysOnTop(bool on)
    {
        _profile.AlwaysOnTop = on;
        SaveConfig();
        ApplyTopmost();
    }

    public void SetVerticalWhenSideSnapped(bool on)
    {
        _profile.VerticalWhenSideSnapped = on;
        SaveConfig();
        QueueRelayout(); // re-orient (and re-size) if the dock is currently snapped to a side
    }

    // ---- Theme ------------------------------------------------------------

    /// <summary>
    /// Resolves the <see cref="ElementTheme"/> to request for any Anchor window given the chosen
    /// <see cref="DockTheme"/>. A High Contrast accessibility theme always wins (returns
    /// <see cref="ElementTheme.Default"/> so the window follows the system HC colors).
    /// </summary>
    internal static ElementTheme ResolveTheme(DockTheme theme)
    {
        if (IsHighContrast())
            return ElementTheme.Default;
        return theme switch
        {
            DockTheme.Light => ElementTheme.Light,
            DockTheme.System => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
    }

    /// <summary>
    /// Applies the configured theme to the dock's root. Called by <see cref="DockManager.SetTheme"/>
    /// on every dock, since the theme is app-wide.
    /// <para>
    /// Setting <c>RequestedTheme</c> fires <c>RootGrid.ActualThemeChanged</c> synchronously, which
    /// runs both this window's own handler (re-paints the DWM rim, subscribed in the constructor)
    /// and the backdrop's handler (re-tints the acrylic for the new theme) — in subscription
    /// order, rim first. That leaves the rim reading the backdrop's <em>outgoing</em> tint for one
    /// pass, and it visibly lingers (a mismatched ring around freshly-retinted glass) until
    /// something else repaints it. Re-syncing and re-painting explicitly afterward, in the right
    /// order, makes the switch atomic instead of leaving that stale frame on screen.
    /// </para>
    /// </summary>
    public void ApplyTheme()
    {
        RootGrid.RequestedTheme = ResolveTheme(_manager.Config.Theme);
        // Re-mix the glass through ApplyGlass rather than re-syncing the backdrop directly:
        // syncing alone re-derives the recipe from the shell colors and drops the accent-tint
        // choice Personalize had folded in, so a theme switch would quietly reset the user's
        // glass to the neutral default until they touched that setting again.
        ApplyGlass();
        ApplyWindowChrome();
    }

    /// <summary>
    /// Re-colors the rounded DWM rim to disappear into the dock's glass. The color is the glass's
    /// own tint, so it tracks the theme and the accent-tint option together and the rim matches
    /// the glass exactly.
    /// <para>
    /// Erring dark is deliberate. The rim cannot be right for every wallpaper — the glass's
    /// rendered color depends on what is behind the window, which is unknowable from here — and a
    /// rim slightly darker than the glass reads as the shadow under a rounded edge, while one
    /// slightly brighter reads as an outline drawn around the dock.
    /// </para>
    /// </summary>
    private void ApplyWindowBorder()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        var tint = _backdrop?.Current.Tint ?? (dark ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3));

        WindowChrome.SetWindowBorderColor(_hwnd, dark, tint);

        static Windows.UI.Color Rgb(byte r, byte g, byte b) =>
            Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private void ApplyWindowChrome()
    {
        ApplyWindowBorder();
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_DONOTROUND); // the region draws the corners
    }

    // The dock is topmost while snapped (so the auto-hide reveal shows over other windows), and
    // while floating only when the user has opted into "always on top".
    private bool ShouldBeTopmost => _profile.Snapped || _profile.AlwaysOnTop;

    private void ApplyTopmost()
    {
        bool top = ShouldBeTopmost;
        if (_appWindow.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = top;
        if (top)
            WindowChrome.EnsureTopmost(_hwnd);
        else
            WindowChrome.SetNotTopmost(_hwnd);
    }

    /// <summary>
    /// Re-places this dock after something outside the window moved it — a monitor change from
    /// Settings, say — so the new coordinates are honored and auto-hide re-computed for the edge
    /// it now sits on.
    /// </summary>
    public void RelayoutAfterExternalMove()
    {
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    // ---- Visibility (driven by the tray menu, via DockManager) -------------

    /// <summary>Hides this dock until it is explicitly summoned back (tray "Hide dock").</summary>
    public void HideByUser()
    {
        // Stop the auto-hide controller first: it polls the cursor and would otherwise keep
        // moving (and re-showing) a window the user has asked to be rid of.
        PauseAutoHideForDrag();
        _appWindow.Hide();
    }

    /// <summary>Puts a user-hidden dock back on screen, without stealing focus.</summary>
    public void ShowAfterUserHide()
    {
        _appWindow.Show(activateWindow: false);
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    /// <summary>
    /// Brings this dock into view and to the front: cancels an auto-hide slide (and holds it out
    /// for the usual settle period) and re-asserts top-most Z-order. With
    /// <paramref name="takeFocus"/> it also becomes the foreground window — only one dock can, so
    /// <see cref="DockManager.BringToFront"/> passes true for just one of them.
    /// </summary>
    public void BringToFront(bool takeFocus = true)
    {
        try
        {
            RevealNow();
            WindowChrome.EnsureTopmost(_hwnd);
            if (!takeFocus)
                return;
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
        catch (Exception ex)
        {
            Diag.Log("BringToFront failed: " + ex);
        }
    }

    /// <summary>Pulls the dock fully back into view immediately. Implemented in the auto-hide
    /// partial, which owns the slide state.</summary>
    partial void RevealNow();

    /// <summary>Clears the dock, re-seeds the default items and returns to a floating position.</summary>
    public void ResetToDefaults()
    {
        _profile.Items.Clear();
        SeedDefaults();
        _profile.Snapped = false;
        _profile.FreeX = null;
        _profile.FreeY = null;
        RebuildVisible();
        SaveConfig();
        UpdateSizeAndPosition();
        ApplyAutoHide();
        RaiseItemsChanged();
        _ = LoadIconsAsync();
    }

    // ---- Dragging ---------------------------------------------------------
    //
    // Dragging a top-level window under the cursor is racy with WinUI pointer capture
    // (the cursor outruns the moving window and slips off it, dropping capture). So we
    // poll the global cursor and left-button state on a timer instead — rock solid
    // regardless of which window the cursor is currently over.
    //
    // A press that starts on an item reorders THAT item (never moves the dock); a press on
    // the background / divider / gear moves the whole dock window.

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragTimer;
    private bool _dragging;
    private bool _dragOccurred;  // suppress the launch "tap" that may follow a drag/reorder
    private NativeMethods.POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private DockItem? _reorderItem; // non-null while a press started on an item
    private double _reorderOriginPx; // screen px of the item host's leading edge along the flow axis
    private Windows.Foundation.Point _reorderOriginDip; // same origin, in RootGrid-relative DIPs (for the drag ghost)
    private double _reorderScale;    // physical px per DIP, captured at gesture start
    private bool _reorderVertical;   // captured at gesture start so mid-drag stays consistent
    private const int DragThreshold = 12;

    /// <summary>How much the dragged cell itself swells for the life of the gesture — the same
    /// magnification channel the group drop-target cue uses (see
    /// <see cref="DockWindow.SetDropTarget"/>), so the icon actually being moved reads as
    /// unmistakably different from the rest of the strip reflowing around it.</summary>
    private const double DragItemSwell = 1.15;

    private void Dock_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
            return;

        _dragOccurred = false; // reset on every press so a prior drag never eats this click
        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragging = false;

        // Did the press land on a dock item? If so this gesture is an item reorder, not a
        // window move — "don't allow dragging of the dock when moving the apps / items".
        _reorderItem = FindItemFromSource(e.OriginalSource);
        // The open-apps divider isn't something to move; a press on it moves the dock, like any
        // other bit of background.
        if (_reorderItem is { IsTransient: true, IsSeparator: true })
            _reorderItem = null;
        if (_reorderItem is null)
            _dragStartWindow = _appWindow.Position;

        _dragTimer ??= CreateDragTimer();
        if (!_dragTimer.IsRunning)
            _dragTimer.Start();
    }

    /// <summary>Walks up from the pressed element to find the dock item it belongs to (if any).</summary>
    private static DockItem? FindItemFromSource(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Tag is DockItem item)
                return item;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDragTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(8);
        t.Tick += (_, _) => DragTick();
        return t;
    }

    private void DragTick()
    {
        // Button released -> end the gesture.
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0)
        {
            _dragTimer?.Stop();
            bool wasDragging = _dragging;
            _dragging = false;

            if (_reorderItem is not null)
            {
                var dragged = _reorderItem;
                _reorderItem = null;
                if (wasDragging)
                    EndItemReorder(dragged);
                else
                    SetDropTarget(null);
                CatchUpOpenApps();
                return;
            }

            if (wasDragging)
            {
                EndDragSnap();
                // Clear the drag flag once the trailing click (the pointer-release that ended
                // the drag) has been delivered and suppressed. Low priority runs after input
                // delivery, so a later keyboard invoke (Enter/Space) is not blocked.
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dragOccurred = false);
            }
            return;
        }

        NativeMethods.GetCursorPos(out var cur);
        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;

        // ---- Item reorder path ----
        if (_reorderItem is not null)
        {
            if (!_dragging)
            {
                if (Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold)
                    return; // still a potential click/launch
                _dragging = true;
                _dragOccurred = true;
                PauseAutoHideForDrag();
                BeginItemReorder();
            }
            UpdateItemReorder(cur.X, cur.Y);
            return;
        }

        // ---- Window move path ----
        if (!_dragging)
        {
            if (Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold)
                return; // still a potential click
            _dragging = true;
            _dragOccurred = true;
            PauseAutoHideForDrag();
        }

        _appWindow.Move(new PointInt32(_dragStartWindow.X + dx, _dragStartWindow.Y + dy));
    }

    // ---- Item reorder mechanics ----

    private void BeginItemReorder()
    {
        // The window is stationary during a reorder, so the strip's screen geometry is fixed:
        // capture the item host's leading edge and the DPI scale once. When the dock is vertical
        // the items flow down the Y axis, so track Y instead of X.
        _reorderVertical = IsVertical;
        _reorderScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var origin = ItemsHost.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        _reorderOriginPx = _reorderVertical
            ? _appWindow.Position.Y + origin.Y * _reorderScale
            : _appWindow.Position.X + origin.X * _reorderScale;
        _reorderOriginDip = origin;

        // Swell the dragged cell itself so it reads as "this is what's moving" rather than just
        // another cell in a strip that's shuffling around it — a reorder used to give no visual
        // cue at all for which icon was actually being held.
        _reorderItem?.SetMagnification(DragItemSwell);
        EnsureVisualAnimationRunning();
        _openAppHome = _reorderItem is { IsTransient: true } ? Items.IndexOf(_reorderItem) : -1;
        _pinSlot = -1;

        if (_reorderItem is not null)
            ShowDragGhost(_reorderItem);
    }

    /// <summary>
    /// Shows the floating copy of the dragged icon and dims its cell in the strip to a
    /// placeholder (see <see cref="DockItem.CellOpacity"/>) — together these are what make the
    /// reorder read as "you're holding this icon" instead of just watching the list reshuffle.
    /// Sized off the density's nominal icon/glyph, not the (possibly still-easing) magnified
    /// size, so the ghost doesn't visibly resize once the swell finishes animating in.
    /// </summary>
    private void ShowDragGhost(DockItem item)
    {
        item.SetDragging(true);

        DragGhost.Width = item.CellWidth;
        DragGhost.Height = item.CellHeight;
        DragGhost.CornerRadius = item.CellCorner;

        DragGhostImage.Source = item.IconImage;
        DragGhostImage.Visibility = item.ImageVisibility;
        DragGhostImage.Width = DragGhostImage.Height = DockMetrics.Icon;

        DragGhostGlyph.Glyph = item.Glyph;
        DragGhostGlyph.Visibility = item.GlyphVisibility;
        DragGhostGlyph.FontSize = DockMetrics.Glyph;

        DragGhostTintedGlyph.Glyph = item.Glyph;
        DragGhostTintedGlyph.Visibility = item.TintedGlyphVisibility;
        DragGhostTintedGlyph.FontSize = DockMetrics.Glyph;
        if (item.GlyphTint is { } tint)
            DragGhostTintedGlyph.Foreground = tint;

        DragGhost.Visibility = Visibility.Visible;
    }

    /// <summary>Moves the drag ghost to sit centered on the cursor along the strip's flow axis,
    /// pinned to the strip's own cross-axis position (icons don't lift off the row, only slide
    /// along it) — <paramref name="rel"/> is the same flow-axis DIP coordinate
    /// <see cref="UpdateItemReorder"/> already computes for hit-testing the drop slot.</summary>
    private void UpdateDragGhostPosition(double rel)
    {
        if (_reorderItem is null)
            return;

        double primary = rel - _reorderItem.CellExtent / 2;
        if (_reorderVertical)
        {
            Canvas.SetLeft(DragGhost, _reorderOriginDip.X);
            Canvas.SetTop(DragGhost, _reorderOriginDip.Y + primary);
        }
        else
        {
            Canvas.SetLeft(DragGhost, _reorderOriginDip.X + primary);
            Canvas.SetTop(DragGhost, _reorderOriginDip.Y);
        }
    }

    private void HideDragGhost(DockItem? item)
    {
        item?.SetDragging(false);
        DragGhost.Visibility = Visibility.Collapsed;
    }

    /// <summary>Maps the cursor onto the slot the dragged item should occupy, and slides the
    /// strip so that is the slot it sits in.</summary>
    private void UpdateItemReorder(int cursorScreenX, int cursorScreenY)
    {
        if (_reorderItem is null || _reorderScale <= 0)
            return;

        double coord = _reorderVertical ? cursorScreenY : cursorScreenX;
        double rel = (coord - _reorderOriginPx) / _reorderScale; // back into DIPs
        UpdateDragGhostPosition(rel);

        int count = Items.Count;
        if (count < 2)
            return;

        int from = Items.IndexOf(_reorderItem);
        if (from < 0)
            return;

        var overGroup = GroupAt(rel, _reorderItem);
        SetDropTarget(overGroup);
        if (overGroup is not null)
            return; // the drop will file it into the group; don't shuffle the strip underneath

        int slot = SlotAt(rel, _reorderItem);
        int pinned = PinnedCount;
        int target;
        if (_reorderItem.IsTransient)
        {
            // An open app dragged among the pinned icons (up to the divider) is kept there when
            // it's let go, as on a Mac; anywhere else, it goes back to its place in the section.
            _pinSlot = slot <= pinned ? slot : -1;
            target = _pinSlot >= 0 ? _pinSlot : _openAppHome;
        }
        else
        {
            // A pinned icon stays among the pinned ones: the section after them is the apps'.
            target = Math.Clamp(slot, 0, pinned - 1);
        }
        target = Math.Clamp(target, 0, count - 1);
        if (target != from)
            Items.Move(from, target);
    }

    /// <summary>
    /// The slot a cell placed at <paramref name="rel"/> — a position along the strip's flow, in
    /// DIPs from the item host's leading edge — should take. Cells are not a uniform pitch (a
    /// separator is a narrow slot), so this walks the strip accumulating each cell's own extent.
    /// It measures against the layout of the <b>other</b> items — <paramref name="exclude"/>, the
    /// one being placed, left out — and inserts where the position passes each one's midpoint:
    /// those midpoints don't move as the placed cell is re-inserted around them, so the result is
    /// stable instead of oscillating between two slots whenever a wide icon crosses a narrow
    /// separator.
    /// <para>
    /// Shared by both things that place a cell on the strip — an item being reordered, and the
    /// shell an incoming drag opens (see <c>DockWindow.DropTargets</c>) — so the two can never
    /// disagree about where a cell starts.
    /// </para>
    /// </summary>
    private int SlotAt(double rel, DockItem? exclude)
    {
        int slot = 0;
        double edge = 0;
        foreach (var item in Items)
        {
            if (ReferenceEquals(item, exclude))
                continue;
            if (rel > edge + item.CellExtent / 2)
                slot++;
            edge += item.CellExtent + CellSpacing;
        }
        return slot;
    }

    /// <summary>
    /// The group <paramref name="rel"/> falls in the middle of, if any: hovering the middle of a
    /// GROUP means "file it in here" rather than "put it beside here". Only the central band
    /// counts, so the outer thirds of a group's cell still reorder past it — otherwise a group
    /// would be impossible to move an item across.
    /// </summary>
    private DockItem? GroupAt(double rel, DockItem dragged)
    {
        if (!CanBeGrouped(dragged))
            return null;

        double edge = 0;
        foreach (var item in Items)
        {
            if (ReferenceEquals(item, dragged))
                continue;
            if (item.IsGroup &&
                rel > edge + item.CellExtent * 0.25 && rel < edge + item.CellExtent * 0.75)
                return item;
            edge += item.CellExtent + CellSpacing;
        }
        return null;
    }

    private void EndItemReorder(DockItem? dragged)
    {
        // Dropped onto a group: file it in there instead of committing the reorder. Read and
        // cleared before anything else, so an early return below can't leave a cell swelled.
        var group = _dropTarget;
        SetDropTarget(null);
        // Un-swell the dragged cell itself, the other half of the cue BeginItemReorder set.
        dragged?.SetMagnification(1);
        EnsureVisualAnimationRunning();
        HideDragGhost(dragged);

        if (dragged is { IsTransient: true })
        {
            // The section goes back exactly as the manager last described it, and the app is
            // pinned at the slot it was dropped in, if any — which then takes it out of the
            // section, since it is no longer unpinned.
            int pinAt = _pinSlot;
            _pinSlot = _openAppHome = -1;
            Items.Remove(dragged);
            // Pinned where it was dropped, with no grow: the icon was already standing there. The
            // manager then re-describes the section without it, which re-syncs the tail.
            if (pinAt >= 0)
                _manager.KeepInDock(this, dragged, pinAt, grow: false);
            else
                SyncOpenApps();
        }
        else if (group is not null && dragged is not null && !ReferenceEquals(group, dragged))
        {
            MoveItemToGroup(dragged, group);
        }
        else
        {
            SyncMasterFromVisible();
            SaveConfig();
            RaiseItemsChanged();
        }

        ResumeAutoHideAfterDrag();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dragOccurred = false);
    }

    /// <summary>On drop, snap to the nearest work-area edge if close enough, else float free.</summary>
    private void EndDragSnap()
    {
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        var work = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest).WorkArea;

        // Only snap when the dock is dropped essentially AT an edge (a small tolerance),
        // otherwise it floats freely wherever it was dropped.
        const int snapThreshold = 16;
        int dLeft = pos.X - work.X;
        int dTop = pos.Y - work.Y;
        int dRight = work.X + work.Width - (pos.X + size.Width);
        int dBottom = work.Y + work.Height - (pos.Y + size.Height);
        int min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        // Remember exactly where it was dropped: this anchors the snapped position along the
        // edge (so it hides where you left it) and identifies the monitor it lives on.
        _profile.FreeX = pos.X;
        _profile.FreeY = pos.Y;

        if (min <= snapThreshold)
        {
            _profile.Snapped = true;
            _profile.Edge = min == dBottom ? DockEdge.Bottom
                         : min == dTop ? DockEdge.Top
                         : min == dLeft ? DockEdge.Left
                         : DockEdge.Right;
        }
        else
        {
            _profile.Snapped = false;
        }

        SaveConfig();
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    // ---- Accessibility / small UI helpers ---------------------------------

    /// <summary>
    /// True when Windows is using a High Contrast theme. Guarded: on any failure (e.g. the
    /// setting is unavailable in this host) we assume false and keep the normal glass styling.
    /// </summary>
    private static bool IsHighContrast()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { return false; }
    }

    /// <summary>A flyout section header using the Fluent "body strong" type-ramp style.</summary>
    private static TextBlock FlyoutHeader(string text)
    {
        var tb = new TextBlock { Text = text };
        if (Application.Current.Resources.TryGetValue("BodyStrongTextBlockStyle", out var s) &&
            s is Style style)
            tb.Style = style;
        else
            tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return tb;
    }

    partial void ApplyAutoHide();
    partial void PauseAutoHideForDrag();
    partial void ResumeAutoHideAfterDrag();
}
