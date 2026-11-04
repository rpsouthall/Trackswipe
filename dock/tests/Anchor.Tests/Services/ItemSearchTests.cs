using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// Quick-launch matching. This is the behaviour a user judges the search box by — whether typing
/// two letters puts the thing they meant at the top — and it used to live inside a WinUI Window,
/// where no test could reach it.
/// </summary>
public class ItemSearchTests
{
    private static DockItem App(string name, string target = "") =>
        new() { Kind = DockItemKind.Application, DisplayName = name, Target = target };

    private static readonly DockItem Code = App("VS Code", @"C:\Program Files\Microsoft VS Code\Code.exe");
    private static readonly DockItem Notepad = App("Notepad", @"C:\Windows\System32\notepad.exe");
    private static readonly DockItem Notes = App("Notes", @"C:\Users\me\notes.txt");
    private static readonly DockItem Buried = App("Readme", @"C:\vs\readme.md");

    private static readonly DockItem[] All = { Code, Notepad, Notes, Buried };

    // ---- What is searchable ------------------------------------------------

    [Fact]
    public void Separators_groups_and_hidden_items_are_not_searchable()
    {
        // A separator has nothing to open, a group would want a fly-out that has nowhere to appear
        // when the dock is hidden, and a hidden item was deliberately taken off the strip.
        Assert.False(ItemSearch.IsSearchable(new DockItem { Kind = DockItemKind.Separator }));
        Assert.False(ItemSearch.IsSearchable(new DockItem { Kind = DockItemKind.Group }));
        Assert.False(ItemSearch.IsSearchable(new DockItem { Hidden = true }));

        Assert.True(ItemSearch.IsSearchable(App("Notepad")));
        Assert.True(ItemSearch.IsSearchable(new DockItem { Kind = DockItemKind.WebLink }));
        Assert.True(ItemSearch.IsSearchable(new DockItem { Kind = DockItemKind.Folder }));
    }

    [Fact]
    public void Filter_excludes_the_items_that_are_not_searchable()
    {
        var items = new[]
        {
            App("Notepad"),
            new DockItem { Kind = DockItemKind.Separator, DisplayName = "Notepad" },
            new DockItem { Kind = DockItemKind.Group, DisplayName = "Notepad" },
            new DockItem { DisplayName = "Notepad", Hidden = true },
        };

        Assert.Single(ItemSearch.Filter(items, "Notepad"));
    }

    // ---- Ranking -----------------------------------------------------------

    [Fact]
    public void A_name_prefix_beats_a_name_substring_beats_the_target()
    {
        Assert.Equal(ItemSearch.RankNameStartsWith, ItemSearch.Rank(Notepad, "Note"));
        Assert.Equal(ItemSearch.RankNameContains, ItemSearch.Rank(Code, "Code"));
        Assert.Equal(ItemSearch.RankTargetContains, ItemSearch.Rank(Notepad, "System32"));
        Assert.Equal(ItemSearch.NoMatch, ItemSearch.Rank(Notepad, "photoshop"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal(ItemSearch.RankNameStartsWith, ItemSearch.Rank(Notepad, "notepad"));
        Assert.Equal(ItemSearch.RankNameStartsWith, ItemSearch.Rank(Notepad, "NOTEPAD"));
        Assert.Equal(ItemSearch.RankNameContains, ItemSearch.Rank(Code, "code"));
    }

    [Fact]
    public void A_name_match_outranks_an_item_that_only_matches_by_path()
    {
        // The case this ranking exists for: "vs" is in VS Code's name and in the *path* of a file
        // living under C:\vs\. The name has to win.
        var results = ItemSearch.Filter(All, "vs").ToList();

        Assert.Equal(Code, results[0]);
        Assert.Contains(Buried, results);
        Assert.True(results.IndexOf(Code) < results.IndexOf(Buried));
    }

    [Fact]
    public void Ties_break_alphabetically_so_the_list_is_stable()
    {
        // "Note" prefixes both, so only the tiebreak decides — and it must not depend on which
        // dock the items happen to sit on.
        var forwards = ItemSearch.Filter(new[] { Notes, Notepad }, "Note");
        var backwards = ItemSearch.Filter(new[] { Notepad, Notes }, "Note");

        Assert.Equal(new[] { Notepad, Notes }, forwards);
        Assert.Equal(forwards, backwards);
    }

    // ---- The empty query and the limit -------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_query_lists_the_first_items_in_dock_order(string? query)
    {
        // The card is useful before a key is typed, and "in the order they sit on the dock" is the
        // order the user already knows.
        var results = ItemSearch.Filter(All, query);

        Assert.Equal(All, results);
    }

    [Fact]
    public void Whitespace_around_a_query_is_ignored()
    {
        Assert.Equal(new[] { Notepad }, ItemSearch.Filter(All, "  Notepad  "));
    }

    [Fact]
    public void No_more_than_the_limit_is_returned()
    {
        var many = Enumerable.Range(0, 50).Select(i => App($"Thing {i:00}")).ToList();

        Assert.Equal(3, ItemSearch.Filter(many, "Thing", limit: 3).Count);
        Assert.Equal(3, ItemSearch.Filter(many, string.Empty, limit: 3).Count);
        Assert.Equal(ItemSearch.DefaultLimit, ItemSearch.Filter(many, "Thing").Count);
    }

    [Fact]
    public void A_query_nothing_matches_returns_nothing()
    {
        Assert.Empty(ItemSearch.Filter(All, "photoshop"));
    }
}
