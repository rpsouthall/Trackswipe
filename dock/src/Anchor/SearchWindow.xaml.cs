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
/// Quick-launch search: a small acrylic card, summoned by its own shortcut, that filters every
/// item on every dock and launches the one you pick. The dock stays where it is — this is the way
/// to reach a pinned item without going and finding the strip, which is exactly the case a dock
/// that auto-hides behind a screen edge is worst at.
/// <para>
/// It searches items rather than the file system on purpose. Windows already has a search that
/// covers the disk; what it cannot do is "the thing I pinned, by the name I gave it", which is
/// the only thing Anchor knows better than the shell does.
/// </para>
/// </summary>
public sealed partial class SearchWindow : Window
{
    /// <summary>How many matches the list shows (see <see cref="ItemSearch.DefaultLimit"/>).</summary>
    private const int MaxResults = ItemSearch.DefaultLimit;

    private const int CardWidth = 460;
    // Taller than the fixed-height rows this used to hold: each result row now carries its own
    // vertical padding (see BuildRow) so the name and subtitle aren't crushed together, and the
    // card grows to match so roughly the same number of matches still fit without scrolling.
    private const int CardHeight = 440;

    private readonly DockManager _manager;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly AcrylicBackdropManager? _backdrop;
    private readonly List<DockItem> _matches = new();
    private bool _wasActivated;

    public SearchWindow(DockManager manager)
    {
        _manager = manager;
        InitializeComponent();

        Title = Loc.Get("Search.Title");
        ExtendsContentIntoTitleBar = true;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Same chrome as a dock: borderless, rounded, off the taskbar and Alt-Tab. This is a
        // summoned overlay, not a window the user manages.
        WindowChrome.MakeBorderlessToolWindow(_appWindow, _hwnd);
        WindowChrome.StripFrame(_hwnd);
        WindowChrome.EnsureRoundedCorners(_hwnd, small: false);

        RootGrid.RequestedTheme = DockWindow.ResolveTheme(manager.Config.Theme);
        _backdrop = new AcrylicBackdropManager(this);
        if (_backdrop.TryApply())
            _backdrop.Personalize(manager.Config.AccentTint);

        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, CardWidth, CardHeight);
        CenterOnCursorDisplay();

        // Summoned by a keystroke and dismissed by losing focus: attention went elsewhere, and an
        // overlay that lingers after that is in the way. Guarded on having been activated first —
        // a window gets a Deactivated on its way to the foreground, and acting on that one would
        // close the card before it ever appeared.
        Activated += OnActivated;
        Closed += OnClosed;

        Refresh(string.Empty);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
            _wasActivated = true;
        else if (_wasActivated)
            Close();
    }

    // Named, and every handler of the window's own removed here (this one included): a handler on
    // the window's own event that references the window is held by the native window for the life
    // of the process, so lambdas here kept every closed card, its UISettings subscription and its
    // whole visual tree alive (about 1.2 MB and 2 handles per open). WindowEventLifetimeTests pins
    // the rule.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        _backdrop?.Dispose();
        NativeReclaim.Request();
    }

    /// <summary>Puts the card on the display the cursor is on, a third of the way down — where
    /// the shell's own search lands, rather than dead centre over whatever is being worked on.</summary>
    private void CenterOnCursorDisplay()
    {
        try
        {
            var size = _appWindow.Size;
            var display = NativeMethods.GetCursorPos(out var cursor)
                ? DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary)
                : DisplayArea.Primary;
            var work = display.WorkArea;
            _appWindow.Move(new PointInt32(
                work.X + (work.Width - size.Width) / 2,
                work.Y + Math.Max(0, (work.Height - size.Height) / 3)));
        }
        catch (Exception ex)
        {
            Diag.Log("Search: could not place the card: " + ex.Message);
        }
    }

    /// <summary>Takes focus and puts the caret in the box — the point of the shortcut is that you
    /// can start typing straight away.</summary>
    public void FocusQuery()
    {
        NativeMethods.SetForegroundWindow(_hwnd);
        Activate();
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    // ---- Filtering ---------------------------------------------------------

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh(QueryBox.Text);

    /// <summary>Rebuilds the match list. The matching itself lives in <see cref="ItemSearch"/>.</summary>
    private void Refresh(string query)
    {
        _matches.Clear();
        _matches.AddRange(ItemSearch.Filter(_manager.AllItems(), query, MaxResults));

        Results.Items.Clear();
        foreach (var item in _matches)
            Results.Items.Add(BuildRow(item));

        bool any = _matches.Count > 0;
        Results.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any)
            Results.SelectedIndex = 0;
    }

    /// <summary>One result row: the item's icon, its name, and the dock it is pinned to (only
    /// when there is more than one dock, where "which one?" is the useful detail).</summary>
    private Grid BuildRow(DockItem item)
    {
        // Auto-height with its own vertical padding rather than a fixed 40 DIP: that left only the
        // icon's own height between the name and subtitle lines and the row above and below, so
        // every result read as one solid block of text. The padding both gives the two lines room
        // to breathe and, since it's paid by every row, spaces consecutive results apart too.
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 8, 4, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement icon = item.IconImage is not null
            ? new Image { Source = item.IconImage, Width = 24, Height = 24, Stretch = Stretch.Uniform }
            : new FontIcon
            {
                Glyph = item.Glyph,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 18,
            };
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.DisplayName) ? Loc.Get("Apps.Unnamed") : item.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = SubtitleFor(item),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, item.DisplayName);
        return grid;
    }

    private string SubtitleFor(DockItem item)
    {
        var target = item.Target ?? string.Empty;
        if (_manager.Config.Docks.Count <= 1)
            return target;

        var profile = _manager.Config.Docks.FirstOrDefault(
            d => d.Items.Contains(item) || d.Items.Any(i => i.Children.Contains(item)));
        return profile is null ? target : $"{_manager.LabelFor(profile)} • {target}";
    }

    // ---- Keyboard ----------------------------------------------------------

    // PreviewKeyDown so Up/Down reach this before the TextBox treats them as caret movement:
    // typing happens in the box but the arrows drive the list, which is what makes the card
    // usable without ever leaving the keyboard.
    private void QueryBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                Move(+1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                Move(-1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_matches.Count == 0)
            return;
        int next = Results.SelectedIndex + delta;
        // Wrap, so Up from the first result reaches the last one without a long press of Down.
        Results.SelectedIndex = (next % _matches.Count + _matches.Count) % _matches.Count;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        int index = Results.Items.IndexOf(e.ClickedItem);
        if (index >= 0)
            Launch(index);
    }

    private void LaunchSelected() => Launch(Results.SelectedIndex);

    private void Launch(int index)
    {
        if (index < 0 || index >= _matches.Count)
            return;
        var item = _matches[index];
        // Close first: launching brings another window forward, and a card still on screen behind
        // it would have to be dismissed by hand.
        Close();
        _manager.LaunchOrFocus(item, forceNewInstance: false);
    }
}
