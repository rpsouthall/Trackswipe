using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT;

namespace Anchor.Services;

/// <summary>
/// Applies a Windows 11 taskbar-style acrylic ("glass") backdrop to a window.
///
/// Two things make this look like the real taskbar rather than a generic WinUI window:
///  1. The backdrop is forced <b>always active</b> (<c>IsInputActive = true</c>). A dock is
///     never the foreground window, and by default WinUI collapses acrylic to a flat
///     fallback color when its window is deactivated. Forcing active keeps the glass alive.
///  2. A hand-tuned tint/luminosity recipe per theme, layered on the system "Base" acrylic,
///     so it reads like the shell's own material and follows the Windows light/dark theme.
/// </summary>
public sealed class AcrylicBackdropManager : IDisposable
{
    private readonly Window _window;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _themeRoot;
    private bool _disposed;
    private UISettings? _uiSettings;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    // The last personalization asked for, remembered so re-syncing the recipes from the shell
    // colors can put it back.
    private bool _accentTint;

    public AcrylicBackdropManager(Window window) => _window = window;

    /// <summary>Recipe used in dark mode. Tunable to match the Win11 dark taskbar exactly.</summary>
    public AcrylicRecipe Dark { get; set; } = DefaultDarkRecipe();

    /// <summary>Recipe used in light mode.</summary>
    public AcrylicRecipe Light { get; set; } = DefaultLightRecipe();

    /// <summary>
    /// The recipe currently in force — whichever of <see cref="Dark"/> / <see cref="Light"/> the
    /// window's effective theme selects, personalization already folded in. Exposed so anything
    /// that has to paint the same glass <em>outside</em> this window can read the one recipe
    /// rather than keep a second copy of it: a group's fly-out bar opens in its own popup window,
    /// which a system backdrop cannot reach, so it mixes a XAML acrylic from these numbers
    /// instead (see <c>DockWindow.BarBackground</c>).
    /// </summary>
    public AcrylicRecipe Current =>
        (_themeRoot?.ActualTheme ?? ElementTheme.Dark) == ElementTheme.Light ? Light : Dark;

    /// <returns>true if acrylic was applied; false if the OS/GPU can't support it.</returns>
    public bool TryApply()
    {
        if (!DesktopAcrylicController.IsSupported())
            return false;

        _config = new SystemBackdropConfiguration
        {
            IsInputActive = true, // never fall back to solid when unfocused
        };

        _themeRoot = _window.Content as FrameworkElement;
        if (_themeRoot is not null)
            _themeRoot.ActualThemeChanged += OnThemeChanged;

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _uiSettings = new UISettings();
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
        SyncWithSystemColors();

        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
        };

        UpdateTheme(); // sets config.Theme + applies the matching recipe

        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
        return true;
    }

    // Raised by XAML. An exception that leaves a handler XAML called is not catchable by anyone: XAML
    // turns it into a stowed-exception fail-fast (0xc000027b), so nothing here may throw.
    private void OnThemeChanged(FrameworkElement sender, object args) => ApplySystemColors();

    // Raised by UISettings on a thread of its own, not the UI thread, while everything the update
    // touches (the root's ActualTheme, the composition controller) belongs to the UI thread — so it
    // is queued there instead of run here. A crash on a Windows.UI.Immersive notification thread
    // calling into XAML is what the 0xc000027b dumps from this app look like; whether this handler is
    // the one involved is not proven (see docs/performance), this is the correct shape for it either way.
    private void OnSystemColorsChanged(UISettings sender, object args)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _disposed)
            return;
        dispatcher.TryEnqueue(ApplySystemColors);
    }

    private void ApplySystemColors()
    {
        if (_disposed)
            return;
        try
        {
            SyncWithSystemColors();
            UpdateTheme();
        }
        catch (Exception ex)
        {
            Diag.Log("AcrylicBackdropManager: system color update failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Retunes the base recipes from the shell's current background color so the dock reads like
    /// the Windows 11 taskbar rather than a hand-picked grey.
    /// </summary>
    public void SyncWithSystemColors()
    {
        try
        {
            var settings = _uiSettings ?? new UISettings();
            var bg = settings.GetColorValue(UIColorType.Background);
            bool dark = (_themeRoot?.ActualTheme ?? ElementTheme.Dark) != ElementTheme.Light;

            // The shell color is only a usable base when Windows is in the same light/dark mode
            // as the window. When the two disagree — Anchor set to Light while Windows is still
            // Dark — the shell background is the opposite end of the scale, and tinting from it
            // stamps near-black glass into the *light* recipe, so the dock goes on looking dark
            // behind light-theme (dark) text. Fall back to the neutral base for the theme that is
            // actually in force whenever they disagree.
            bool systemDark = !IsLight(bg);

            if (dark)
            {
                var surface = Rgb(0x20, 0x20, 0x20);
                var tint = systemDark ? Darken(bg, 0.15) : DefaultDarkRecipe().Tint;
                Dark = Dark with { Tint = tint, Fallback = surface, LuminosityOpacity = 0.88 };
            }
            else
            {
                var surface = Rgb(0xF3, 0xF3, 0xF3);
                var tint = systemDark ? DefaultLightRecipe().Tint : Lighten(bg, 0.08);
                Light = Light with { Tint = tint, Fallback = surface, LuminosityOpacity = 0.88 };
            }

            // Re-syncing rebuilds the recipes from scratch, which would drop the accent tint the
            // user chose. Fold it back in — this runs on every theme change, including an OS
            // light/dark flip while Anchor follows the system, where nothing else would restore it.
            ApplyPersonalization();
        }
        catch
        {
            Dark = DefaultDarkRecipe();
            Light = DefaultLightRecipe();
            ApplyPersonalization();
        }
    }

    /// <summary>
    /// Re-applies the current recipes. <see cref="Dark"/> and <see cref="Light"/> are plain
    /// properties, so assigning one changes what the <em>next</em> theme update would use but
    /// leaves the live controller alone; the personalization setting (accent tint) needs it to
    /// take effect now.
    /// </summary>
    public void Refresh() => UpdateTheme();

    /// <summary>
    /// Re-tints both recipes for the given personalization setting, and applies it.
    /// </summary>
    /// <param name="accentTint">Tint with the Windows accent color rather than the neutral grey
    /// the taskbar uses. The accent is darkened for the dark recipe and lightened for the light
    /// one, because the raw accent at full strength overwhelms a 40px strip of icons.</param>
    public void Personalize(bool accentTint)
    {
        _accentTint = accentTint;
        ApplyPersonalization();
        Refresh();
    }

    /// <summary>Stamps the remembered personalization onto both recipes. No-op without an accent
    /// tint, so an unpersonalized window keeps its synced neutral tint.</summary>
    private void ApplyPersonalization()
    {
        // Without an accent tint, the neutral grey below is exactly what SyncWithSystemColors
        // already computed (including its light/dark-mismatch fallback), so leave Tint alone.
        if (!_accentTint)
            return;

        var darkTint = Blend(AccentColor(), Rgb(0x00, 0x00, 0x00), 0.55);
        var lightTint = Blend(AccentColor(), Rgb(0xFF, 0xFF, 0xFF), 0.60);
        Dark = Dark with { Tint = darkTint };
        Light = Light with { Tint = lightTint };
    }

    /// <summary>The Windows accent color, or Anchor's fallback blue if it can't be read.</summary>
    private static Color AccentColor()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Rgb(0x00, 0x78, 0xD4);
        }
    }

    /// <summary>Mixes <paramref name="color"/> toward <paramref name="toward"/> by
    /// <paramref name="amount"/> (0 = unchanged, 1 = fully the other color).</summary>
    private static Color Blend(Color color, Color toward, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(255, Mix(color.R, toward.R), Mix(color.G, toward.G), Mix(color.B, toward.B));
    }

    private void UpdateTheme()
    {
        if (_controller is null || _config is null)
            return;

        var theme = _themeRoot?.ActualTheme ?? ElementTheme.Dark;
        bool dark = theme != ElementTheme.Light;

        _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        var r = dark ? Dark : Light;
        _controller.TintColor = r.Tint;
        _controller.TintOpacity = (float)r.TintOpacity;
        _controller.LuminosityOpacity = (float)r.LuminosityOpacity;
        _controller.FallbackColor = r.Fallback;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_themeRoot is not null)
            _themeRoot.ActualThemeChanged -= OnThemeChanged;

        if (_uiSettings is not null)
            _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;

        _controller?.Dispose();
        _controller = null;
        _config = null;
    }

    /// <summary>Perceived-luminance test, used to tell which end of the light/dark scale a
    /// system color sits on.</summary>
    private static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) >= 128.0;

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static Color Darken(Color color, double amount)
    {
        byte Mix(byte v) => (byte)Math.Round(v * (1 - amount));
        return Color.FromArgb(255, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private static Color Lighten(Color color, double amount)
    {
        byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * amount);
        return Color.FromArgb(255, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private static AcrylicRecipe DefaultDarkRecipe() => new(
        Tint: Rgb(0x1C, 0x1C, 0x1C),
        TintOpacity: 0.55,
        LuminosityOpacity: 0.88,
        Fallback: Rgb(0x20, 0x20, 0x20));

    private static AcrylicRecipe DefaultLightRecipe() => new(
        Tint: Rgb(0xF2, 0xF2, 0xF2),
        TintOpacity: 0.55,
        LuminosityOpacity: 0.88,
        Fallback: Rgb(0xF3, 0xF3, 0xF3));
}

/// <summary>A tunable acrylic material recipe.</summary>
/// <param name="Tint">Base tint color.</param>
/// <param name="TintOpacity">How strongly the tint color is applied (0..1).</param>
/// <param name="LuminosityOpacity">Frostiness — the taskbar leans high here (0..1).</param>
/// <param name="Fallback">Solid color used when composition acrylic is unavailable.</param>
public readonly record struct AcrylicRecipe(
    Color Tint,
    double TintOpacity,
    double LuminosityOpacity,
    Color Fallback);
