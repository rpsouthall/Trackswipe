using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// The command line built when a file is dropped onto an app icon. Only the string-building half
/// of <see cref="Launcher"/> is exercised here — everything else in it starts a process, which a
/// unit test has no business doing — but this is the half that quietly breaks: a path with a space
/// arriving as two arguments looks like the app is broken, not Anchor.
/// </summary>
public class LauncherTests
{
    [Fact]
    public void A_single_path_is_quoted()
    {
        Assert.Equal("\"C:\\notes.txt\"", Launcher.BuildArguments(null, new[] { @"C:\notes.txt" }));
    }

    [Fact]
    public void A_path_with_spaces_stays_one_argument()
    {
        // The whole reason for quoting: unquoted, this arrives as "C:\Program", "Files\a", "b.txt".
        Assert.Equal(
            "\"C:\\Program Files\\a b.txt\"",
            Launcher.BuildArguments(null, new[] { @"C:\Program Files\a b.txt" }));
    }

    [Fact]
    public void Several_paths_go_to_one_process_space_separated()
    {
        // One process with every file, the way the shell does it — not one window per file.
        Assert.Equal(
            "\"C:\\a.txt\" \"C:\\b.txt\"",
            Launcher.BuildArguments(null, new[] { @"C:\a.txt", @"C:\b.txt" }));
    }

    [Fact]
    public void The_items_own_arguments_come_first()
    {
        // A pinned "app with switches" keeps them when something is dropped on it.
        Assert.Equal(
            "--new-window \"C:\\a.txt\"",
            Launcher.BuildArguments("--new-window", new[] { @"C:\a.txt" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_configured_arguments_means_no_leading_space(string? configured)
    {
        Assert.Equal("\"C:\\a.txt\"", Launcher.BuildArguments(configured, new[] { @"C:\a.txt" }));
    }

    [Fact]
    public void Configured_arguments_are_trimmed()
    {
        Assert.Equal("-x \"C:\\a.txt\"", Launcher.BuildArguments("  -x  ", new[] { @"C:\a.txt" }));
    }

    [Fact]
    public void With_no_paths_only_the_configured_arguments_remain()
    {
        Assert.Equal("-x", Launcher.BuildArguments("-x", Array.Empty<string>()));
        Assert.Equal(string.Empty, Launcher.BuildArguments(null, Array.Empty<string>()));
    }

    [Fact]
    public void Blank_paths_are_dropped_rather_than_becoming_empty_arguments()
    {
        // A drag can hand over a storage item with no filesystem path at all.
        Assert.Equal(
            "\"C:\\a.txt\"",
            Launcher.BuildArguments(null, new[] { "", "   ", @"C:\a.txt" }));
    }

    [Fact]
    public void A_quote_inside_a_path_cannot_split_the_argument()
    {
        // Windows paths can't contain a double quote, so this shouldn't arise — but if one ever
        // does, it must not be able to close its own quoting and turn into extra arguments.
        Assert.Equal(
            "\"C:\\weird name.txt\"",
            Launcher.BuildArguments(null, new[] { "C:\\weird\" name.txt" }));
    }

    [Fact]
    public void An_app_path_offers_open_file_location()
    {
        Assert.True(Launcher.SupportsFileLocation(new DockItem
        {
            Kind = DockItemKind.Application,
            Target = @"C:\Windows\explorer.exe",
        }));
    }

    [Fact]
    public void A_start_menu_app_does_not_offer_open_file_location()
    {
        // A Store app is an entry in a virtual folder, not a file, so there is no folder to
        // reveal it in — and the menu shouldn't offer an entry that could only do nothing.
        Assert.False(Launcher.SupportsFileLocation(new DockItem
        {
            Kind = DockItemKind.Application,
            Target = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        }));
    }
}
