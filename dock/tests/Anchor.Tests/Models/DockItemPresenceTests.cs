using Anchor.Models;
using Xunit;

namespace Anchor.Tests.Models;

/// <summary>Growing into the strip and shrinking out of it, which the dock sizes itself from frame by frame.</summary>
public class DockItemPresenceTests
{
    public DockItemPresenceTests() => DockItemAnimations.ReducedMotion = false;

    [Fact]
    public void An_appearing_app_starts_at_nothing_and_grows_to_a_full_cell()
    {
        var item = DockItem.CreateOpenApp("Paint", @"C:\paint.exe", "paint");
        item.BeginAppearing();

        Assert.Equal(0, item.CellExtent);
        Assert.Equal(0, item.CellOpacity);
        Assert.True(item.IsPresenceAnimating);

        Assert.True(item.AdvancePresence(Environment.TickCount64 + 100)); // part way: still going
        Assert.InRange(item.CellExtent, 0.01, DockMetrics.Cell - 0.01);

        Assert.False(item.AdvancePresence(Environment.TickCount64 + 1000));
        Assert.Equal(DockMetrics.Cell, item.CellExtent);
        Assert.Equal(1, item.CellOpacity);
    }

    [Fact]
    public void A_closing_app_shrinks_away_and_then_has_left()
    {
        var item = DockItem.CreateOpenApp("Paint", @"C:\paint.exe", "paint");
        item.AnimatePresenceTo(0);

        Assert.True(item.IsLeaving);
        Assert.False(item.HasLeft);
        item.AdvancePresence(Environment.TickCount64 + 1000);
        Assert.True(item.HasLeft);
        Assert.Equal(0, item.CellExtent);
    }

    [Fact]
    public void An_app_reopened_while_shrinking_grows_back()
    {
        var item = DockItem.CreateOpenApp("Paint", @"C:\paint.exe", "paint");
        item.AnimatePresenceTo(0);
        item.AdvancePresence(Environment.TickCount64 + 100);
        item.AnimatePresenceTo(1);

        Assert.False(item.IsLeaving);
        item.AdvancePresence(Environment.TickCount64 + 1000);
        Assert.Equal(DockMetrics.Cell, item.CellExtent);
    }

    [Fact]
    public void With_animations_off_an_app_is_simply_there_or_gone()
    {
        DockItemAnimations.ReducedMotion = true;
        try
        {
            var item = DockItem.CreateOpenApp("Paint", @"C:\paint.exe", "paint");
            item.BeginAppearing();
            Assert.Equal(DockMetrics.Cell, item.CellExtent);
            item.AnimatePresenceTo(0);
            Assert.True(item.HasLeft);
        }
        finally
        {
            DockItemAnimations.ReducedMotion = false;
        }
    }
}
