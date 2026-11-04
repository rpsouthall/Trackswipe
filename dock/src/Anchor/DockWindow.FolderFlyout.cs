using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Anchor;

/// <summary>
/// Folder fly-outs — the macOS "stack". A folder item with
/// <see cref="DockItem.FolderFlyout"/> on lists what is inside it in the same
/// <see cref="DockWindow.ShowDockBarFlyout">dock bar</see> a group uses, instead of handing the
/// folder to Explorer. Partial of <see cref="DockWindow"/>.
/// <para>
/// The entries are built fresh every time the bar opens and are never persisted: they are a view
/// of what is on disk right now, not pinned items. That is also why they are ordinary
/// <see cref="DockItem"/>s rather than a type of their own — the bar, the icon resolution and the
/// launcher all already know how to handle one, and a parallel type would have to re-implement
/// each of them to say the same thing.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>
    /// Opens a bar over <paramref name="path"/>'s contents, anchored on the dock icon that was
    /// clicked. Clicking a folder inside it re-opens the bar on <em>that</em> folder — still
    /// anchored on the same dock icon, since the cell the click came from goes away with the bar.
    /// <para>
    /// <see cref="FolderListing.Read"/> runs on a thread-pool thread rather than inline: it walks
    /// the target directory, and a folder on a slow or disconnected network share can make that
    /// walk take far longer than a click should ever be able to block the UI thread for. This is
    /// reached directly from <c>Item_Click</c>, so without this the whole dock would freeze for
    /// as long as the listing did.
    /// </para>
    /// </summary>
    private async void ShowFolderFlyout(FrameworkElement anchor, string path)
    {
        var entries = await Task.Run(() => FolderListing.Read(path));

        // Always offer the way out to the real file manager: a stack is for the common case, and
        // "everything else in here" has to remain one click away.
        var openInExplorer = new DockItem
        {
            Kind = DockItemKind.Folder,
            DisplayName = Loc.Get("Folder.OpenInExplorer"),
            Target = path,
            CustomGlyph = "", // FolderOpen
        };

        var items = new List<DockItem>(entries.Count + 1);
        items.AddRange(entries);
        items.Add(openInExplorer);

        ShowDockBarFlyout(anchor, new DockBarOptions(
            items,
            Loc.Get("Folder.Empty"),
            // Clicking a subfolder drills in — the bar re-opens on that folder, still anchored on
            // the dock icon, since the cell the click came from goes away with the bar. The
            // "Open in File Explorer" cell is a folder too but is deliberately excluded: its whole
            // job is to leave the bar.
            OnActivate: entry =>
            {
                if (entry.Kind != DockItemKind.Folder || !entries.Contains(entry))
                    return false;
                ShowFolderFlyout(anchor, entry.Target);
                return true;
            },
            OnContext: ShowFolderEntryMenu));

        // Icons resolve asynchronously (shell thumbnails), and these items were created a moment
        // ago, so the bar opens on glyphs and fills in as they land — the cells subscribe to the
        // item for exactly that (see BuildBarCell).
        _ = LoadFolderIconsAsync(entries);
    }

    private void ShowFolderEntryMenu(
        FrameworkElement target, DockItem entry, Flyout owner, ContextRequestedEventArgs e)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Mi(Loc.Get("Menu.Open"), () => { owner.Hide(); Launcher.Launch(entry); }));
        menu.Items.Add(Mi(Loc.Get("Menu.PinToDock"), () =>
        {
            owner.Hide();
            // A copy, not the transient entry itself: the bar's items are rebuilt on every open
            // and pinning one must outlive the bar it came from.
            AddDockItem(new DockItem
            {
                Kind = entry.Kind,
                DisplayName = entry.DisplayName,
                Target = entry.Target,
            });
        }));

        if (e.TryGetPosition(target, out var pos))
            menu.ShowAt(target, pos);
        else
            menu.ShowAt(target);

        static MenuFlyoutItem Mi(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }
    }

    // Resolved concurrently rather than one at a time (see DockWindow.LoadIconsAsync): each
    // entry's icon is an independent shell-thumbnail lookup, and a folder bar can hold up to
    // FolderListing.MaxEntries of them.
    private Task LoadFolderIconsAsync(List<DockItem> entries) =>
        Task.WhenAll(entries.Select(LoadOneIconAsync));
}
