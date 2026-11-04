using Anchor.Interop;
using Anchor.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Anchor.Services;

/// <summary>
/// Turns whatever the shell puts on a drag into targets the dock can hold.
/// <para>
/// Files, folders and shortcuts dragged from Explorer or the desktop are simply their paths, and
/// arrive as <c>StandardDataFormats.StorageItems</c> (CF_HDROP under the covers). An app dragged
/// out of the <b>Start menu</b> is not a file at all: Start's app list is the virtual
/// <c>AppsFolder</c> namespace, whose items are identified by an AppUserModelID rather than a
/// path, and the shell offers such a drag as <c>"Shell IDList Array"</c> (CFSTR_SHELLIDLIST) and
/// <em>nothing else</em> — no CF_HDROP, so <see cref="DataPackageView.Contains"/> reports no
/// storage items. That is why dropping an app from Start used to show the "no entry" cursor and do
/// nothing: the dock asked only that question.
/// </para>
/// <para>
/// The items are readable all the same — <see cref="DataPackageView.GetStorageItemsAsync"/>
/// returns them whether or not <c>Contains</c> admitted to them, as path-less
/// <see cref="StorageFile"/>s standing in for shell items — so the format's presence is what this
/// asks about, and a path-less item is then resolved through its shell properties: the app it
/// points at if it has one, otherwise its AppUserModelID, as a <c>shell:AppsFolder\…</c> target
/// that ShellExecute launches like any other.
/// </para>
/// </summary>
public static class ShellDrop
{
    /// <summary>CFSTR_SHELLIDLIST — how the shell describes items that may not be files.</summary>
    public const string ShellIdListFormat = "Shell IDList Array";

    /// <summary>The exe a Start-menu entry stands for, when there is one (a Win32 app).</summary>
    private const string LinkTargetProperty = "System.Link.TargetParsingPath";

    /// <summary>What identifies an app with no exe of its own (a Store app).</summary>
    private const string AppUserModelIdProperty = "System.AppUserModel.ID";

    /// <summary>One thing the user dropped, resolved to something the dock can store.</summary>
    /// <param name="Target">What <see cref="Launcher"/> would open: a path, or a shell command.</param>
    /// <param name="DisplayName">The name to show in the dock.</param>
    /// <param name="Kind">The dock item kind the target resolves to.</param>
    public sealed record DroppedItem(string Target, string DisplayName, DockItemKind Kind)
    {
        /// <summary>True when the target is a real path — i.e. something that can be handed to
        /// another app on a command line, which a Store app's identifier cannot.</summary>
        public bool IsFileSystemPath { get; init; }

        public DockItem ToDockItem() =>
            new() { Kind = Kind, DisplayName = DisplayName, Target = Target };
    }

    /// <summary>
    /// True when the drag carries items the dock could add — files or folders, or shell items such
    /// as a Start-menu app. Cheap and synchronous, so a DragOver handler can call it on every
    /// pointer move.
    /// </summary>
    public static bool HasShellItems(DataPackageView data)
    {
        try
        {
            return data.Contains(StandardDataFormats.StorageItems) ||
                   data.AvailableFormats.Contains(ShellIdListFormat);
        }
        catch (Exception ex)
        {
            Diag.Log("ShellDrop.HasShellItems failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// True when the drag carries anything the Add window can turn into dock items: shell items
    /// (see <see cref="HasShellItems"/>), or a web link / plain text a browser drag provides.
    /// </summary>
    public static bool CanAcceptAny(DataPackageView data)
    {
        try
        {
            return HasShellItems(data) ||
                   data.Contains(StandardDataFormats.WebLink) ||
                   data.Contains(StandardDataFormats.Text);
        }
        catch (Exception ex)
        {
            Diag.Log("ShellDrop.CanAcceptAny failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Like <see cref="ReadAsync"/>, but a drag with no shell items is also read as a web link or
    /// text — the same order the dock strip itself uses. Text that isn't a plausible web address
    /// resolves to nothing. Never throws, and has the same must-be-live requirement.
    /// </summary>
    public static async Task<IReadOnlyList<DroppedItem>> ReadAnyAsync(DataPackageView data)
    {
        try
        {
            if (HasShellItems(data))
                return await ReadAsync(data);

            string? text = data.Contains(StandardDataFormats.WebLink)
                ? (await data.GetWebLinkAsync())?.ToString()
                : data.Contains(StandardDataFormats.Text)
                    ? await data.GetTextAsync()
                    : null;

            return DockItemFactory.TryNormalizeWebUrl(text) is { } url
                ? [new DroppedItem(url, DockItemFactory.SuggestName(url), DockItemKind.WebLink)]
                : [];
        }
        catch (Exception ex)
        {
            Diag.Log($"ShellDrop.ReadAnyAsync failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// True when the drag carries CF_HDROP-backed storage items specifically — as opposed to a
    /// Start-menu app, which arrives as Shell IDList Array only. Callers that need to tell the two
    /// apart (a dropped app has no path to hand an app on a command line) ask this rather than
    /// calling <see cref="DataPackageView.Contains"/> directly: querying a live drag's view can
    /// throw if the drag source lets go of its data mid-query, which <see cref="HasShellItems"/>
    /// already guards against, and a caller asking the same kind of question deserves the same
    /// guard rather than a second, unguarded copy of it.
    /// </summary>
    public static bool HasStorageItems(DataPackageView data)
    {
        try
        {
            return data.Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception ex)
        {
            Diag.Log("ShellDrop.HasStorageItems failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Resolves everything on the drag, in the order it was dropped. Never throws: an item that
    /// cannot be resolved is logged and left out, so one odd one can't lose the rest.
    /// <para>
    /// Must be called while the drag event is still live — inside the handler, or under a deferral
    /// taken from it — since a <see cref="DataPackageView"/> from a drag is only readable for as
    /// long as the source's data object is.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<DroppedItem>> ReadAsync(DataPackageView data)
    {
        if (!HasShellItems(data))
            return [];

        var resolved = new List<DroppedItem>();
        try
        {
            foreach (var item in await data.GetStorageItemsAsync())
            {
                // The ordinary case: a file, folder or shortcut, which is its path.
                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    resolved.Add(FromPath(item.Path));
                    continue;
                }

                var app = await TryResolveShellItemAsync(item);
                if (app is not null)
                    resolved.Add(app);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"ShellDrop.ReadAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
        return resolved;
    }

    /// <summary>A dropped path, classified the same way the Add window classifies a typed one.</summary>
    private static DroppedItem FromPath(string path) =>
        new(path, DockItemFactory.SuggestName(path), DockItemFactory.Classify(path))
        {
            IsFileSystemPath = true,
        };

    /// <summary>
    /// Works out what a dropped item with no path is — in practice an entry from the Start menu.
    /// <para>
    /// Its exe is preferred when it has one, so that an app dragged in is the same dock item as
    /// one added by browsing to it: running indicators, "Open file location" and arguments all key
    /// off a real path. A Store app has no exe to point at, so it is stored as the AppUserModelID
    /// that identifies it instead.
    /// </para>
    /// </summary>
    private static async Task<DroppedItem?> TryResolveShellItemAsync(IStorageItem item)
    {
        var properties = item switch
        {
            StorageFile file => file.Properties,
            StorageFolder folder => folder.Properties,
            _ => null,
        };
        if (properties is null)
        {
            Diag.Log($"ShellDrop: dropped item '{item.Name}' has no path, and nothing to ask about it");
            return null;
        }

        IDictionary<string, object>? values = null;
        try
        {
            values = await properties.RetrievePropertiesAsync([LinkTargetProperty, AppUserModelIdProperty]);
        }
        catch (Exception ex)
        {
            Diag.Log($"ShellDrop: reading '{item.Name}'s shell properties failed: {ex.GetType().Name}: {ex.Message}");
        }

        // The shell's own display name for the item ("Google Chrome"), which is friendlier than
        // anything derivable from the target ("chrome").
        string name = string.IsNullOrWhiteSpace(item.Name) ? "App" : item.Name;

        if (Value(values, LinkTargetProperty) is { } target &&
            (File.Exists(target) || Directory.Exists(target)))
        {
            Diag.Log($"ShellDrop: '{name}' resolved to {target}");
            return FromPath(target) with { DisplayName = name };
        }

        if (Value(values, AppUserModelIdProperty) is { } appId)
        {
            string command = DockItemFactory.AppsFolderPrefix + appId;
            if (CanShellResolve(command))
            {
                Diag.Log($"ShellDrop: '{name}' resolved to {command}");
                return new DroppedItem(command, name, DockItemKind.Application);
            }
            Diag.Log($"ShellDrop: the shell doesn't recognize '{command}', so '{name}' was skipped");
            return null;
        }

        Diag.Log($"ShellDrop: nothing to launch behind the dropped item '{name}'");
        return null;
    }

    private static string? Value(IDictionary<string, object>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) &&
        value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>
    /// Confirms the shell can make sense of a target before the dock stores it — an icon that
    /// launches nothing is worse than a drop that visibly declined. Parsing it is what
    /// ShellExecute would do, minus the launch.
    /// </summary>
    private static bool CanShellResolve(string target)
    {
        nint pidl = nint.Zero;
        try
        {
            return NativeMethods.SHParseDisplayName(target, nint.Zero, out pidl, 0, out _) == 0 &&
                   pidl != nint.Zero;
        }
        catch (Exception ex)
        {
            Diag.Log($"ShellDrop: parsing '{target}' failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (pidl != nint.Zero)
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pidl);
        }
    }
}
