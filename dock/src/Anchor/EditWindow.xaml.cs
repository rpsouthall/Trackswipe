using Anchor.Controls;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Anchor;

/// <summary>
/// The item editor: one window carrying a pinned item's name, target and icon (or, for a group,
/// the icon's colour in place of a target), opened from an icon's right-click menu or from a row
/// in Settings ▸ Apps &amp; links.
/// <para>
/// A window rather than a fly-out over the dock, which is what this started as. The dock is a
/// strip a single cell tall, so an editor anchored on it has to be a popup — and a popup is
/// light-dismissed by the next popup, which is exactly what choosing an icon needs. Opening the
/// picker therefore took the half-finished edit away with it. Swapping the picker into the panel
/// in place avoided the dismissal but read as the editor vanishing. A real window has neither
/// problem: the picker opens as an ordinary flyout beside its field, and closing it leaves the
/// form untouched behind it.
/// </para>
/// <para>
/// Mica-backed, centered on the cursor and matched to the dock's theme and topmost state, like
/// <see cref="AddNewWindow"/> and <see cref="SettingsWindow"/>.
/// </para>
/// </summary>
public sealed partial class EditWindow : Window
{
    private readonly DockWindow _dock;
    private readonly DockItem _item;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;

    // The group's glyph colour as the form currently has it (#RRGGBB, or null for the theme's).
    // Held here and previewed rather than applied as it changes: a custom colour changes on every
    // step of a drag across the spectrum, and Escape has to be able to throw it away like the name.
    private string? _pendingColour;

    /// <param name="manager">App-wide state — the theme this window matches, above all.</param>
    /// <param name="dock">The dock that owns <paramref name="item"/>: every change is applied
    /// through it, so the strip and the config stay in step.</param>
    /// <param name="item">The item being edited.</param>
    public EditWindow(DockManager manager, DockWindow dock, DockItem item)
    {
        _dock = dock;
        _item = item;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("Edit.Title");
        SystemBackdrop = new MicaBackdrop();

        // Extend the Mica backdrop under the caption so the title bar matches a native Windows 11
        // window instead of showing an opaque strip. Must happen before the chrome theming below:
        // ExtendsContentIntoTitleBar re-extends the DWM frame into the client area, which resets
        // any border color already applied — set it any later and HideWindowBorder's effect gets
        // silently clobbered, leaving the default rim visible along the top edge.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyTheme(manager.Config.Theme);
        RootGrid.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        // Re-assert on activation: DWM otherwise restores its default rim on some state changes.
        Activated += OnActivated;

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            // Only outrank other apps when the dock itself currently does — otherwise this window
            // would needlessly float above everything (full-screen apps, video calls) even though
            // a floating, non-topmost dock doesn't need that.
            p.IsAlwaysOnTop = dock.Profile.Snapped || dock.Profile.AlwaysOnTop;
        }
        _appWindow.IsShownInSwitchers = true;

        // Sized to the form rather than the other way round: the window does not resize. A group
        // has its colour row where everything else has its target, so both are the same height.
        // The slack over the tightest fit is for translations that wrap a field caption to two lines.
        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 520, 280);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        LoadItem();

        // An icon can resolve — or be replaced from the picker — while this window is open, and
        // the preview and the hero both show it.
        _item.PropertyChanged += OnItemPropertyChanged;

        // Escape discards, which is what the Cancel button did before it went: closing an
        // unsubmitted form is already the discard.
        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        // The dock is where this was opened from and where the result lands, so hold it on screen
        // for as long as the editor is up rather than letting auto-hide slide it away mid-edit.
        _dock.HoldAutoHide(true);
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e) => ApplyChromeTheme();

    // Named, and every handler of the window's own removed here (this one included): a handler on
    // the window's own event that references the window is held by the native window for the life
    // of the process, so a lambda here kept every closed EditWindow and its whole visual tree
    // alive. WindowEventLifetimeTests pins the rule.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        _item.PropertyChanged -= OnItemPropertyChanged;
        _dock.HoldAutoHide(false);
        SystemBackdrop = null;
        NativeReclaim.Request();
    }

    /// <summary>The item this window is editing, so the manager can spot an editor already open on
    /// it rather than stacking a second one.</summary>
    internal DockItem Item => _item;

    /// <summary>Applies the given app theme to this window's root (called on open and whenever the
    /// choice changes while this window is open), and re-themes the chrome to match.</summary>
    internal void ApplyTheme(DockTheme theme)
    {
        RootGrid.RequestedTheme = DockWindow.ResolveTheme(theme);
        ApplyChromeTheme();
    }

    /// <summary>
    /// Tracks the caption buttons to the effective theme, and hides the DWM window rim by painting
    /// it this theme's surface color — this window extends its content into the title bar, so the
    /// rim survives only along the top edge, where anything that doesn't match the surface reads
    /// as a stray line above the title bar (see <see cref="WindowChrome.HideWindowBorder"/>).
    /// </summary>
    private void ApplyChromeTheme()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        WindowChrome.SetTitleBarTheme(_appWindow, dark);
        WindowChrome.HideWindowBorder(_hwnd, dark);
    }

    // ---- The form ----------------------------------------------------------

    private void LoadItem()
    {
        NameBox.Text = _item.DisplayName;
        TargetBox.Text = _item.Target;

        // A group points at nothing of its own: it has a name, an icon and the icon's colour.
        TargetPanel.Visibility = _item.IsGroup ? Visibility.Collapsed : Visibility.Visible;
        ColourPanel.Visibility = _item.IsGroup ? Visibility.Visible : Visibility.Collapsed;
        _pendingColour = _item.IconColor;

        // Browsing only makes sense for something on disk. A web link or a shell command is typed.
        BrowseButton.Visibility = _item.Kind is DockItemKind.Application or DockItemKind.File
                                              or DockItemKind.Folder
            ? Visibility.Visible
            : Visibility.Collapsed;

        RenderIcon();
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DockItem.IconImage) or nameof(DockItem.Glyph)
            or nameof(DockItem.HasCustomIcon))
            RenderIcon();
    }

    /// <summary>Draws the item's current icon into the hero and the picker button, and re-labels
    /// the hero from whatever is in the form.</summary>
    private void RenderIcon()
    {
        // The form's colour, not the item's, so a swatch shows on the hero before it is saved.
        // Only when there is one: a null Foreground would hide the glyph, not untint it.
        var tint = PendingTint();

        Fill(HeroIcon, 24);
        Fill(IconPreview, 20);
        HeroName.Text = string.IsNullOrWhiteSpace(_item.DisplayName)
            ? Loc.Get("Apps.Unnamed")
            : _item.DisplayName;
        HeroKind.Text = KindLabel(_item.Kind);
        // Nothing to put back when the item is showing the icon Windows (or the site) gave it.
        ResetIconButton.Visibility = _item.HasCustomIcon ? Visibility.Visible : Visibility.Collapsed;
        RenderSwatches();

        void Fill(Grid host, double size)
        {
            host.Children.Clear();
            if (_item.IconImage is not null)
            {
                host.Children.Add(new Image
                {
                    Source = _item.IconImage,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                });
            }
            else
            {
                var glyph = new FontIcon
                {
                    Glyph = _item.Glyph,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = size * 0.8,
                };
                if (tint is not null)
                    glyph.Foreground = tint;
                host.Children.Add(glyph);
            }
        }
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

    // ---- Icon --------------------------------------------------------------

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            // This window is a form barely 280 DIP tall, and a flyout is clipped to the window it
            // belongs to unless told otherwise — so the picker was squeezed into whatever was left
            // below the icon field and the presenter's own scroller took over, showing two rows of
            // swatches at a time. Unconstrained, the popup gets its own top-level window and opens
            // at the size of the whole grid, which is the point of showing the set unscrolled.
            ShouldConstrainToRootBounds = false,
        };
        flyout.Content = IconPickerPanel.Build(_hwnd, swatchStyle: null, selection =>
        {
            flyout.Hide();
            // Applied straight away rather than held until Save: the picker is its own confirmed
            // choice, and the preview beside the button is what says it landed.
            _dock.ApplyIconSelection(_item, selection);
        });
        flyout.ShowAt(ChangeIconButton, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private void ResetIcon_Click(object sender, RoutedEventArgs e) =>
        _dock.SetCustomIcon(_item, null);

    // ---- Colour (groups only) ----------------------------------------------

    /// <summary>What the custom picker opens on when there is no colour yet: the accent fallback
    /// from docs/design-colours.md, so the spectrum starts somewhere vivid rather than on black.</summary>
    private static readonly Windows.UI.Color CustomStart = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

    /// <summary>The brush the preview draws the glyph in, or null for the theme's text colour.</summary>
    private SolidColorBrush? PendingTint() =>
        !DockItemAnimations.HighContrast && DockItem.TryParseColor(_pendingColour, out var c)
            ? new SolidColorBrush(c)
            : null;

    /// <summary>
    /// Checks the swatch for the form's colour (and only that one — a toggle button checks itself
    /// on a click, including a click on the one already chosen, which must stay checked). A colour
    /// that is none of the presets lives on the custom swatch, which then fills with it.
    /// </summary>
    private void RenderSwatches()
    {
        if (!_item.IsGroup)
            return;

        // A colour only reaches a glyph: an image icon draws its own, and High Contrast draws in
        // Windows' colours whatever the item asks for. Left in place rather than hidden so the
        // form doesn't jump when the icon changes under it; dimmed as well as disabled, since a
        // disabled toggle greys its chrome but not the colour it holds.
        bool usable = _item.IconImage is null && !DockItemAnimations.HighContrast;
        SwatchRow.Opacity = usable ? 1 : 0.4;

        bool matched = false;
        foreach (var child in SwatchRow.Children)
        {
            if (child is not ToggleButton swatch)
                continue;
            swatch.IsEnabled = usable;
            if (!ReferenceEquals(swatch, CustomSwatch))
            {
                var hex = swatch.Tag as string;
                bool on = string.IsNullOrEmpty(hex)
                    ? _pendingColour is null
                    : string.Equals(hex, _pendingColour, StringComparison.OrdinalIgnoreCase);
                swatch.IsChecked = on;
                matched |= on;
            }
        }

        bool custom = !matched && _pendingColour is not null;
        CustomSwatch.IsChecked = custom;
        CustomSwatchFill.Fill = custom ? PendingTint() : null;
        CustomSwatchGlyph.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        var hex = (sender as FrameworkElement)?.Tag as string;
        _pendingColour = string.IsNullOrEmpty(hex) ? null : hex;
        RenderIcon();
    }

    private void CustomSwatch_Click(object sender, RoutedEventArgs e)
    {
        // Undo the toggle's own flip: it is checked by having a custom colour, not by being clicked.
        RenderSwatches();

        var picker = new ColorPicker
        {
            Color = DockItem.TryParseColor(_pendingColour, out var c) ? c : CustomStart,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        picker.ColorChanged += (_, args) =>
        {
            // The picker can report the colour it was opened on as it lays out; only a real move
            // is a choice, or merely opening it would swap the theme colour for the start colour.
            if (args.NewColor.Equals(args.OldColor))
                return;
            _pendingColour = DockItem.FormatColor(args.NewColor);
            RenderIcon();
        };

        var flyout = new Flyout
        {
            Content = picker,
            Placement = FlyoutPlacementMode.Bottom,
            // As for the icon picker: this window is too short to hold the spectrum, so give the
            // popup a window of its own rather than squeezing it into what is left below the row.
            ShouldConstrainToRootBounds = false,
        };
        flyout.ShowAt(CustomSwatch, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_item.Kind == DockItemKind.Folder)
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add("*");
                if (await picker.PickSingleFolderAsync() is { } folder)
                    TargetBox.Text = folder.Path;
            }
            else
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add("*");
                if (await picker.PickSingleFileAsync() is { } file)
                    TargetBox.Text = file.Path;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"Edit browse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- Commit ------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _dock.ApplyItemEdit(_item, NameBox.Text, _item.IsGroup ? null : TargetBox.Text,
                            _item.IsGroup ? _pendingColour : _item.IconColor);
        Close();
    }
}
