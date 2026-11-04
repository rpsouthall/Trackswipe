using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Anchor.Models;

public enum DockItemKind
{
    Application, // .exe (or any launchable app)
    File,
    Folder,
    WebLink,
    Separator,
    Group, // holds Children instead of a target; opens a fly-out rather than launching
}

/// <summary>
/// One entry in the dock. Serializable data lives on the public settable properties;
/// the resolved <see cref="IconImage"/> is a runtime-only visual and is not persisted.
/// </summary>
public sealed class DockItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DockItemKind Kind { get; set; } = DockItemKind.Application;

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    /// <summary>File-system path for apps/files/folders, or a URL for web links.</summary>
    public string Target { get; set; } = "";

    /// <summary>Optional command-line arguments (apps only).</summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// Optional path to a user-supplied icon overriding the shell/favicon icon. Set from the
    /// item's right-click menu ("Change icon…"); cleared by "Use the default icon". Mutually
    /// exclusive with <see cref="CustomGlyph"/> — the icon picker sets one and clears the other,
    /// since only one can be shown at a time.
    /// </summary>
    public string? CustomIconPath { get; set; }

    private string? _customGlyph;

    /// <summary>
    /// Optional built-in glyph (a Segoe Fluent Icons character, chosen from the icon picker's
    /// swatch grid) overriding the kind's default <see cref="Glyph"/>. Unlike
    /// <see cref="CustomIconPath"/> this needs no network/shell resolution, so it renders
    /// immediately and is never touched by <c>IconService</c>.
    /// </summary>
    public string? CustomGlyph
    {
        get => _customGlyph;
        set
        {
            if (_customGlyph == value)
                return;
            _customGlyph = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(HasCustomIcon));
        }
    }

    private string? _iconColor;
    private Windows.UI.Color? _iconTint;
    private Brush? _glyphTint;

    /// <summary>
    /// Optional colour for the item's glyph, as <c>#RRGGBB</c> — picked from a group's editor.
    /// Null (or anything that doesn't parse) means the theme's text colour, which is what every
    /// glyph used before this existed. Only a glyph is tinted: an image icon draws its own colours.
    /// Kept as a string for the same reason <see cref="Hotkey"/> is — a hand-edited config stays
    /// legible, and a bad value degrades to "no colour" rather than failing the load.
    /// </summary>
    public string? IconColor
    {
        get => _iconColor;
        set
        {
            _iconTint = TryParseColor(value, out var c) ? c : null;
            _iconColor = _iconTint is { } t ? FormatColor(t) : null;
            _glyphTint = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlyphTint));
            OnPropertyChanged(nameof(GlyphVisibility));
            OnPropertyChanged(nameof(TintedGlyphVisibility));
        }
    }

    /// <summary>True when the glyph is drawn in <see cref="IconColor"/> rather than the theme's
    /// text colour. Never under High Contrast, where Windows supplies every colour.</summary>
    [JsonIgnore]
    public bool HasGlyphTint => _iconTint is not null && !DockItemAnimations.HighContrast;

    /// <summary>
    /// The brush for a tinted glyph, or null when <see cref="HasGlyphTint"/> is false. Built on
    /// first read rather than in the setter: the setter runs while the config is deserialized,
    /// which need not be on the UI thread, and a XAML brush can only be made on it.
    /// </summary>
    [JsonIgnore]
    public Brush? GlyphTint =>
        HasGlyphTint ? _glyphTint ??= new SolidColorBrush(_iconTint!.Value) : null;

    /// <summary>Parses <c>#RRGGBB</c> (the leading <c>#</c> optional) into an opaque colour.</summary>
    public static bool TryParseColor(string? text, out Windows.UI.Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 }
            || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>The <c>#RRGGBB</c> form <see cref="IconColor"/> stores (alpha dropped: always opaque).</summary>
    public static string FormatColor(Windows.UI.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// When true the item stays in the config (and in the Settings ▸ Apps list) but is not
    /// rendered on the dock. Toggled from the Settings window's per-app show/hide switch.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// The entries inside a <see cref="DockItemKind.Group"/>, in the order its fly-out shows
    /// them. Empty for every other kind — a group holds children <em>instead of</em> a target.
    /// Groups don't nest: a child is always a leaf.
    /// </summary>
    public List<DockItem> Children { get; set; } = new();

    /// <summary>
    /// An optional per-item system-wide shortcut, in the readable form <c>HotkeyGesture</c>
    /// parses (e.g. <c>"Ctrl+Alt+1"</c>). Empty means none. Kept as a string for the same reason
    /// <see cref="DockConfig.Hotkey"/> is: a hand-edited config stays legible, and a combination
    /// that no longer parses degrades to "no shortcut" rather than failing the whole load.
    /// </summary>
    public string? Hotkey { get; set; }

    /// <summary>
    /// For a <see cref="DockItemKind.Folder"/>: open a fly-out listing the folder's contents
    /// instead of handing the folder to Explorer. Off by default, so a folder keeps behaving the
    /// way it always has until the user asks for the stack.
    /// </summary>
    public bool FolderFlyout { get; set; }

    /// <summary>
    /// For a <see cref="DockItemKind.Group"/>: hides <see cref="Children"/> from the Settings
    /// ▸ Apps &amp; links list. Purely a display preference for that list — the dock's own
    /// fly-out always shows every child regardless.
    /// </summary>
    public bool IsCollapsed { get; set; }

    // ---- Runtime-only visual state (never serialized) ----------------------

    private ImageSource? _iconImage;

    [JsonIgnore]
    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            _iconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageVisibility));
            OnPropertyChanged(nameof(GlyphVisibility));
            OnPropertyChanged(nameof(TintedGlyphVisibility));
        }
    }

    // ---- Running-app state (runtime-only, refreshed on a poll) -------------

    private bool _isRunning;
    private string _runningStatusText = "";

    /// <summary>True while an instance of this item's app has an open window.</summary>
    [JsonIgnore]
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Updates the running state. The localized status text is passed in rather than looked up
    /// here so the model stays free of the string table — the dock owns that.
    /// </summary>
    public void SetRunning(bool running, string statusText)
    {
        if (_isRunning == running && _runningStatusText == statusText)
            return;
        _isRunning = running;
        _runningStatusText = statusText;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(RunningIndicatorVisibility));
        OnPropertyChanged(nameof(RunningStatus));
    }

    /// <summary>The dot under the icon: only for a running, launchable item.</summary>
    [JsonIgnore]
    public Visibility RunningIndicatorVisibility =>
        _isRunning && !IsSeparator && !IsGroup ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Announced by Narrator alongside the item's name (AutomationProperties.ItemStatus), so the
    /// dot conveys the same thing to assistive technology as it does visually.
    /// </summary>
    [JsonIgnore]
    public string RunningStatus => _isRunning ? _runningStatusText : "";

    /// <summary>
    /// The glyph shown when no bitmap icon is available: <see cref="CustomGlyph"/> if the user
    /// picked one from the icon picker, otherwise a fallback that depends on <see cref="Kind"/>.
    /// </summary>
    [JsonIgnore]
    public string Glyph => !string.IsNullOrEmpty(CustomGlyph) ? CustomGlyph : Kind switch
    {
        DockItemKind.WebLink => "\uE774",   // Globe
        DockItemKind.Folder => "\uE8B7",    // Folder
        DockItemKind.Group => "\uE838",     // FolderOpen \u2014 a group "opens" to reveal its contents
        DockItemKind.Separator => "",
        _ => "\uE7C3",                       // Page (generic file/app fallback)
    };

    [JsonIgnore]
    public Visibility ImageVisibility => _iconImage is null ? Visibility.Collapsed : Visibility.Visible;

    // Two glyphs rather than one with a swappable brush: the untinted one's foreground is a
    // ThemeResource, which only XAML can keep following the theme, and overwriting it with a
    // colour would lose that for good once the colour was cleared again.

    /// <summary>The glyph in the theme's text colour: no bitmap icon and no <see cref="IconColor"/>.</summary>
    [JsonIgnore]
    public Visibility GlyphVisibility =>
        _iconImage is null && !HasGlyphTint ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The glyph drawn in <see cref="GlyphTint"/>: no bitmap icon, and a colour picked.</summary>
    [JsonIgnore]
    public Visibility TintedGlyphVisibility =>
        _iconImage is null && HasGlyphTint ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public bool IsSeparator => Kind == DockItemKind.Separator;

    /// <summary>True for a fly-out group, which holds <see cref="Children"/> instead of a target.</summary>
    [JsonIgnore]
    public bool IsGroup => Kind == DockItemKind.Group;

    /// <summary>
    /// True for the transient empty cell the strip opens up while something is being dragged in
    /// from outside — the "shell" standing where the drop will land, so the items part around it
    /// exactly as they part around an icon being reordered. Never persisted and never in a
    /// profile's item list: the dock inserts one into its <em>visible</em> collection for the life
    /// of the drag and takes it out again on the drop (see <c>DockWindow.DropTargets</c>).
    /// </summary>
    [JsonIgnore]
    public bool IsPlaceholder { get; private init; }

    /// <summary>
    /// Creates the drop shell. A <see cref="DockItemKind.File"/> because that is the kind that
    /// gets a full cell's slot (a separator's is narrow) <em>and</em> that the per-icon drop
    /// handlers pass straight through — an empty cell has nothing to open a dropped file with.
    /// </summary>
    public static DockItem CreatePlaceholder() =>
        new() { Kind = DockItemKind.File, IsPlaceholder = true };

    /// <summary>
    /// True for the dock's open-apps section, as on a Mac: an app that isn't pinned, shown only
    /// while it has a window open (and the divider in front of them). Never persisted and never in
    /// a profile's item list — the dock appends these to the tail of its <em>visible</em>
    /// collection and keeps them there (see <c>DockWindow.RunningApps</c>).
    /// </summary>
    [JsonIgnore]
    public bool IsTransient { get; private init; }

    /// <summary>The key of the open app a transient item stands for (see <c>RunningApp.Key</c>).</summary>
    [JsonIgnore]
    public string? RunningAppKey { get; private init; }

    /// <summary>Creates an open app's icon for the dock's open-apps section.</summary>
    public static DockItem CreateOpenApp(string name, string target, string key) => new()
    {
        Kind = DockItemKind.Application,
        DisplayName = name,
        Target = target,
        IsTransient = true,
        RunningAppKey = key,
    };

    /// <summary>Creates the divider between the pinned icons and the open-apps section.</summary>
    public static DockItem CreateOpenAppsDivider() =>
        new() { Kind = DockItemKind.Separator, IsTransient = true };

    /// <summary>True when the user has pinned an icon of their own onto this item — either a
    /// custom image file or a built-in glyph chosen from the icon picker.</summary>
    [JsonIgnore]
    public bool HasCustomIcon =>
        !string.IsNullOrWhiteSpace(CustomIconPath) || !string.IsNullOrEmpty(CustomGlyph);

    /// <summary>
    /// How much room this item takes along the strip's flow, in DIPs. A separator is a thin
    /// divider rather than a launchable cell, so it gets a much narrower slot than the
    /// taskbar-sized icons around it. Used both to size the dock window (see
    /// <c>DockWindow.UpdateSizeAndPosition</c>) and to map a drag position onto a slot while
    /// reordering, so the two can never disagree about where a cell starts.
    /// </summary>
    [JsonIgnore]
    public double CellExtent => IsSeparator ? DockMetrics.SeparatorExtent : DockMetrics.Cell;

    /// <summary>The rounded corner on this cell's hover/press chrome.</summary>
    [JsonIgnore]
    public CornerRadius CellCorner => new(DockMetrics.CellCorner);

    // ---- Hover (runtime-only) ----------------------------------------------

    private bool _hovered;
    private double _hoverOpacityDisplay;
    private double _hoverOpacityTarget;

    /// <summary>
    /// Marks this cell as the one the cursor is in, which is what draws its highlight. Set from
    /// the strip's own pointer tracking (see <c>DockWindow.TrackStripPointer</c>) rather than from
    /// the cell's <c>Button</c>, whose <c>PointerOver</c> state does not arrive on a repeater-
    /// realized cell — which is why dock items had no hover cue at all.
    /// </summary>
    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered)
            return;
        _hovered = hovered;
        _hoverOpacityTarget = hovered && !IsSeparator && !IsPlaceholder ? 1 : 0;
        if (DockItemAnimations.ReducedMotion)
        {
            _hoverOpacityDisplay = _hoverOpacityTarget;
            OnPropertyChanged(nameof(HoverOpacity));
        }
    }

    /// <summary>
    /// The highlight's opacity, eased toward the hover target by <see cref="AnimateVisuals"/>.
    /// </summary>
    [JsonIgnore]
    public double HoverOpacity => _hoverOpacityDisplay;

    // ---- Dragging (runtime-only) --------------------------------------------

    private bool _dragging;

    /// <summary>
    /// Marks this item as the one currently picked up for a reorder. Its own cell in the strip
    /// dims to a placeholder while the dock draws a floating ghost that tracks the pointer
    /// instead — without this, the cell just reflowed silently as the list reordered underneath
    /// it, with no visual tying the motion to the cursor.
    /// </summary>
    public void SetDragging(bool dragging)
    {
        if (_dragging == dragging)
            return;
        _dragging = dragging;
        OnPropertyChanged(nameof(CellOpacity));
    }

    /// <summary>Full opacity at rest; dimmed to a placeholder while this item is being dragged.</summary>
    [JsonIgnore]
    public double CellOpacity => _dragging ? 0.35 : 1;

    // ---- Drop shell (runtime-only) -----------------------------------------

    private bool _placeholderMuted;

    /// <summary>
    /// Hides the shell's own fill while leaving its slot open. Used while the drag is over an icon
    /// that will take the drop itself (a file onto an app, or anything into a group), where the
    /// swelled icon is the cue and a second one would contradict it. The slot stays open all the
    /// same, because closing it would shift every cell past it by a full cell width and hand the
    /// drag to whichever icon slid under the cursor — which re-opens the slot, and so on.
    /// </summary>
    public void SetPlaceholderMuted(bool muted)
    {
        if (_placeholderMuted == muted)
            return;
        _placeholderMuted = muted;
        OnPropertyChanged(nameof(PlaceholderVisibility));
    }

    /// <summary>The empty slot itself, drawn only for a drop shell and only while it is the cue.</summary>
    [JsonIgnore]
    public Visibility PlaceholderVisibility =>
        IsPlaceholder && !_placeholderMuted ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Advances hover/magnify easing. Returns true if any displayed value changed.</summary>
    internal bool AnimateVisuals(double hoverStep, double magnifyStep)
    {
        bool changed = false;
        if (Math.Abs(_hoverOpacityDisplay - _hoverOpacityTarget) > 0.001)
        {
            _hoverOpacityDisplay = Lerp(_hoverOpacityDisplay, _hoverOpacityTarget, hoverStep);
            changed = true;
            OnPropertyChanged(nameof(HoverOpacity));
        }

        if (Math.Abs(_magnifyDisplay - _magnifyTarget) > 0.001)
        {
            _magnifyDisplay = Lerp(_magnifyDisplay, _magnifyTarget, magnifyStep);
            changed = true;
            OnPropertyChanged(nameof(RenderIconSize));
            OnPropertyChanged(nameof(RenderGlyphSize));
        }

        return changed;
    }

    internal void SnapVisuals()
    {
        _hoverOpacityDisplay = _hoverOpacityTarget;
        _magnifyDisplay = _magnifyTarget;
        OnPropertyChanged(nameof(HoverOpacity));
        OnPropertyChanged(nameof(RenderIconSize));
        OnPropertyChanged(nameof(RenderGlyphSize));
    }

    private static double Lerp(double from, double to, double step) =>
        from + (to - from) * Math.Clamp(step, 0, 1);

    // ---- Magnification (runtime-only) --------------------------------------

    private double _magnifyTarget = 1;
    private double _magnifyDisplay = 1;

    /// <summary>
    /// Sets the magnification target for this cell. The displayed size eases toward it via
    /// <see cref="AnimateVisuals"/> unless reduced motion is on.
    /// </summary>
    public void SetMagnification(double scale)
    {
        if (Math.Abs(_magnifyTarget - scale) < 0.001)
            return;
        _magnifyTarget = scale;
        if (DockItemAnimations.ReducedMotion)
        {
            _magnifyDisplay = scale;
            OnPropertyChanged(nameof(RenderIconSize));
            OnPropertyChanged(nameof(RenderGlyphSize));
        }
    }

    /// <summary>
    /// The icon's drawn size: the density's icon size, swelled by any magnification, but never
    /// past the cell it lives in (a cell is fixed, so an unbounded swell would just clip).
    /// </summary>
    [JsonIgnore]
    public double RenderIconSize => Math.Min(DockMetrics.Icon * _magnifyDisplay, DockMetrics.Cell - 2);

    /// <summary>The fallback glyph's size, magnified on the same curve as <see cref="RenderIconSize"/>.</summary>
    [JsonIgnore]
    public double RenderGlyphSize => Math.Min(DockMetrics.Glyph * _magnifyDisplay, (DockMetrics.Cell - 2) * 0.72);

    /// <summary>Whether a persistent label is drawn under the icon (app-wide setting).</summary>
    [JsonIgnore]
    public Visibility LabelVisibility =>
        DockItemAnimations.ShowLabels && !IsSeparator ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The running-app indicator's length, which tracks the density.</summary>
    [JsonIgnore]
    public double IndicatorLength => DockMetrics.IndicatorLength;

    /// <summary>
    /// Re-reads every geometry-derived property after the app-wide density changed. The dock
    /// calls this on each of its items rather than every item subscribing to
    /// <see cref="DockMetrics.Changed"/> itself — items outlive no window, but a static event
    /// they never unsubscribe from would keep every removed item alive forever.
    /// </summary>
    public void RefreshMetrics()
    {
        OnPropertyChanged(nameof(CellWidth));
        OnPropertyChanged(nameof(CellHeight));
        OnPropertyChanged(nameof(CellCorner));
        OnPropertyChanged(nameof(RenderIconSize));
        OnPropertyChanged(nameof(RenderGlyphSize));
        OnPropertyChanged(nameof(LabelVisibility));
        OnPropertyChanged(nameof(IndicatorLength));
        OnPropertyChanged(nameof(SeparatorLineWidth));
        OnPropertyChanged(nameof(SeparatorLineHeight));
    }

    /// <summary>The launch button is shown for everything except a separator and a drop shell,
    /// neither of which has anything to launch.</summary>
    [JsonIgnore]
    public Visibility ButtonVisibility =>
        IsSeparator || IsPlaceholder ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The thin divider line is shown only for a separator.</summary>
    [JsonIgnore]
    public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;

    // Whether the strip currently flows top-to-bottom rather than left-to-right. Orientation is
    // a dock-wide property, but the cell sizes below are per-item template bindings, so the dock
    // pushes it down onto every item (see DockWindow.ApplyOrientation) rather than the template
    // reaching back up for it.
    private bool _flowVertical;

    /// <summary>Re-orients this item's cell. No-op when the orientation is unchanged.</summary>
    public void SetFlowVertical(bool vertical)
    {
        if (_flowVertical == vertical)
            return;
        _flowVertical = vertical;
        OnPropertyChanged(nameof(CellWidth));
        OnPropertyChanged(nameof(CellHeight));
        OnPropertyChanged(nameof(SeparatorLineWidth));
        OnPropertyChanged(nameof(SeparatorLineHeight));
    }

    /// <summary>Cell width: the narrow side only when a separator sits in a horizontal strip.</summary>
    [JsonIgnore]
    public double CellWidth =>
        IsSeparator && !_flowVertical ? DockMetrics.SeparatorExtent : DockMetrics.Cell;

    /// <summary>Cell height: the narrow side only when a separator sits in a vertical strip.</summary>
    [JsonIgnore]
    public double CellHeight =>
        IsSeparator && _flowVertical ? DockMetrics.SeparatorExtent : DockMetrics.Cell;

    /// <summary>A separator's hairline lies across the flow, so its sides swap with orientation.</summary>
    [JsonIgnore]
    public double SeparatorLineWidth => _flowVertical ? DockMetrics.DividerLength : 1;

    [JsonIgnore]
    public double SeparatorLineHeight => _flowVertical ? 1 : DockMetrics.DividerLength;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
