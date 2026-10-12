using System.Drawing;
using TrackSwipe.Overlay;

namespace TrackSwipe.Core.Tests;

public class ThumbnailClipTests
{
    [Fact]
    public void Window_overhanging_desktop_is_cropped_without_rescaling()
    {
        Assert.True(ThumbnailClip.TryClip(new Rectangle(-100, 20, 400, 200), new Rectangle(0, 0, 1000, 600),
            new Rectangle(8, 0, 400, 200), out var destination, out var source));
        Assert.Equal(new Rectangle(0, 20, 300, 200), destination);
        Assert.Equal(new Rectangle(108, 0, 300, 200), source);
    }

    [Fact]
    public void Scaled_window_is_confined_to_its_own_desktop_tile()
    {
        Assert.True(ThumbnailClip.TryClip(new Rectangle(190, 10, 80, 40), new Rectangle(200, 10, 60, 30),
            new Rectangle(0, 0, 800, 400), out var destination, out var source));
        Assert.Equal(new Rectangle(200, 10, 60, 30), destination);
        Assert.Equal(new Rectangle(100, 0, 600, 300), source);
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(1000)]
    public void Neighbouring_desktop_window_cannot_spill_into_current_desktop(int shift)
    {
        Assert.False(ThumbnailClip.TryClip(new Rectangle(shift - 100, 0, 50, 100), new Rectangle(shift, 0, 1000, 600),
            new Rectangle(0, 0, 50, 100), out _, out _));
    }

    [Fact]
    public void Reversing_a_swipe_reveals_the_original_source_region()
    {
        var window = new Rectangle(-50, 0, 200, 100);
        var desktop = new Rectangle(0, 0, 1000, 600);
        var source = new Rectangle(8, 0, 200, 100);
        ThumbnailClip.TryClip(window, desktop, source, out _, out var firstCrop);
        window.Offset(100, 0);
        ThumbnailClip.TryClip(window, desktop, source, out _, out var revealed);
        window.Offset(-100, 0);
        ThumbnailClip.TryClip(window, desktop, source, out _, out var reversed);
        Assert.Equal(source, revealed);
        Assert.Equal(firstCrop, reversed);
    }

    [Fact]
    public void Empty_destinations_and_subpixel_source_slivers_are_hidden()
    {
        Assert.False(ThumbnailClip.TryClip(Rectangle.Empty, new Rectangle(0, 0, 100, 100),
            new Rectangle(0, 0, 10, 10), out _, out _));
        Assert.False(ThumbnailClip.TryClip(new Rectangle(0, 0, 1000, 1000), new Rectangle(0, 0, 1, 1),
            new Rectangle(0, 0, 10, 10), out _, out _));
    }
}

public class DesktopMembershipTests
{
    private static readonly Guid[] Desktops = { Guid.NewGuid(), Guid.NewGuid() };

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public void Actual_desktop_id_wins_over_visibility_and_current_desktop(int cloak)
    {
        Assert.Equal(1, DesktopMembership.Resolve(cloak, 0, Desktops, Desktops[1]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void App_hidden_or_unreadable_windows_are_excluded(int cloak)
    {
        Assert.Equal(-1, DesktopMembership.Resolve(cloak, 0, Desktops, Desktops[1]));
    }

    [Fact]
    public void Visible_popup_without_a_desktop_id_can_fall_back_to_current_desktop()
    {
        Assert.Equal(0, DesktopMembership.Resolve(0, 0, Desktops, null));
    }

    [Fact]
    public void Hidden_window_without_a_known_desktop_is_not_assigned_to_current()
    {
        Assert.Equal(-1, DesktopMembership.Resolve(2, 0, Desktops, null));
        Assert.Equal(-1, DesktopMembership.Resolve(0, 0, Desktops, Guid.NewGuid()));
    }
}

public class ThumbnailUpdateCacheTests
{
    [Fact]
    public void Hidden_motion_does_not_submit_updates_and_reentry_uses_latest_position()
    {
        var cache = new ThumbnailUpdateCache();
        var home = new Rectangle(0, 0, 200, 100);
        Assert.True(cache.Update(home, 255, null, true));
        Assert.True(cache.Update(home, 255, null, false));
        for (var frame = 0; frame < 240; frame++)
            Assert.False(cache.Update(new Rectangle(-1000 - frame, 0, 200, 100), 255, null, false));
        var reentry = new Rectangle(25, 0, 200, 100);
        Assert.True(cache.Update(reentry, 255, null, true));
        Assert.Equal(reentry, cache.Destination);
        Assert.False(cache.Update(reentry, 255, null, true));
    }

    [Fact]
    public void Cropped_to_uncropped_transition_and_fading_require_updates()
    {
        var cache = new ThumbnailUpdateCache();
        var home = new Rectangle(0, 0, 200, 100);
        Assert.True(cache.Update(home, 255, new Rectangle(20, 0, 200, 100), true));
        Assert.True(cache.Update(home, 255, null, true));
        Assert.True(cache.Update(home, 140, null, true));
        cache.Reset(); // a newly registered thumbnail must receive placement even if unchanged
        Assert.True(cache.Update(home, 140, null, true));
    }
}
