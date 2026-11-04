using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// What a folder fly-out ("stack") lists, against a real directory — the ordering and the
/// hidden-file exclusion are the sort of rules that can only really be checked on disk.
/// </summary>
public class FolderListingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "anchor-folder-tests", Guid.NewGuid().ToString("N"));

    public FolderListingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // Clear the attributes first: Directory.Delete refuses to remove a hidden/system file.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A temp folder left behind is untidy, not a failure.
        }
    }

    private void File_(string name, FileAttributes attributes = FileAttributes.Normal)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "x");
        System.IO.File.SetAttributes(path, attributes);
    }

    private void Dir_(string name) => Directory.CreateDirectory(Path.Combine(_root, name));

    [Fact]
    public void Folders_come_first_then_files_each_alphabetical()
    {
        // Explorer's own order, so the bar lists things where the user expects to find them.
        File_("zebra.txt");
        File_("apple.txt");
        Dir_("Zulu");
        Dir_("Alpha");

        var names = FolderListing.Read(_root).Select(i => i.DisplayName).ToList();

        Assert.Equal(new[] { "Alpha", "Zulu", "apple.txt", "zebra.txt" }, names);
    }

    [Fact]
    public void Entries_are_classified_and_carry_their_full_path()
    {
        Dir_("Sub");
        File_("readme.txt");

        var items = FolderListing.Read(_root);

        var folder = items.Single(i => i.DisplayName == "Sub");
        Assert.Equal(DockItemKind.Folder, folder.Kind);
        Assert.Equal(Path.Combine(_root, "Sub"), folder.Target);

        var file = items.Single(i => i.DisplayName == "readme.txt");
        Assert.Equal(DockItemKind.File, file.Kind);
        Assert.Equal(Path.Combine(_root, "readme.txt"), file.Target);
    }

    [Fact]
    public void An_exe_is_listed_as_an_application_not_a_file()
    {
        // Classification runs through DockItemFactory, so pinning one out of a stack gets the
        // right kind — and with it the running-app dot and click-to-focus.
        File_("tool.exe");

        Assert.Equal(DockItemKind.Application, Assert.Single(FolderListing.Read(_root)).Kind);
    }

    [Fact]
    public void Hidden_and_system_entries_are_skipped()
    {
        // A stack full of desktop.ini and thumbs.db is noise, not contents.
        File_("visible.txt");
        File_("desktop.ini", FileAttributes.Hidden);
        File_("swap.sys", FileAttributes.System);

        Assert.Equal("visible.txt", Assert.Single(FolderListing.Read(_root)).DisplayName);
    }

    [Fact]
    public void The_listing_is_capped()
    {
        for (int i = 0; i < 20; i++)
            File_($"file{i:00}.txt");

        Assert.Equal(5, FolderListing.Read(_root, max: 5).Count);
        Assert.Empty(FolderListing.Read(_root, max: 0));
    }

    [Fact]
    public void An_empty_folder_lists_nothing()
    {
        Assert.Empty(FolderListing.Read(_root));
    }

    [Fact]
    public void A_folder_that_is_gone_yields_an_empty_list_rather_than_throwing()
    {
        // A pinned folder can be deleted or renamed at any time, and the click that opens its
        // stack must not take the dock down with it.
        Assert.Empty(FolderListing.Read(Path.Combine(_root, "no-such-folder")));
        Assert.Empty(FolderListing.Read(string.Empty));
        Assert.Empty(FolderListing.Read(@"Z:\definitely\not\here"));
    }

    [Fact]
    public void A_file_path_is_not_a_folder()
    {
        File_("thing.txt");

        Assert.Empty(FolderListing.Read(Path.Combine(_root, "thing.txt")));
    }
}
