using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

public class DockItemFactoryTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "AnchorTests_" + Guid.NewGuid().ToString("N"));

    public DockItemFactoryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Classify_recognizes_an_existing_directory_as_a_folder()
    {
        Assert.Equal(DockItemKind.Folder, DockItemFactory.Classify(_tempDir));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?query=1")]
    public void Classify_recognizes_http_and_https_urls_as_web_links(string url)
    {
        Assert.Equal(DockItemKind.WebLink, DockItemFactory.Classify(url));
    }

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData("notepad.exe")]
    [InlineData(@"C:\Tools\run.bat")]
    [InlineData("script.cmd")]
    [InlineData("legacy.com")]
    [InlineData(@"C:\Users\Public\Desktop\App.lnk")]
    public void Classify_recognizes_launchable_extensions_as_applications(string target)
    {
        Assert.Equal(DockItemKind.Application, DockItemFactory.Classify(target));
    }

    [Theory]
    [InlineData(@"C:\Users\Test\notes.txt")]
    [InlineData("readme")]
    public void Classify_falls_back_to_file_for_anything_else(string target)
    {
        Assert.Equal(DockItemKind.File, DockItemFactory.Classify(target));
    }

    [Fact]
    public void SuggestName_uses_the_host_for_a_web_link()
    {
        Assert.Equal("github.com",
            DockItemFactory.SuggestName("https://github.com/microsoft/WinUI-Gallery"));
    }

    [Fact]
    public void SuggestName_uses_the_filename_without_extension_for_an_app_path()
    {
        Assert.Equal("explorer", DockItemFactory.SuggestName(@"C:\Windows\explorer.exe"));
    }

    [Fact]
    public void SuggestName_trims_a_trailing_separator_for_a_folder_path()
    {
        Assert.Equal("Documents", DockItemFactory.SuggestName(@"C:\Users\Test\Documents\"));
    }

    [Fact]
    public void SuggestName_strips_the_extension_from_a_bare_filename()
    {
        Assert.Equal("readme", DockItemFactory.SuggestName("readme.txt"));
    }

    // ---- Dropped links -----------------------------------------------------

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("  http://example.com  ", "http://example.com/")]
    [InlineData("example.com", "https://example.com/")]
    public void TryNormalizeWebUrl_accepts_web_addresses_and_adds_a_missing_scheme(string text, string expected)
    {
        Assert.Equal(expected, DockItemFactory.TryNormalizeWebUrl(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some prose.")]
    [InlineData("hello")]
    [InlineData("ftp://example.com")]
    [InlineData(@"C:\Windows\explorer.exe")]
    public void TryNormalizeWebUrl_rejects_anything_that_is_not_a_web_address(string? text)
    {
        Assert.Null(DockItemFactory.TryNormalizeWebUrl(text));
    }

    // ---- Start-menu apps ---------------------------------------------------
    //
    // A Store app dropped from the Start menu has no path at all, only an AppUserModelID (see
    // Services/ShellDrop.cs). Those IDs are full of dots, so anything that reads the tail of one
    // as a file extension classifies "…Calculator_8wekyb3d8bbwe!App" as a plain file — an item
    // the dock would show with a document icon and open with a text editor.

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:AppsFolder\Chrome")]
    [InlineData(@"SHELL:APPSFOLDER\Chrome")]
    public void Classify_recognizes_a_start_menu_app_as_an_application(string target)
    {
        Assert.Equal(DockItemKind.Application, DockItemFactory.Classify(target));
    }

    [Fact]
    public void SuggestName_of_a_start_menu_app_is_its_id_without_the_folder()
    {
        // Only a fallback — a drop names the item from the shell ("Calculator") — but it must not
        // come back as "Microsoft" (the first dotted segment) or as the whole shell command.
        Assert.Equal(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            DockItemFactory.SuggestName(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\Chrome", true)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData("https://example.com", false)]
    [InlineData(null, false)]
    public void IsAppsFolderTarget_only_matches_the_shell_command(string? target, bool expected)
    {
        Assert.Equal(expected, DockItemFactory.IsAppsFolderTarget(target));
    }
}
