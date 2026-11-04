using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// Import and export — the only parts of <see cref="DockStore"/> that take a path rather than
/// writing to the user's real data directory, and the parts where getting it wrong loses a dock.
/// </summary>
public class DockStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "anchor-store-tests", Guid.NewGuid().ToString("N"));

    public DockStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // A temp folder left behind is untidy, not a failure.
        }
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private static DockConfig SampleConfig()
    {
        var config = new DockConfig
        {
            Theme = DockTheme.Light,
            Density = DockDensity.Large,
            Language = "de",
            Magnify = true,
            GroupOpenOnHover = false,
            ItemHotkeysEnabled = true,
        };
        config.Docks.Add(new DockProfile
        {
            Name = "Work",
            Edge = DockEdge.Left,
            Snapped = true,
            Items =
            {
                new DockItem
                {
                    Kind = DockItemKind.Application,
                    DisplayName = "Notepad",
                    Target = @"C:\Windows\System32\notepad.exe",
                    Hotkey = "Ctrl+Alt+1",
                },
                new DockItem
                {
                    Kind = DockItemKind.Group,
                    DisplayName = "Tools",
                    Children = { new DockItem { DisplayName = "Calc", Target = "calc.exe" } },
                },
            },
        });
        return config;
    }

    [Fact]
    public void An_exported_config_imports_back_unchanged()
    {
        var path = Path_("backup.json");
        var original = SampleConfig();

        Assert.True(DockStore.ExportTo(original, path));
        var restored = DockStore.ImportFrom(path);

        Assert.NotNull(restored);
        Assert.Equal(DockTheme.Light, restored!.Theme);
        Assert.Equal(DockDensity.Large, restored.Density);
        Assert.Equal("de", restored.Language);
        Assert.True(restored.Magnify);
        Assert.False(restored.GroupOpenOnHover);
        Assert.True(restored.ItemHotkeysEnabled);

        var dock = Assert.Single(restored.Docks);
        Assert.Equal("Work", dock.Name);
        Assert.Equal(DockEdge.Left, dock.Edge);
        Assert.True(dock.Snapped);
        Assert.Equal(2, dock.Items.Count);
        Assert.Equal("Ctrl+Alt+1", dock.Items[0].Hotkey);

        // A group's children have to survive the round-trip too, or importing a backup quietly
        // empties every group in it.
        var group = dock.Items[1];
        Assert.Equal(DockItemKind.Group, group.Kind);
        Assert.Equal("Calc", Assert.Single(group.Children).DisplayName);
    }

    [Fact]
    public void Export_creates_the_directory_it_is_asked_to_write_into()
    {
        var path = Path_(Path.Combine("nested", "deeper", "backup.json"));

        Assert.True(DockStore.ExportTo(SampleConfig(), path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Importing_a_file_that_is_not_a_config_fails_instead_of_yielding_defaults()
    {
        // The important half: Load() falls back to defaults because refusing to start is worse,
        // but an import that did the same would replace the user's real dock with a seeded one.
        var notJson = Path_("notes.json");
        File.WriteAllText(notJson, "this is not json at all");
        Assert.Null(DockStore.ImportFrom(notJson));

        var emptyObject = Path_("empty.json");
        File.WriteAllText(emptyObject, "{}");
        Assert.Null(DockStore.ImportFrom(emptyObject));

        var otherAppsJson = Path_("other.json");
        File.WriteAllText(otherAppsJson, """{ "name": "some-package", "version": "1.0.0" }""");
        Assert.Null(DockStore.ImportFrom(otherAppsJson));
    }

    [Fact]
    public void Importing_a_missing_file_fails_quietly()
    {
        Assert.Null(DockStore.ImportFrom(Path_("nothing-here.json")));
    }

    [Fact]
    public void A_pre_multi_dock_backup_still_imports()
    {
        // Files exported by hand — or copied out of an old %AppData%\Anchor — carry the flat
        // single-dock shape, which Migrate folds into Docks[0].
        var path = Path_("legacy.json");
        File.WriteAllText(path, """
            {
              "Items": [ { "Kind": "Application", "DisplayName": "Notepad", "Target": "notepad.exe" } ],
              "Snapped": true,
              "Edge": "Top",
              "Theme": "Light"
            }
            """);

        var restored = DockStore.ImportFrom(path);

        Assert.NotNull(restored);
        var dock = Assert.Single(restored!.Docks);
        Assert.Equal(DockEdge.Top, dock.Edge);
        Assert.True(dock.Snapped);
        Assert.Equal("Notepad", Assert.Single(dock.Items).DisplayName);
    }

    [Fact]
    public void Export_reports_failure_rather_than_throwing_on_an_unusable_path()
    {
        // The caller is a button in the Settings window; it shows a message, so this must not
        // come back as an exception.
        Assert.False(DockStore.ExportTo(SampleConfig(), Path_("bad\0name.json")));
    }
}
