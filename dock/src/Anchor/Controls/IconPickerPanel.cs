using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anchor.Controls;

/// <summary>
/// The "choose an icon" panel: every built-in glyph as one unscrolled swatch grid, plus the two
/// routes to an image of the user's own.
/// <para>
/// Built as a plain panel rather than a control or a flyout of its own, because the two places it
/// appears host it differently — the item editor puts it in a flyout beside its icon field, and
/// the dock's new-group modal opens it as a panel over the strip — and the only thing they need to
/// agree on is what is in it.
/// </para>
/// </summary>
internal static class IconPickerPanel
{
    private const int Columns = 6;
    private const double SwatchSize = 36;

    /// <summary>
    /// Builds the panel.
    /// </summary>
    /// <param name="ownerHwnd">Window the OS file dialogs are parented to; without it they open
    /// behind the app.</param>
    /// <param name="swatchStyle">Chrome for the glyph buttons. The dock passes its own glass
    /// button style so the picker matches the strip it opens over; a normal window passes null and
    /// gets the standard button.</param>
    /// <param name="onSelected">Called with the chosen glyph or file path. Never both.</param>
    /// <param name="beforeBrowse">Run just before an OS file dialog opens — it takes focus, which
    /// light-dismisses whatever popup this panel is sitting in.</param>
    public static StackPanel Build(
        nint ownerHwnd,
        Style? swatchStyle,
        Action<IconSelection> onSelected,
        Action? beforeBrowse = null)
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(4), MaxWidth = 320 };

        var header = new TextBlock { Text = Loc.Get("IconPicker.Title") };
        if (Application.Current.Resources.TryGetValue("BodyStrongTextBlockStyle", out var h) &&
            h is Style headerStyle)
            header.Style = headerStyle;
        else
            header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        panel.Children.Add(header);

        // Every glyph at once, in no scroller. There are few enough of them (six rows of six) that
        // the whole set fits a flyout without one, and a scrolling panel of icons hides half the
        // choice behind a gesture — the point of a swatch grid is that you can see all of it.
        panel.Children.Add(BuildGrid(swatchStyle, onSelected));

        var browseImage = BrowseButton(Loc.Get("IconPicker.Browse"));
        browseImage.Click += async (_, _) =>
        {
            beforeBrowse?.Invoke();
            if (await PickImageAsync(ownerHwnd) is { } path)
                onSelected(new IconSelection(null, path));
        };
        panel.Children.Add(browseImage);

        var browseApp = BrowseButton(Loc.Get("IconPicker.BrowseApp"));
        browseApp.Click += async (_, _) =>
        {
            beforeBrowse?.Invoke();
            if (await PickExecutableAsync(ownerHwnd) is { } path)
                onSelected(new IconSelection(null, path));
        };
        panel.Children.Add(browseApp);

        return panel;
    }

    private static Grid BuildGrid(Style? swatchStyle, Action<IconSelection> onSelected)
    {
        var choices = IconChoices.All;
        int columns = Math.Min(Columns, choices.Count);
        int rows = (choices.Count + columns - 1) / columns;

        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var swatch = new Button
            {
                Width = SwatchSize,
                Height = SwatchSize,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = choice.Glyph,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 16,
                },
            };
            if (swatchStyle is not null)
                swatch.Style = swatchStyle;
            ToolTipService.SetToolTip(swatch, choice.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, choice.Name);
            var captured = choice;
            swatch.Click += (_, _) => onSelected(new IconSelection(captured.Glyph, null));
            Grid.SetColumn(swatch, i % columns);
            Grid.SetRow(swatch, i / columns);
            grid.Children.Add(swatch);
        }
        return grid;
    }

    private static Button BrowseButton(string text) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Opens the OS file picker for an icon image, returning the chosen path or null if
    /// cancelled or the picker itself failed.</summary>
    public static async Task<string?> PickImageAsync(nint ownerHwnd)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            // Formats the XAML imaging stack decodes. Deliberately no .exe/.dll here: pulling an
            // icon out of a binary means choosing an index too, which is the picker below.
            foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diag.Log($"Change icon failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the OS file picker for a binary to lift an icon out of.</summary>
    public static async Task<string?> PickExecutableAsync(nint ownerHwnd)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            foreach (var ext in new[] { ".exe", ".dll", ".ico" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diag.Log($"Pick executable icon failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
