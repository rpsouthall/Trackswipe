using Anchor.Models;

namespace Anchor.Services;

/// <summary>
/// The matching behind quick-launch search: which pinned items a query finds, and in what order.
/// <para>
/// It searches the items you pinned rather than the file system on purpose. Windows already has a
/// search that covers the disk; what it cannot do is "the thing I pinned, by the name I gave it",
/// which is the only thing Anchor knows better than the shell does.
/// </para>
/// <para>
/// Separate from <c>SearchWindow</c> so the ranking is testable: a WinUI <c>Window</c> can't be
/// constructed in a unit test, and "does typing 'vs' find VS Code before a file that merely lives
/// under C:\vs\" is exactly the kind of rule that rots silently.
/// </para>
/// </summary>
public static class ItemSearch
{
    /// <summary>How many matches to show. Enough to choose from, few enough that the card stays a
    /// card and the right answer is still a couple of arrow presses away.</summary>
    public const int DefaultLimit = 8;

    /// <summary>Ranks, in the order <see cref="Rank"/> returns them.</summary>
    public const int RankNameStartsWith = 0;
    public const int RankNameContains = 1;
    public const int RankTargetContains = 2;

    /// <summary>Returned by <see cref="Rank"/> for an item the query does not match at all.</summary>
    public const int NoMatch = int.MaxValue;

    /// <summary>
    /// True for an item quick-launch can actually open. Separators have no target, groups have
    /// nothing to launch (a fly-out would have nowhere to appear when the dock is hidden), and a
    /// hidden item was deliberately taken off the strip.
    /// </summary>
    public static bool IsSearchable(DockItem item) =>
        !item.IsSeparator && !item.IsGroup && !item.Hidden;

    /// <summary>
    /// The best <paramref name="limit"/> matches for <paramref name="query"/>, best first.
    /// <para>
    /// An empty query returns the first searchable items in dock order, so the card is useful
    /// before a single key is typed. Anything else ranks by <em>where</em> the query matched — a
    /// name prefix beats a name substring beats the target — and ties break alphabetically so the
    /// list is stable rather than dependent on which dock an item happens to sit on.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DockItem> Filter(
        IEnumerable<DockItem> items, string? query, int limit = DefaultLimit)
    {
        var candidates = items.Where(IsSearchable);
        query = query?.Trim() ?? string.Empty;

        if (query.Length == 0)
            return candidates.Take(limit).ToList();

        return candidates
            .Select(item => (item, rank: Rank(item, query)))
            .Where(x => x.rank != NoMatch)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>
    /// How well <paramref name="item"/> matches <paramref name="query"/> — lower is better,
    /// <see cref="NoMatch"/> means it doesn't. Case- and culture-insensitive, because the user is
    /// typing a name they chose, not a identifier.
    /// </summary>
    public static int Rank(DockItem item, string query)
    {
        if (string.IsNullOrEmpty(query))
            return RankNameStartsWith;

        var name = item.DisplayName ?? string.Empty;
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            return RankNameStartsWith;
        if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return RankNameContains;
        if ((item.Target ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return RankTargetContains;
        return NoMatch;
    }
}
