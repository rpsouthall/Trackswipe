using Anchor.Models;
using Anchor.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Anchor;

/// <summary>
/// A Windows-app-style modal for adding a new dock entry: an app, file, folder, web link or a
/// free-form shortcut/command. Mica-backed, centered, and matches the dock's own topmost state
/// (see <see cref="SettingsWindow"/>). Not truly modal to the OS, but presented like the shell's
/// add-item dialogs. Something dragged onto it from Explorer, the Start menu or a browser fills the
/// form in (see "Drag &amp; drop" below).
/// </summary>
public sealed partial class AddNewWindow : Window
{
    private enum AddKind { App, File, Folder, Link, Shortcut, Separator, Group, DragDrop }

    private readonly DockManager _manager;
    private readonly DockWindow _dock;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private AddKind _kind = AddKind.App;
    private bool _nameEdited;
    private bool _closed;

    /// <summary>
    /// What a drop of <em>several</em> items put in the window, waiting for the Add button. Empty
    /// otherwise — a single item is shown in the fields themselves, where it can be edited.
    /// </summary>
    private readonly List<ShellDrop.DroppedItem> _dropped = [];

    /// <param name="manager">App-wide state (the theme this window matches, above all).</param>
    /// <param name="dock">The dock the new item is added to — with several on screen, the one
    /// whose menu or gear opened this window.</param>
    public AddNewWindow(DockManager manager, DockWindow dock)
    {
        _manager = manager;
        _dock = dock;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("Add.Title");
        SystemBackdrop = new MicaBackdrop();

        // Extend the Mica backdrop under the caption so the title bar matches a native Windows 11
        // window instead of showing an opaque strip. Must happen before the chrome theming below:
        // ExtendsContentIntoTitleBar re-extends the DWM frame into the client area, which resets
        // any border color already applied — set it any later and HideWindowBorder's effect
        // gets silently clobbered, leaving the default rim visible along the top edge.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Match the dock's chosen Light/Dark/System theme so this window reads the same, and keep
        // the caption buttons and window frame in step if the OS theme changes while System mode
        // is on.
        ApplyTheme(manager.Config.Theme);
        RootGrid.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        // Re-assert on activation: DWM otherwise restores its default rim on some state changes.
        Activated += OnActivated;

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            // Only outrank other apps when the dock itself currently does — otherwise this
            // dialog would needlessly float above everything (full-screen apps, video calls)
            // even though a floating, non-topmost dock doesn't need that.
            p.IsAlwaysOnTop = dock.Profile.Snapped || dock.Profile.AlwaysOnTop;
        }
        _appWindow.IsShownInSwitchers = true;

        // Wide enough for the eight type tiles at their translated widths (see AddNewWindow.xaml).
        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 740, 580);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        // Track manual edits to the name so an auto-suggested name doesn't clobber user input.
        NameBox.TextChanged += (_, _) =>
        {
            if (NameBox.FocusState != FocusState.Unfocused)
                _nameEdited = !string.IsNullOrEmpty(NameBox.Text);
        };

        SelectType(AddKind.App);

        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e) => ApplyChromeTheme();

    // Named, and every handler of the window's own removed here (this one included): a handler on
    // the window's own event that references the window is held by the native window for the life
    // of the process, so a lambda here kept every closed AddNewWindow and its whole visual tree
    // alive. WindowEventLifetimeTests pins the rule.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Activated -= OnActivated;
        Closed -= OnClosed;
        SystemBackdrop = null;
        NativeReclaim.Request();
    }

    /// <summary>Applies the given app theme to this window's root (called on open and whenever
    /// the choice changes while this window is open), and re-themes the chrome to match.</summary>
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

    // ---- Type selection ----------------------------------------------------

    private void TypeSelected(object sender, RoutedEventArgs e)
    {
        AddKind kind = ReferenceEquals(sender, AppType) ? AddKind.App
                     : ReferenceEquals(sender, FileType) ? AddKind.File
                     : ReferenceEquals(sender, FolderType) ? AddKind.Folder
                     : ReferenceEquals(sender, LinkType) ? AddKind.Link
                     : ReferenceEquals(sender, SeparatorType) ? AddKind.Separator
                     : ReferenceEquals(sender, GroupType) ? AddKind.Group
                     : ReferenceEquals(sender, DropType) ? AddKind.DragDrop
                     : AddKind.Shortcut;

        // Choosing a type by hand starts a fresh add, so a list left by a drop of several goes.
        _dropped.Clear();
        SelectType(kind);
    }

    private void SelectType(AddKind kind)
    {
        _kind = kind;

        // Manual radio-group behavior: exactly one toggle stays checked.
        AppType.IsChecked = kind == AddKind.App;
        FileType.IsChecked = kind == AddKind.File;
        FolderType.IsChecked = kind == AddKind.Folder;
        LinkType.IsChecked = kind == AddKind.Link;
        ShortcutType.IsChecked = kind == AddKind.Shortcut;
        SeparatorType.IsChecked = kind == AddKind.Separator;
        GroupType.IsChecked = kind == AddKind.Group;
        DropType.IsChecked = kind == AddKind.DragDrop;

        // "Drag 'n drop" is the waiting state: an empty drop target in place of the fields — or,
        // once several items have been dropped together, the list of them. There is nothing to type
        // either way, and nothing to add until something has landed.
        bool dropMode = kind == AddKind.DragDrop;
        bool listing = dropMode && _dropped.Count > 0;
        DropZone.Visibility = dropMode && !listing ? Visibility.Visible : Visibility.Collapsed;
        DroppedPanel.Visibility = listing ? Visibility.Visible : Visibility.Collapsed;
        TargetPanel.Visibility = dropMode ? Visibility.Collapsed : Visibility.Visible;
        AddButton.IsEnabled = !dropMode || listing;
        AddButton.Content = listing
            ? Loc.Format("Add.SubmitMany", _dropped.Count)
            : Loc.Get("Add.Submit");
        if (dropMode)
        {
            HideError();
            NamePanel.Visibility = Visibility.Collapsed;
            ArgsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Neither a separator nor a group points at anything, so the target field goes away for
        // both. A group still has a name (it labels its fly-out); a separator doesn't, so its
        // form collapses to the explanatory hint alone.
        bool hasTarget = kind is not (AddKind.Separator or AddKind.Group);
        TargetLabel.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        TargetRow.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        NamePanel.Visibility = kind != AddKind.Separator ? Visibility.Visible : Visibility.Collapsed;

        bool canBrowse = kind is AddKind.App or AddKind.File or AddKind.Folder;
        BrowseButton.Visibility = canBrowse ? Visibility.Visible : Visibility.Collapsed;
        ArgsPanel.Visibility = kind == AddKind.App ? Visibility.Visible : Visibility.Collapsed;

        // Label / placeholder / hint all come from one "Add.<Kind>.*" family in the string table.
        // The kinds with no target contribute only a hint — there is no label or placeholder to
        // translate for a field that is never shown.
        string prefix = "Add." + kind switch
        {
            AddKind.App => "App",
            AddKind.File => "File",
            AddKind.Folder => "Folder",
            AddKind.Link => "Link",
            AddKind.Separator => "Separator",
            AddKind.Group => "Group",
            _ => "Shortcut",
        };
        if (hasTarget)
        {
            TargetLabel.Text = Loc.Get(prefix + ".Label");
            TargetBox.PlaceholderText = Loc.Get(prefix + ".Placeholder");
        }
        TargetHint.Text = Loc.Get(prefix + ".Hint");

        HideError();
    }

    // ---- Browsing ----------------------------------------------------------

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_kind == AddKind.Folder)
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                    SetTarget(folder.Path);
            }
            else
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                if (_kind == AddKind.App)
                {
                    picker.FileTypeFilter.Add(".exe");
                    picker.FileTypeFilter.Add(".lnk");
                    picker.FileTypeFilter.Add(".bat");
                    picker.FileTypeFilter.Add(".cmd");
                }
                else
                {
                    picker.FileTypeFilter.Add("*");
                }
                var file = await picker.PickSingleFileAsync();
                if (file is not null)
                    SetTarget(file.Path);
            }
        }
        catch (Exception ex)
        {
            ShowError(Loc.Format("Add.Error.Picker", ex.Message));
        }
    }

    private void SetTarget(string path)
    {
        TargetBox.Text = path;
        if (!_nameEdited)
            NameBox.Text = DockItemFactory.SuggestName(path);
        HideError();
    }

    private void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the suggested name in step with the typed target until the user edits the name.
        if (!_nameEdited && !string.IsNullOrWhiteSpace(TargetBox.Text))
            NameBox.Text = DockItemFactory.SuggestName(TargetBox.Text.Trim());
        HideError();
    }

    // ---- Drag & drop -------------------------------------------------------
    //
    // Anything the dock itself would take — apps (Start menu ones included), files, folders,
    // shortcuts, web links — is accepted anywhere in this window, whichever type is selected.
    // What happens next is what Browse does with a picked file, only more of it: the matching
    // type is selected and the target and display name are filled in, ready to check, edit and
    // add. It is deliberately not added on the spot — this is the Add dialog, and its Add button
    // is how anything gets onto the dock from here.

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!ShellDrop.CanAcceptAny(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
        {
            ui.Caption = Loc.Get("Dock.DropCaption");
            ui.IsCaptionVisible = true;
            ui.IsGlyphVisible = true;
        }
        SetDropHighlight(true);
        e.Handled = true;
    }

    /// <summary>
    /// A child's DragLeave bubbles up here every time the pointer crosses from one element to the
    /// next, so the cue only goes once the pointer is really outside the window's content.
    /// </summary>
    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(RootGrid);
        if (p.X <= 0 || p.Y <= 0 || p.X >= RootGrid.ActualWidth || p.Y >= RootGrid.ActualHeight)
            SetDropHighlight(false);
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;

        var deferral = e.GetDeferral();
        try
        {
            var dropped = await ShellDrop.ReadAnyAsync(e.DataView);
            if (_closed)
                return;

            if (dropped.Count == 0)
            {
                ShowError(Loc.Get("Add.Drop.Nothing"));
                return;
            }
            FillFrom(dropped);
        }
        catch (Exception ex)
        {
            Diag.Log("Drop onto the Add window failed: " + ex.Message);
            if (!_closed)
                ShowError(Loc.Get("Add.Drop.Nothing"));
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Lights the drop zone's outline in the accent color while a drop would be taken.</summary>
    private void SetDropHighlight(bool on) =>
        DropZoneHighlight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Puts what was dropped into the window: one item selects its type and fills in the target and
    /// name; several are listed, each with its own, for the Add button to add together.
    /// </summary>
    private void FillFrom(IReadOnlyList<ShellDrop.DroppedItem> dropped)
    {
        _dropped.Clear();
        if (dropped.Count == 1)
        {
            ShowDropped(dropped[0]);
        }
        else
        {
            _dropped.AddRange(dropped);
            SelectType(AddKind.DragDrop);
            ShowDroppedList();
        }

        // The drag came from another window, which may be the one in front. Bring this one up with
        // Add focused, so Enter finishes the job.
        Activate();
        AddButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Selects the type a dropped item is, and fills in its path and display name.</summary>
    private void ShowDropped(ShellDrop.DroppedItem item)
    {
        SelectType(KindFor(item.Kind));

        // Marked as edited first: the target's TextChanged would otherwise replace the shell's own
        // name for the item ("Google Chrome") with one guessed from its path ("chrome").
        _nameEdited = true;
        TargetBox.Text = item.Target;
        NameBox.Text = item.DisplayName;
        ArgsBox.Text = string.Empty;
    }

    /// <summary>The type tile that matches what a drop turned out to be.</summary>
    private static AddKind KindFor(DockItemKind kind) => kind switch
    {
        DockItemKind.Application => AddKind.App,
        DockItemKind.Folder => AddKind.Folder,
        DockItemKind.WebLink => AddKind.Link,
        _ => AddKind.File,
    };

    private void ShowDroppedList()
    {
        DroppedHeader.Text = Loc.Format("Add.Drop.Pending", _dropped.Count);
        DroppedList.Children.Clear();
        foreach (var item in _dropped)
            DroppedList.Children.Add(BuildDroppedRow(item));
    }

    /// <summary>One line of the list of several dropped items: its type, name and path.</summary>
    private Border BuildDroppedRow(ShellDrop.DroppedItem item)
    {
        var kind = KindFor(item.Kind);
        var symbols = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
        var secondary = (Style)RootGrid.Resources["DroppedSecondaryStyle"];

        var grid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The same glyphs as the type tiles above, so the row reads as "an App", "a Folder"…
        grid.Children.Add(new FontIcon
        {
            Glyph = kind switch
            {
                AddKind.App => "",
                AddKind.Folder => "",
                AddKind.Link => "",
                _ => "",
            },
            FontFamily = symbols,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var path = new TextBlock { Text = item.Target, Style = secondary };
        ToolTipService.SetToolTip(path, item.Target);
        text.Children.Add(path);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var category = new TextBlock
        {
            Text = Loc.Get(kind switch
            {
                AddKind.App => "Add.TypeApp",
                AddKind.Folder => "Add.TypeFolder",
                AddKind.Link => "Add.TypeLink",
                _ => "Add.TypeFile",
            }),
            Style = secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(category, 2);
        grid.Children.Add(category);

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontFamily = symbols, FontSize = 12 }, // Cancel
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(remove, Loc.Get("Menu.Remove"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, Loc.Get("Menu.Remove"));
        remove.Click += (_, _) => RemoveDropped(item);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        return new Border { Style = (Style)RootGrid.Resources["DroppedRowStyle"], Child = grid };
    }

    /// <summary>
    /// Takes one item off the list. Down to one, the list gives way to the fields (which is where a
    /// single item lives); down to none, back to the empty drop zone.
    /// </summary>
    private void RemoveDropped(ShellDrop.DroppedItem item)
    {
        _dropped.Remove(item);
        switch (_dropped.Count)
        {
            case 0:
                SelectType(AddKind.DragDrop);
                break;
            case 1:
                var last = _dropped[0];
                _dropped.Clear();
                ShowDropped(last);
                break;
            default:
                SelectType(AddKind.DragDrop); // refreshes the count on the Add button
                ShowDroppedList();
                break;
        }
    }

    // ---- Commit ------------------------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // Several items dropped together are each already complete — a type, a target and a name —
        // so they go straight on, in the order they were dropped.
        if (_dropped.Count > 0)
        {
            foreach (var dropped in _dropped)
                _dock.AddDockItem(dropped.ToDockItem());
            Close();
            return;
        }

        // A separator carries no target or name of its own, so it short-circuits the validation
        // and naming below entirely.
        if (_kind == AddKind.Separator)
        {
            _dock.AddSeparator();
            Close();
            return;
        }

        // A group is created empty and filled from the items already on the dock ("Move to
        // group ▸" on an item's right-click menu), so it only needs a name.
        if (_kind == AddKind.Group)
        {
            _dock.CreateGroup(NameBox.Text.Trim());
            Close();
            return;
        }

        var target = TargetBox.Text.Trim();
        if (target.Length == 0)
        {
            ShowError(Loc.Get("Add.Error.NoTarget"));
            return;
        }

        DockItemKind kind;
        switch (_kind)
        {
            case AddKind.App:
                kind = DockItemKind.Application;
                break;
            case AddKind.File:
                kind = DockItemKind.File;
                break;
            case AddKind.Folder:
                kind = DockItemKind.Folder;
                break;
            case AddKind.Link:
                // A "Web link" is specifically an http/https address (arbitrary URIs/commands go
                // through the "Shortcut" type instead). Add the scheme if omitted, then validate —
                // so a WebLink item never carries a non-web scheme that its globe icon would belie.
                if (!target.Contains("://"))
                    target = "https://" + target;
                if (!Uri.TryCreate(target, UriKind.Absolute, out var linkUri) ||
                    (linkUri.Scheme != Uri.UriSchemeHttp && linkUri.Scheme != Uri.UriSchemeHttps))
                {
                    ShowError(Loc.Get("Add.Error.BadUrl"));
                    return;
                }
                target = linkUri.ToString();
                kind = DockItemKind.WebLink;
                break;
            default: // Shortcut: detect from the raw target
                kind = DockItemFactory.Classify(target);
                break;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = DockItemFactory.SuggestName(target);

        var item = new DockItem
        {
            Kind = kind,
            DisplayName = name,
            Target = target,
        };
        if (kind == DockItemKind.Application && ArgsBox.Text.Trim() is { Length: > 0 } args)
            item.Arguments = args;

        _dock.AddDockItem(item);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Error surface -----------------------------------------------------

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void HideError() => ErrorBar.IsOpen = false;
}
