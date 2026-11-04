using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Anchor.Models;

namespace Anchor.Services;

/// <summary>Loads and saves the dock configuration as JSON under %AppData%\Anchor.</summary>
public static class DockStore
{
    /// <summary>
    /// Where Anchor keeps everything it writes: <c>dock.json</c> and the icon cache.
    /// <c>%AppData%\Anchor</c> normally, or whatever <c>ANCHOR_DATA_DIR</c> names.
    /// <para>
    /// The override exists so a UI test run (see <c>tests/Anchor.UITests</c>) drives a throwaway
    /// dock instead of the signed-in user's real one — reset-to-defaults and remove-item are
    /// exactly the paths worth covering, and exactly the ones you can't point at live data.
    /// It doubles as a way to run a genuinely portable copy from a removable drive.
    /// </para>
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("ANCHOR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch
            {
                // An unusable override must not cost the user their settings: fall through to
                // the default location rather than failing to start.
            }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anchor");
    }

    private static string Dir => DataDirectory;

    private static readonly string FilePath = Path.Combine(DataDirectory, "dock.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // The default encoder escapes anything outside a conservative ASCII set, which turns
        // "Ctrl+Alt+A" into "Ctrl+Alt+A" and a name like "書類" into a run of escapes.
        // dock.json is documented as hand-editable, so it is written to be read: this file is
        // never embedded in HTML or a script, which is the only context that escaping guards.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Reads the config, always returning something usable: a file that is missing, unreadable or
    /// corrupt yields defaults rather than an exception, because the alternative is an app that
    /// won't start. The result is always migrated, so callers can rely on
    /// <see cref="DockConfig.Docks"/> being populated.
    /// <para>
    /// A file that exists but couldn't be used is preserved (see <see cref="PreserveUnreadable"/>)
    /// before defaults are handed back — <see cref="Anchor.DockManager.Start"/> saves whatever this
    /// returns on first run, and without a copy that save would overwrite the one file that still
    /// held the user's real dock with a freshly seeded one, discarding it for good.
    /// </para>
    /// </summary>
    public static DockConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = ReadAllTextWithRetry(FilePath);
                var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options);
                if (cfg is not null)
                    return cfg.Migrate();
                Diag.Log("DockStore.Load: dock.json parsed to no config (a JSON 'null' body) — treating as corrupt");
            }
        }
        catch (Exception ex)
        {
            Diag.Log("DockStore.Load failed: " + ex.Message);
        }
        PreserveUnreadable();
        return new DockConfig().Migrate();
    }

    /// <summary>
    /// Reads the file, riding out a lock held by another process — most plausibly a moment of
    /// antivirus scanning — rather than treating the very first failed attempt as corruption. A
    /// lock like that clears in milliseconds; a handful of short retries costs nothing next to what
    /// <see cref="Load"/> would otherwise do with it, which is discard the user's whole config.
    /// </summary>
    private static string ReadAllTextWithRetry(string path)
    {
        const int attempts = 3;
        for (int i = 1; ; i++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Copies an unreadable <c>dock.json</c> aside so a bad load never silently costs the user
    /// their configuration — only the failed read does, not the file itself. Best-effort: a failure
    /// here only costs the backup, never the fallback to defaults that keeps Anchor starting.
    /// </summary>
    private static void PreserveUnreadable()
    {
        try
        {
            if (!File.Exists(FilePath))
                return; // first run — nothing to preserve
            var backup = FilePath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(FilePath, backup, overwrite: true);
            Diag.Log($"DockStore.Load: preserved the unreadable config as '{backup}'");
        }
        catch (Exception ex)
        {
            Diag.Log("DockStore.Load: failed to preserve the unreadable config: " + ex.Message);
        }
    }

    public static void Save(DockConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(config, Options);
            // Write-then-rename for crash safety.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Log("DockStore.Save failed: " + ex.Message);
        }
    }

    // ---- Import / export ---------------------------------------------------
    //
    // The exported file is exactly the same shape as dock.json — not a format of its own — so a
    // backup can also be dropped straight into %AppData%\Anchor by hand, and a config copied out
    // of there imports without conversion.

    /// <summary>Writes <paramref name="config"/> to an arbitrary path.</summary>
    /// <returns>True on success; a failure is logged and reported rather than thrown, since the
    /// caller is a button in the Settings window.</returns>
    public static bool ExportTo(DockConfig config, string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"DockStore.ExportTo('{path}') failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads a configuration from an arbitrary path, migrated to the current shape.
    /// <para>
    /// Unlike <see cref="Load"/> this returns null on any problem instead of falling back to
    /// defaults. The difference is deliberate: at startup, defaults beat refusing to run, but
    /// importing a file that turned out not to be an Anchor config must not silently wipe the
    /// user's real dock and replace it with a seeded one.
    /// </para>
    /// </summary>
    public static DockConfig? ImportFrom(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(path), Options);
            if (config is null)
                return null;

            // Checked BEFORE migrating: any JSON object deserializes happily into a DockConfig
            // full of defaults, and Migrate would then manufacture a dock for it — so a file that
            // is not an Anchor config would import as a plausible-looking empty one.
            if (config.Docks.Count == 0 && config.LegacyItems is null)
                return null;

            return config.Migrate();
        }
        catch (Exception ex)
        {
            Diag.Log($"DockStore.ImportFrom('{path}') failed: {ex.Message}");
            return null;
        }
    }
}
