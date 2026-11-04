using System.Text.Json;
using System.Text.Json.Serialization;
using Anchor.Models;
using Xunit;

namespace Anchor.Tests.Models;

public class DockConfigTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Defaults_match_the_first_run_experience()
    {
        var cfg = new DockConfig().Migrate();

        // One dock, empty and floating.
        var dock = Assert.Single(cfg.Docks);
        Assert.Empty(dock.Items);
        Assert.False(dock.Snapped);
        Assert.Equal(DockEdge.Bottom, dock.Edge);
        Assert.Null(dock.FreeX);
        Assert.Null(dock.FreeY);
        Assert.True(dock.AutoHide);
        Assert.True(dock.DodgeWindows);
        Assert.True(dock.AlwaysOnTop);
        Assert.False(dock.VerticalWhenSideSnapped);

        // App-wide settings.
        Assert.False(cfg.LaunchAtStartup);
        Assert.Equal(DockTheme.Dark, cfg.Theme);
        Assert.True(cfg.ShowRunningIndicators);
        Assert.True(cfg.ShowRunningApps);
        Assert.False(cfg.Seeded);
        Assert.Equal(string.Empty, cfg.Language); // follow the Windows display language
        Assert.True(cfg.HotkeyEnabled);
        Assert.Equal("Ctrl+Alt+A", cfg.Hotkey);
    }

    [Fact]
    public void Migrate_always_leaves_at_least_one_dock()
    {
        // A hand-edited (or truncated) file with an empty dock list would otherwise start Anchor
        // with no window and no obvious way to get one.
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Docks\": [] }", Options)!.Migrate();

        Assert.Single(cfg.Docks);
        Assert.Same(cfg.Docks[0], cfg.Primary);
    }

    [Fact]
    public void Migrate_drops_null_entries_from_a_hand_edited_items_array()
    {
        // dock.json is documented as hand-editable, and a literal JSON `null` inside an items
        // array deserializes as a null list entry rather than failing the whole file (DockItem is
        // a reference type). DockWindow's own constructor walks every item as it lays out and
        // would NullReferenceException on the first one that isn't real — before any window
        // exists, which is a crash on every subsequent launch. Migrate has to make that shape
        // impossible to see downstream.
        const string json = """
        {
          "Docks": [
            {
              "Items": [
                null,
                { "Kind": "Application", "DisplayName": "Notepad", "Target": "notepad.exe" },
                {
                  "Kind": "Group",
                  "DisplayName": "Tools",
                  "Children": [ null, { "DisplayName": "Calc" } ]
                }
              ]
            }
          ]
        }
        """;

        var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options)!.Migrate();

        var dock = Assert.Single(cfg.Docks);
        Assert.Equal(2, dock.Items.Count);
        Assert.Equal("Notepad", dock.Items[0].DisplayName);

        var group = dock.Items[1];
        Assert.Equal(DockItemKind.Group, group.Kind);
        Assert.Equal("Calc", Assert.Single(group.Children).DisplayName);
    }

    [Fact]
    public void Migrate_repairs_a_group_whose_children_list_was_explicitly_null()
    {
        // "Children": null overwrites the property's own empty-list default the same way a
        // missing field wouldn't — anything that later does `group.Children.Add(...)` or iterates
        // it (the fly-out, the strip, Settings ▸ Apps & links) would NullReferenceException.
        const string json = """
        { "Docks": [ { "Items": [ { "Kind": "Group", "DisplayName": "Empty", "Children": null } ] } ] }
        """;

        var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options)!.Migrate();

        var group = Assert.Single(Assert.Single(cfg.Docks).Items);
        Assert.NotNull(group.Children);
        Assert.Empty(group.Children);
    }

    [Fact]
    public void Migrate_drops_a_null_dock_alongside_a_real_one()
    {
        // The same hand-editing hazard one level up: "Docks": [null, {...}].
        const string json = """{ "Docks": [ null, { "Name": "Real" } ] }""";

        var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options)!.Migrate();

        Assert.Equal("Real", Assert.Single(cfg.Docks).Name);
    }

    [Fact]
    public void Migrate_still_leaves_a_dock_when_the_only_entry_was_null()
    {
        // "Docks": [null] is the same hazard with nothing real beside it. Guaranteeing a dock before
        // the null is removed left none at all: the app started with a tray icon and no window, and
        // DockConfig.Primary would throw for anything that asked for it.
        const string json = """{ "Docks": [ null ] }""";

        var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options)!.Migrate();

        Assert.NotNull(Assert.Single(cfg.Docks));
        Assert.NotNull(cfg.Primary);
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        var cfg = new DockConfig().Migrate();
        var dock = cfg.Primary;

        cfg.Migrate();
        cfg.Migrate();

        Assert.Single(cfg.Docks);
        Assert.Same(dock, cfg.Primary); // not replaced by a fresh one on each pass
    }

    [Fact]
    public void WasMigrated_flags_only_a_config_that_actually_had_to_change()
    {
        // Startup rewrites the file when this is set, so a false positive would mean writing to
        // disk on every launch for no reason.
        Assert.False(new DockConfig().Migrate().WasMigrated);
        Assert.False(JsonSerializer.Deserialize<DockConfig>(
            "{ \"Docks\": [ {} ] }", Options)!.Migrate().WasMigrated);

        Assert.True(JsonSerializer.Deserialize<DockConfig>(
            "{ \"Items\": [], \"Edge\": \"Top\" }", Options)!.Migrate().WasMigrated);
    }

    // ---- Upgrading from the single-dock config shape ----------------------

    [Fact]
    public void A_pre_multi_dock_config_becomes_one_dock_with_its_settings_intact()
    {
        // What Anchor wrote before it supported more than one dock: the items and placement sat
        // at the top level. Everything about that dock has to survive the upgrade, or a user's
        // pinned items and their snapped edge quietly vanish on first run of the new build.
        const string legacy = """
        {
          "Items": [
            { "Kind": "Application", "DisplayName": "Notepad", "Target": "C:\\notepad.exe" },
            { "Kind": "WebLink", "DisplayName": "Example", "Target": "https://example.com/" }
          ],
          "Snapped": true,
          "Edge": "Left",
          "FreeX": 120,
          "FreeY": 340,
          "AutoHide": false,
          "AlwaysOnTop": false,
          "VerticalWhenSideSnapped": true,
          "Theme": "Light",
          "Language": "de",
          "Seeded": true
        }
        """;

        var cfg = JsonSerializer.Deserialize<DockConfig>(legacy, Options)!.Migrate();

        var dock = Assert.Single(cfg.Docks);
        Assert.Equal(2, dock.Items.Count);
        Assert.Equal("Notepad", dock.Items[0].DisplayName);
        Assert.True(dock.Snapped);
        Assert.Equal(DockEdge.Left, dock.Edge);
        Assert.Equal(120, dock.FreeX);
        Assert.Equal(340, dock.FreeY);
        Assert.False(dock.AutoHide);
        Assert.False(dock.AlwaysOnTop);
        Assert.True(dock.VerticalWhenSideSnapped);

        // App-wide settings stay app-wide.
        Assert.Equal(DockTheme.Light, cfg.Theme);
        Assert.Equal("de", cfg.Language);
        Assert.True(cfg.Seeded);
    }

    [Fact]
    public void An_upgraded_config_stops_writing_the_legacy_fields()
    {
        // The file converts itself on first save rather than carrying both shapes forever, so a
        // later load can never see a stale top-level dock alongside the real one.
        var cfg = JsonSerializer.Deserialize<DockConfig>(
            "{ \"Items\": [], \"Snapped\": true, \"Edge\": \"Top\" }", Options)!.Migrate();

        // Inspected at the root specifically: the same names legitimately appear *inside* each
        // dock, which is exactly where they moved to.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(cfg, Options));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Docks", out _));
        foreach (var legacy in new[]
                 {
                     "Items", "Snapped", "Edge", "FreeX", "FreeY",
                     "AutoHide", "AlwaysOnTop", "VerticalWhenSideSnapped",
                 })
            Assert.False(root.TryGetProperty(legacy, out _), $"root still writes '{legacy}'");

        // …and the dock it produced is still the migrated one.
        Assert.True(cfg.Primary.Snapped);
        Assert.Equal(DockEdge.Top, cfg.Primary.Edge);
    }

    [Fact]
    public void A_config_that_already_has_docks_ignores_leftover_legacy_fields()
    {
        // Both shapes present means the file was written by the new build and hand-edited, or
        // merged badly. Docks is authoritative; adopting the stale top-level dock too would
        // duplicate the user's strip.
        const string mixed = """
        {
          "Docks": [ { "Name": "Main", "Snapped": true, "Edge": "Right" } ],
          "Items": [ { "DisplayName": "Stale" } ],
          "Edge": "Bottom"
        }
        """;

        var cfg = JsonSerializer.Deserialize<DockConfig>(mixed, Options)!.Migrate();

        var dock = Assert.Single(cfg.Docks);
        Assert.Equal("Main", dock.Name);
        Assert.Equal(DockEdge.Right, dock.Edge);
        Assert.Empty(dock.Items);
    }

    [Fact]
    public void Several_docks_round_trip_with_their_own_items_and_edges()
    {
        var cfg = new DockConfig
        {
            Docks =
            {
                new DockProfile
                {
                    Name = "Left screen",
                    Snapped = true,
                    Edge = DockEdge.Left,
                    FreeX = 0,
                    FreeY = 200,
                    Items = { new DockItem { DisplayName = "A", Target = @"C:\a.exe" } },
                },
                new DockProfile
                {
                    Name = "Right screen",
                    AutoHide = false,
                    Items = { new DockItem { DisplayName = "B", Target = @"C:\b.exe" } },
                },
            },
        };

        var loaded = JsonSerializer.Deserialize<DockConfig>(
            JsonSerializer.Serialize(cfg, Options), Options)!.Migrate();

        Assert.Equal(2, loaded.Docks.Count);
        Assert.Equal("Left screen", loaded.Docks[0].Name);
        Assert.Equal(DockEdge.Left, loaded.Docks[0].Edge);
        Assert.Equal("A", Assert.Single(loaded.Docks[0].Items).DisplayName);
        Assert.False(loaded.Docks[1].AutoHide);
        Assert.Equal("B", Assert.Single(loaded.Docks[1].Items).DisplayName);
        Assert.Same(loaded.Docks[0], loaded.Primary);
    }

    [Fact]
    public void Each_dock_gets_its_own_id()
    {
        Assert.NotEqual(new DockProfile().Id, new DockProfile().Id);
        Assert.False(string.IsNullOrWhiteSpace(new DockProfile().Id));
    }

    // ---- App-wide settings -------------------------------------------------

    [Fact]
    public void Config_saved_before_the_language_and_hotkey_options_still_loads()
    {
        // Upgrading users have a config file with neither field. The language must stay "follow
        // Windows" and — since JSON omission means "take the property initializer" — the default
        // shortcut comes along, so the feature is on for them without a re-save.
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Seeded\": true }", Options);

        Assert.NotNull(cfg);
        Assert.Equal(string.Empty, cfg!.Language);
        Assert.Equal("Ctrl+Alt+A", cfg.Hotkey);
        Assert.True(cfg.HotkeyEnabled);
    }

    [Fact]
    public void The_default_hotkey_string_is_one_the_gesture_parser_accepts()
    {
        // DockConfig stores the shortcut as text; if the two ever drift, Anchor would ship with
        // a shortcut that silently fails to register.
        Assert.True(HotkeyGesture.TryParse(new DockConfig().Hotkey, out var gesture));
        Assert.True(gesture.IsValid);
    }

    [Fact]
    public void An_unparseable_hotkey_does_not_stop_the_config_loading()
    {
        // Hand-edited nonsense degrades to "no shortcut", not to a lost dock.
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Hotkey\": \"Ctrl+Nonsense\" }", Options);

        Assert.NotNull(cfg);
        Assert.False(HotkeyGesture.TryParse(cfg!.Hotkey, out _));
    }

    [Fact]
    public void A_group_and_its_children_round_trip_through_json()
    {
        // Children are the one nested structure in the config, so this is the load path that
        // would silently flatten a user's groups if the property ever stopped serializing.
        var cfg = new DockConfig
        {
            Docks =
            {
                new DockProfile
                {
                    Items =
                    {
                        new DockItem
                        {
                            Kind = DockItemKind.Group,
                            DisplayName = "Dev",
                            Children =
                            {
                                new DockItem { Kind = DockItemKind.Application, DisplayName = "Code", Target = @"C:\code.exe" },
                                new DockItem { Kind = DockItemKind.WebLink, DisplayName = "Docs", Target = "https://example.com/" },
                            },
                        },
                    },
                },
            },
        };

        var loaded = JsonSerializer.Deserialize<DockConfig>(
            JsonSerializer.Serialize(cfg, Options), Options)!.Migrate();

        var group = Assert.Single(loaded.Primary.Items);
        Assert.True(group.IsGroup);
        Assert.Equal("Dev", group.DisplayName);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal("Code", group.Children[0].DisplayName);
        Assert.Equal(DockItemKind.WebLink, group.Children[1].Kind);
    }

    [Fact]
    public void Config_saved_before_the_running_indicator_option_still_loads_with_it_on()
    {
        // An existing config file has no such field; omission must mean "take the default", so
        // upgrading users get the feature without having to find the switch.
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Seeded\": true }", Options);

        Assert.NotNull(cfg);
        Assert.True(cfg!.ShowRunningIndicators);
    }

    [Fact]
    public void Config_saved_before_the_open_apps_option_still_loads_with_it_on()
    {
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Seeded\": true }", Options);

        Assert.NotNull(cfg);
        Assert.True(cfg!.ShowRunningApps);
    }

    [Fact]
    public void An_open_app_is_never_written_into_a_saved_dock()
    {
        // The open-apps section lives only on the strip; were one to reach a profile, it must not
        // come back as a pinned icon on the next launch.
        var profile = new DockProfile();
        profile.Items.Add(DockItem.CreateOpenApp("Notepad", @"C:\Windows\notepad.exe", @"C:\Windows\notepad.exe"));
        var json = JsonSerializer.Serialize(profile, Options);

        Assert.DoesNotContain("IsTransient", json);
        Assert.DoesNotContain("RunningAppKey", json);
    }

    [Fact]
    public void Config_saved_before_the_theme_option_still_loads_as_Dark()
    {
        // An existing config file has no "Theme" field. It must deserialize to Dark so the app
        // keeps its original dark-taskbar look for users who upgrade.
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Seeded\": true }", Options);

        Assert.NotNull(cfg);
        Assert.Equal(DockTheme.Dark, cfg!.Theme);
    }

    [Theory]
    [InlineData(DockTheme.Light)]
    [InlineData(DockTheme.Dark)]
    [InlineData(DockTheme.System)]
    public void Theme_round_trips_through_json_as_a_string(DockTheme theme)
    {
        var json = JsonSerializer.Serialize(new DockConfig { Theme = theme }, Options);
        Assert.Contains($"\"{theme}\"", json); // stored by name, not ordinal

        var loaded = JsonSerializer.Deserialize<DockConfig>(json, Options);
        Assert.Equal(theme, loaded!.Theme);
    }

    [Fact]
    public void A_config_saved_before_the_personalization_options_loads_with_the_old_look()
    {
        // Density, magnification, per-item shortcuts and the update check all arrived together.
        // Their defaults have to reproduce how Anchor behaved before they existed, or an upgrade
        // would silently change the dock — and, for the update check, silently start contacting
        // the network.
        var cfg = JsonSerializer.Deserialize<DockConfig>("{ \"Seeded\": true }", Options);

        Assert.NotNull(cfg);
        Assert.Equal(DockDensity.Medium, cfg!.Density); // the taskbar-matching 40px cells
        Assert.False(cfg.AccentTint);
        Assert.False(cfg.Magnify);
        Assert.False(cfg.ItemHotkeysEnabled);
        Assert.Equal(string.Empty, cfg.SearchHotkey);
        Assert.False(cfg.CheckForUpdates);
        Assert.True(cfg.GroupOpenOnHover); // groups already opened on hover before this was a setting
    }

    [Fact]
    public void CopyAppSettingsFrom_takes_every_app_wide_setting_and_no_docks()
    {
        // What import relies on: the running DockConfig instance is shared by every window, so an
        // import fills it in rather than swapping in the deserialized one.
        var source = new DockConfig
        {
            Theme = DockTheme.Light,
            Density = DockDensity.Large,
            Language = "ja",
            Hotkey = "Ctrl+Alt+K",
            HotkeyEnabled = false,
            SearchHotkey = "Ctrl+Alt+Space",
            ItemHotkeysEnabled = true,
            AccentTint = true,
            Magnify = true,
            GroupOpenOnHover = false,
            LaunchAtStartup = true,
            ShowRunningIndicators = false,
            ShowRunningApps = false,
            CheckForUpdates = true,
            SkippedUpdate = "9.9.9",
            Seeded = true,
        };
        source.Docks.Add(new DockProfile { Name = "Theirs" });

        var target = new DockConfig();
        target.Docks.Add(new DockProfile { Name = "Mine" });
        target.CopyAppSettingsFrom(source);

        Assert.Equal(DockTheme.Light, target.Theme);
        Assert.Equal(DockDensity.Large, target.Density);
        Assert.Equal("ja", target.Language);
        Assert.Equal("Ctrl+Alt+K", target.Hotkey);
        Assert.False(target.HotkeyEnabled);
        Assert.Equal("Ctrl+Alt+Space", target.SearchHotkey);
        Assert.True(target.ItemHotkeysEnabled);
        Assert.True(target.AccentTint);
        Assert.True(target.Magnify);
        Assert.False(target.GroupOpenOnHover);
        Assert.True(target.LaunchAtStartup);
        Assert.False(target.ShowRunningIndicators);
        Assert.False(target.ShowRunningApps);
        Assert.True(target.CheckForUpdates);
        Assert.Equal("9.9.9", target.SkippedUpdate);

        // The docks are replaced separately, and "have you been past first run?" is a fact about
        // this installation rather than about the file that was imported.
        Assert.Equal("Mine", Assert.Single(target.Docks).Name);
        Assert.False(target.Seeded);
    }

    [Fact]
    public void An_items_shortcut_and_folder_flyout_round_trip_through_json()
    {
        var config = new DockConfig();
        config.Docks.Add(new DockProfile
        {
            Items =
            {
                new DockItem { DisplayName = "Notepad", Hotkey = "Ctrl+Alt+1" },
                new DockItem { Kind = DockItemKind.Folder, DisplayName = "Home", FolderFlyout = true },
                new DockItem
                {
                    Kind = DockItemKind.Group,
                    DisplayName = "Tools",
                    IsCollapsed = true,
                    Children = { new DockItem { DisplayName = "Calc" } },
                },
            },
        });

        var loaded = JsonSerializer.Deserialize<DockConfig>(
            JsonSerializer.Serialize(config, Options), Options)!.Migrate();

        var items = Assert.Single(loaded.Docks).Items;
        Assert.Equal("Ctrl+Alt+1", items[0].Hotkey);
        Assert.True(items[1].FolderFlyout);
        Assert.True(items[2].IsCollapsed);
    }
}
