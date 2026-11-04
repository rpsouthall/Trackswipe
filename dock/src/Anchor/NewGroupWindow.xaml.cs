using Anchor.Controls;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Anchor;

/// <summary>
/// The "create a group" popup: an icon box (the same picker <see cref="EditWindow"/>'s "Change
/// icon…" opens) and a name field, opened from the dock's own "New group" menu entry or from an
/// item's "Move to group ▸ New group…".
/// <para>
/// A window rather than the <see cref="ContentDialog"/> this started as, for the same reason
/// <see cref="EditWindow"/> is one: the dialog shared the dock's own tiny <c>XamlRoot</c>, so
/// opening the icon picker from inside it — the whole point of offering one — closed the
/// half-filled dialog on the way past. A real window keeps the picker as an ordinary flyout beside
/// its button instead.
/// </para>
/// <para>
/// When <see cref="_pendingItem"/> is given (opened via an item's "Move to group ▸ New group…")
/// it is filed into the group the moment it is created.
/// </para>
/// </summary>
public sealed partial class NewGroupWindow : Window
{
    /// <summary>The icon a group gets if it's never customized — matches <see cref="DockItem.Glyph"/>'s
    /// own default for a group.</summary>
    private const string DefaultGroupGlyph = ""; // FolderOpen

    private readonly DockWindow _dock;
    private readonly DockItem? _pendingItem;

    /// <summary>The dock this group is added to, so <see cref="DockManager.OpenNewGroupWindow"/>
    /// can tell whether a re-invocation targets the same window or must replace it.</summary>
    public DockWindow Dock => _dock;

    /// <summary>The item to be filed into the group once created, or null. Exposed for the same
    /// reason as <see cref="Dock"/>.</summary>
    public DockItem? PendingItem => _pendingItem;

    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private IconSelection? _pendingIcon;

    /// <param name="manager">App-wide state — the theme this window matches, above all.</param>
    /// <param name="dock">The dock the new group is added to.</param>
    /// <param name="pendingItem">An item to file into the group the moment it is created, or null
    /// when the group is created empty.</param>
    public NewGroupWindow(DockManager manager, DockWindow dock, DockItem? pendingItem)
    {
        _dock = dock;
        _pendingItem = pendingItem;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("Flyout.NewGroup");
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

        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 440, 270);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        RenderIcon();
        NameBox.Text = Loc.Get("Kind.Group");
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();

        // Escape discards, which is what a Cancel button does anyway: closing an unsubmitted form
        // is already the discard.
        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        // The dock is where this was opened from and where the result lands, so hold it on screen
        // for as long as this window is up rather than letting auto-hide slide it away mid-edit.
        _dock.HoldAutoHide(true);
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e) => ApplyChromeTheme();

    // Named, and every handler of the window's own removed here (this one included): a handler on
    // the window's own event that references the window is held by the native window for the life
    // of the process, so a lambda here kept every closed NewGroupWindow and its whole visual tree
    // alive. WindowEventLifetimeTests pins the rule.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        _dock.HoldAutoHide(false);
        SystemBackdrop = null;
        NativeReclaim.Request();
    }

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

    // ---- Icon ----------------------------------------------------------------

    private void RenderIcon()
    {
        IconPreview.Children.Clear();
        if (_pendingIcon is { FilePath: { } path })
        {
            IconPreview.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(path)),
                Width = 28,
                Height = 28,
                Stretch = Stretch.Uniform,
            });
        }
        else
        {
            IconPreview.Children.Add(new FontIcon
            {
                Glyph = _pendingIcon?.Glyph ?? DefaultGroupGlyph,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 22,
            });
        }
    }

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            // This window is a form barely 270 DIP tall — see EditWindow.ChangeIcon_Click for why
            // the picker needs to escape that instead of being squeezed into what's left below the
            // icon field.
            ShouldConstrainToRootBounds = false,
        };
        flyout.Content = IconPickerPanel.Build(_hwnd, swatchStyle: null, selection =>
        {
            flyout.Hide();
            _pendingIcon = selection;
            RenderIcon();
        });
        flyout.ShowAt(IconButton, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    // ---- Commit ------------------------------------------------------------

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var group = _dock.CreateGroup(NameBox.Text.Trim());
        if (_pendingItem is not null)
            _dock.MoveItemToGroup(_pendingItem, group);
        if (_pendingIcon is { } selection)
            _dock.ApplyIconSelection(group, selection);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
