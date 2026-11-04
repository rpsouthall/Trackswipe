using System.Text.Json.Serialization;

namespace Anchor.Models;

/// <summary>Which screen edge the dock is snapped to.</summary>
public enum DockEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// The app's colour theme. <see cref="System"/> follows the current Windows light/dark setting;
/// <see cref="Light"/> and <see cref="Dark"/> pin it regardless. A High Contrast accessibility
/// theme always overrides this so the shell's high-contrast colours come through.
/// </summary>
public enum DockTheme
{
    Light,
    Dark,
    System,
}

/// <summary>
/// Everything that persists between runs: the docks and the app-wide settings.
/// <para>
/// Per-dock state (items, position, edge, auto-hide) lives on each <see cref="DockProfile"/> in
/// <see cref="Docks"/>; everything here is app-wide and shared by every strip.
/// </para>
/// </summary>
public sealed class DockConfig
{
    /// <summary>
    /// The docks Anchor shows, in creation order. Always non-empty after
    /// <see cref="Migrate"/> — a config with no docks would leave the user with no UI and no way
    /// to get one back.
    /// </summary>
    public List<DockProfile> Docks { get; set; } = new();

    // ---- App-wide settings ------------------------------------------------

    /// <summary>Start Anchor automatically when the user signs in (per-user Run key).</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// The app's colour theme. Defaults to <see cref="DockTheme.Dark"/> so it reads like the
    /// Windows 11 dark taskbar (and so existing configs without this field keep that look).
    /// </summary>
    public DockTheme Theme { get; set; } = DockTheme.Dark;

    /// <summary>
    /// Show a dot under an app that is already running, and make clicking it focus that window
    /// instead of starting a second copy (Shift+click still starts a new instance). On by
    /// default. With <see cref="ShowRunningApps"/> also off, it stops the watch for those windows.
    /// </summary>
    public bool ShowRunningIndicators { get; set; } = true;

    /// <summary>
    /// The macOS Dock's open-apps section: an app that isn't pinned to any dock appears at the end
    /// of the first dock while it has a window open, and leaves when its last window closes. On by
    /// default.
    /// </summary>
    public bool ShowRunningApps { get; set; } = true;

    /// <summary>
    /// A divider between the pinned icons and the open apps. Off by default: as on a Mac with
    /// "recent apps" off, open apps simply follow the pinned ones, one row.
    /// </summary>
    public bool OpenAppsDivider { get; set; }

    /// <summary>
    /// The language Anchor's own UI uses, as a BCP-47 code from <c>Loc.Available</c> (e.g.
    /// <c>"de"</c>, <c>"zh-Hans"</c>). Empty — the default — follows the Windows display language.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The system-wide shortcut that brings the dock to the front, in the readable form
    /// <c>HotkeyGesture</c> parses (e.g. <c>"Ctrl+Alt+A"</c>). Empty means no shortcut. Kept as a
    /// string so a hand-edited config stays legible, and so an unparseable value degrades to
    /// "no shortcut" instead of failing to load the whole config.
    /// </summary>
    public string Hotkey { get; set; } = HotkeyGesture.Default.ToString();

    /// <summary>Whether <see cref="Hotkey"/> is registered with Windows. Lets the user switch the
    /// shortcut off without losing the combination they had chosen.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// True once the default items have been seeded (first run). Prevents re-seeding after the
    /// user has intentionally emptied the dock — an empty dock then persists and shows the
    /// "+ Add New" affordance instead of springing the defaults back.
    /// </summary>
    public bool Seeded { get; set; }

    /// <summary>
    /// How big the dock's icons and cells are. <see cref="DockDensity.Medium"/> matches the
    /// Windows 11 taskbar and is the default; the geometry every surface derives from it lives on
    /// <see cref="DockMetrics"/>.
    /// </summary>
    public DockDensity Density { get; set; } = DockDensity.Medium;

    /// <summary>Tint the glass with the Windows accent color instead of the neutral grey the
    /// taskbar uses.</summary>
    public bool AccentTint { get; set; }

    /// <summary>
    /// Swell an icon as the cursor passes over it, macOS-dock style. The swell happens
    /// <em>inside</em> the cell — the dock window never resizes — because the acrylic backdrop
    /// paints the whole window and cannot be masked to a taller, mostly-empty one.
    /// </summary>
    public bool Magnify { get; set; }

    /// <summary>
    /// Opens a group's fly-out as the cursor passes over its icon, and closes it again once the
    /// cursor leaves both the icon and the bar. On by default. Off makes a group strictly
    /// click-driven instead: one click opens its fly-out, a second click (or clicking elsewhere)
    /// closes it, and hovering does nothing either way — see <c>DockWindow.ToggleGroupFlyout</c>.
    /// </summary>
    public bool GroupOpenOnHover { get; set; } = true;

    /// <summary>
    /// Where the settings gear sits on every dock strip. Defaults to the trailing end, after
    /// user items and the divider.
    /// </summary>
    public SettingsPosition SettingsPosition { get; set; } = SettingsPosition.Trailing;

    /// <summary>
    /// Whether each dock shows its gear (and the divider before it). Off, Settings is still on the
    /// dock's right-click menu and the tray icon. On by default.
    /// </summary>
    public bool ShowSettingsButton { get; set; } = true;

    /// <summary>
    /// When true, each item's display name is always visible under its icon rather than only in
    /// a hover tooltip.
    /// </summary>
    public bool ShowItemLabels { get; set; }

    /// <summary>
    /// Per-item shortcuts (<c>DockItem.Hotkey</c>) are only registered with Windows while this is
    /// on. Off by default: a handful of extra system-wide combinations is a decision the user
    /// should make deliberately, not one that arrives with an update.
    /// </summary>
    public bool ItemHotkeysEnabled { get; set; }

    /// <summary>
    /// The shortcut that opens quick-launch search, in <c>HotkeyGesture</c>'s readable form.
    /// Empty means none — which is the default, for the same reason as
    /// <see cref="ItemHotkeysEnabled"/>.
    /// </summary>
    public string SearchHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Check GitHub for a newer release on startup. <b>Off by default and strictly opt-in</b> —
    /// it is the only thing besides a web link's favicon that would make Anchor talk to the
    /// network, and the privacy promise in the README depends on it staying that way.
    /// </summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>A release the user chose to skip, so the same prompt doesn't reappear every
    /// launch. Empty means nothing is skipped.</summary>
    public string SkippedUpdate { get; set; } = string.Empty;

    // ---- Legacy single-dock fields (read on load, never written again) -----
    //
    // Configs written before Anchor supported more than one dock carry these at the top level.
    // They are nullable so that, once Migrate has folded them into a DockProfile and nulled them
    // out, DockStore's WhenWritingNull policy drops them from the file entirely — the config
    // converts itself on first save rather than carrying both shapes around forever.

    [JsonPropertyName("Items")]
    public List<DockItem>? LegacyItems { get; set; }

    [JsonPropertyName("Snapped")]
    public bool? LegacySnapped { get; set; }

    [JsonPropertyName("Edge")]
    public DockEdge? LegacyEdge { get; set; }

    [JsonPropertyName("FreeX")]
    public int? LegacyFreeX { get; set; }

    [JsonPropertyName("FreeY")]
    public int? LegacyFreeY { get; set; }

    [JsonPropertyName("AutoHide")]
    public bool? LegacyAutoHide { get; set; }

    [JsonPropertyName("AlwaysOnTop")]
    public bool? LegacyAlwaysOnTop { get; set; }

    [JsonPropertyName("VerticalWhenSideSnapped")]
    public bool? LegacyVerticalWhenSideSnapped { get; set; }

    /// <summary>
    /// True when <see cref="Migrate"/> actually had to change something — i.e. the file on disk
    /// is still in the old shape. Startup uses this to rewrite it once, so the conversion isn't
    /// re-done on every launch until the user happens to change a setting.
    /// </summary>
    [JsonIgnore]
    public bool WasMigrated { get; private set; }

    /// <summary>
    /// Brings a freshly loaded config up to the current shape: folds a pre-multi-dock file's
    /// top-level dock into <see cref="Docks"/>, and guarantees at least one dock exists.
    /// Idempotent, and safe to call on a config that is already current.
    /// </summary>
    public DockConfig Migrate()
    {
        // A null entry is not a dock, so it goes before anything decides whether there is one:
        // SanitizeItems (below) drops them too, but guaranteeing a dock first and sanitizing after
        // left "Docks": [null] with none at all.
        Docks.RemoveAll(d => d is null);

        bool hasLegacyDock =
            LegacyItems is not null || LegacySnapped is not null || LegacyEdge is not null ||
            LegacyFreeX is not null || LegacyFreeY is not null || LegacyAutoHide is not null ||
            LegacyAlwaysOnTop is not null || LegacyVerticalWhenSideSnapped is not null;

        // Only adopt the legacy fields when there is nothing newer to conflict with. A file that
        // already has Docks is authoritative; anything left over at the top level is stale.
        WasMigrated |= hasLegacyDock;

        if (hasLegacyDock && Docks.Count == 0)
        {
            Docks.Add(new DockProfile
            {
                Items = LegacyItems ?? new List<DockItem>(),
                Snapped = LegacySnapped ?? false,
                Edge = LegacyEdge ?? DockEdge.Bottom,
                FreeX = LegacyFreeX,
                FreeY = LegacyFreeY,
                AutoHide = LegacyAutoHide ?? true,
                AlwaysOnTop = LegacyAlwaysOnTop ?? true,
                VerticalWhenSideSnapped = LegacyVerticalWhenSideSnapped ?? false,
            });
        }

        LegacyItems = null;
        LegacySnapped = null;
        LegacyEdge = null;
        LegacyFreeX = null;
        LegacyFreeY = null;
        LegacyAutoHide = null;
        LegacyAlwaysOnTop = null;
        LegacyVerticalWhenSideSnapped = null;

        if (Docks.Count == 0)
            Docks.Add(new DockProfile());

        SanitizeItems();

        return this;
    }

    /// <summary>
    /// Strips out the shapes a hand-edited (or partially overwritten) <c>dock.json</c> can produce
    /// that no exception ever flags: a literal JSON <c>null</c> inside an items array deserializes
    /// as a null list entry rather than failing the file, since <see cref="DockItem"/> is a
    /// reference type — and an explicit <c>"Children": null</c> on a group overwrites its default
    /// empty list the same way. Every consumer downstream (starting with <c>DockWindow</c>'s own
    /// constructor, which walks every item as it lays out) assumes every entry is real and would
    /// <see cref="NullReferenceException"/> on the first one that isn't — before any window exists,
    /// which is a crash on every subsequent launch, not just this one. Run once here so nothing
    /// later has to guard against it.
    /// </summary>
    private void SanitizeItems()
    {
        Docks.RemoveAll(d => d is null);

        foreach (var profile in Docks)
        {
            profile.Items.RemoveAll(item => item is null);
            foreach (var item in profile.Items)
            {
                item.Children ??= new List<DockItem>();
                item.Children.RemoveAll(child => child is null);
            }
        }
    }

    /// <summary>
    /// Copies every app-wide setting off <paramref name="other"/>, leaving <see cref="Docks"/>
    /// alone. Used by import, which replaces the docks separately: the running
    /// <see cref="DockConfig"/> instance is shared by every window and the manager, so an import
    /// has to fill the existing object in rather than swap in a new one.
    /// <para>
    /// <see cref="Seeded"/> is deliberately not copied — it says whether <em>this installation</em>
    /// has been past its first run, which is a fact about this machine and not about the file.
    /// </para>
    /// </summary>
    public void CopyAppSettingsFrom(DockConfig other)
    {
        LaunchAtStartup = other.LaunchAtStartup;
        Theme = other.Theme;
        ShowRunningIndicators = other.ShowRunningIndicators;
        ShowRunningApps = other.ShowRunningApps;
        OpenAppsDivider = other.OpenAppsDivider;
        Language = other.Language;
        Hotkey = other.Hotkey;
        HotkeyEnabled = other.HotkeyEnabled;
        Density = other.Density;
        AccentTint = other.AccentTint;
        Magnify = other.Magnify;
        GroupOpenOnHover = other.GroupOpenOnHover;
        SettingsPosition = other.SettingsPosition;
        ShowSettingsButton = other.ShowSettingsButton;
        ShowItemLabels = other.ShowItemLabels;
        ItemHotkeysEnabled = other.ItemHotkeysEnabled;
        SearchHotkey = other.SearchHotkey;
        CheckForUpdates = other.CheckForUpdates;
        SkippedUpdate = other.SkippedUpdate;
    }

    /// <summary>The dock that app-wide actions fall back to (the tray's "Add new…", for one).</summary>
    [JsonIgnore]
    public DockProfile Primary => Docks.Count > 0 ? Docks[0] : throw new InvalidOperationException(
        "DockConfig has no docks; Migrate() must run before the config is used.");
}
