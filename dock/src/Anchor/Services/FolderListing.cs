using Anchor.Models;

namespace Anchor.Services;

/// <summary>
/// Reads a folder's contents as dock items — what a folder fly-out ("stack") shows.
/// <para>
/// The entries are a view of what is on disk right now and are never persisted, which is why they
/// are ordinary <see cref="DockItem"/>s rather than a type of their own: the fly-out bar, the icon
/// resolution and the launcher all already know how to handle one.
/// </para>
/// <para>
/// Separate from <c>DockWindow</c> so the ordering and filtering rules are testable against a real
/// directory, which is the only way to be sure "folders first, then files, each alphabetical" and
/// the hidden-file exclusion actually hold.
/// </para>
/// </summary>
public static class FolderListing
{
    /// <summary>
    /// How many entries a folder bar will show. A stack is a shortcut to the handful of things you
    /// actually reach for, and a bar with a thousand cells in it is a worse file manager than the
    /// one already on the machine — past this, the bar's "Open in File Explorer" cell is the answer.
    /// </summary>
    public const int MaxEntries = 60;

    /// <summary>
    /// Hard ceiling on how many raw directory entries are ever pulled off disk before sorting,
    /// independent of <see cref="MaxEntries"/>/<c>max</c>. "Folders first, then alphabetical"
    /// needs every candidate entry visible to the sort to be correct, but <c>OrderBy</c> cannot
    /// yield its first result until it has buffered the <em>entire</em> input — so a naive
    /// <c>.OrderBy(...).Take(max)</c> walks a folder's contents in full no matter how small <c>max</c>
    /// is. A folder holding tens of thousands of entries — especially over a slow network share,
    /// where each entry can cost a round trip — must not turn "click a stack" into a filesystem
    /// walk proportional to the folder's total size rather than to what's actually shown. Past
    /// this many raw entries the sort simply works with what it's already seen; the exact top
    /// <see cref="MaxEntries"/> alphabetically is not worth guaranteeing for a folder this large,
    /// and the bar's own "Open in File Explorer" cell is the honest answer for one.
    /// </summary>
    private const int EnumerationCap = 5000;

    /// <summary>
    /// The folder's contents as dock items: directories first, then files, each alphabetical —
    /// Explorer's own order, so the bar lists things where the user expects to find them.
    /// <para>
    /// Never throws. A folder that has been deleted, renamed or that this user can't read yields
    /// an empty list, which the bar renders as its "nothing here" caption — the alternative is an
    /// exception on a click, which is a worse answer to "that folder is gone".
    /// </para>
    /// </summary>
    public static List<DockItem> Read(string path, int max = MaxEntries)
    {
        var items = new List<DockItem>();
        if (max <= 0)
            return items;

        try
        {
            if (!Directory.Exists(path))
                return items;

            // Hidden and system entries are skipped for the same reason Explorer hides them by
            // default: a stack full of desktop.ini and $RECYCLE.BIN is noise, not contents. The
            // EnumerationCap Take() runs before the sort so it actually bounds how much of the
            // folder gets pulled off disk — a Take() placed after OrderBy would only bound the
            // output, not the walk that has to happen first to produce it.
            foreach (var entry in new DirectoryInfo(path)
                         .EnumerateFileSystemInfos()
                         .Take(EnumerationCap)
                         .Where(e => (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                         .OrderBy(e => e is DirectoryInfo ? 0 : 1)
                         .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                         .Take(max))
            {
                items.Add(new DockItem
                {
                    Kind = entry is DirectoryInfo
                        ? DockItemKind.Folder
                        : DockItemFactory.Classify(entry.FullName),
                    DisplayName = entry.Name,
                    Target = entry.FullName,
                });
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"FolderListing: could not read '{path}': {ex.GetType().Name}: {ex.Message}");
        }
        return items;
    }
}
