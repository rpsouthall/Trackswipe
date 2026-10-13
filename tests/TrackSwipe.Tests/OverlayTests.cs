using System.Drawing;
using TrackSwipe.Overlay;

namespace TrackSwipe.Tests;

public class SpritePlacementTests
{
    private static readonly Size Monitor = new(1000, 600);

    [Fact]
    public void Window_overhanging_desktop_is_clipped_without_rescaling()
    {
        var state = new SpriteState();
        state.Place(new Rectangle(-100, 20, 400, 200), 255, new Rectangle(0, 0, 1000, 600), Monitor);
        Assert.True(state.Visible);
        Assert.Equal(new Size(400, 200), state.Size);
        Assert.Equal(new Point(-100, 20), state.Location);
        Assert.Equal(new RectangleF(100, 0, 300, 200), state.Clip);
    }

    [Fact]
    public void Scaled_window_is_confined_to_its_own_desktop_tile()
    {
        var state = new SpriteState();
        state.Place(new Rectangle(190, 10, 80, 40), 255, new Rectangle(200, 10, 60, 30), Monitor);
        Assert.Equal(new Size(80, 40), state.Size);
        Assert.Equal(new RectangleF(10, 0, 60, 30), state.Clip);
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(1000)]
    public void Neighbouring_desktop_window_cannot_spill_into_current_desktop(int shift)
    {
        var state = new SpriteState();
        state.Place(new Rectangle(shift - 100, 0, 50, 100), 255, new Rectangle(shift, 0, 1000, 600), Monitor);
        Assert.False(state.Visible);
    }

    [Fact]
    public void Window_partly_off_the_monitor_is_clipped_to_it()
    {
        var state = new SpriteState();
        state.Place(new Rectangle(900, 500, 200, 200), 255, null, Monitor);
        Assert.Equal(new RectangleF(0, 0, 100, 100), state.Clip);
    }

    [Fact]
    public void Reversing_a_swipe_restores_the_original_clip()
    {
        var state = new SpriteState();
        var desktop = new Rectangle(0, 0, 1000, 600);
        state.Place(new Rectangle(-50, 0, 200, 100), 255, desktop, Monitor);
        var first = state.Clip;
        state.Place(new Rectangle(50, 0, 200, 100), 255, desktop, Monitor);
        Assert.Equal(new RectangleF(0, 0, 200, 100), state.Clip);
        state.Place(new Rectangle(-50, 0, 200, 100), 255, desktop, Monitor);
        Assert.Equal(first, state.Clip);
    }

    [Fact]
    public void Empty_destinations_and_zero_opacity_are_hidden()
    {
        var state = new SpriteState();
        Assert.Equal(SpriteChanges.None, state.Place(Rectangle.Empty, 255, null, Monitor));
        Assert.Equal(SpriteChanges.None, state.Place(new Rectangle(0, 0, 100, 100), 0, null, Monitor));
        Assert.False(state.Visible);
    }

    [Fact]
    public void A_pure_move_needs_no_DWM_resize()
    {
        var state = new SpriteState();
        state.Place(new Rectangle(0, 0, 300, 200), 255, null, Monitor);
        var changes = state.Place(new Rectangle(40, 0, 300, 200), 255, null, Monitor);
        Assert.Equal(SpriteChanges.Location, changes);
    }

    [Fact]
    public void First_placement_sends_everything_that_differs_from_a_new_thumbnail()
    {
        var state = new SpriteState();
        var changes = state.Place(new Rectangle(10, 20, 300, 200), 128, null, Monitor);
        Assert.Equal(SpriteChanges.Size | SpriteChanges.Location | SpriteChanges.Clip | SpriteChanges.Opacity | SpriteChanges.Visibility, changes);
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

public class SwitchLandingTests
{
    private static readonly IntPtr Arriving = new(1), Leaving = new(2);

    private static SwitchLanding Landing(bool? arriving, bool? leaving, bool idChanged = true) => new(
        arriving is null ? IntPtr.Zero : Arriving, leaving is null ? IntPtr.Zero : Leaving,
        window => window == Arriving ? arriving : window == Leaving ? leaving : null, () => idChanged, requested: 10);

    [Fact]
    public void Target_window_decides_when_there_is_one()
    {
        Assert.False(Landing(arriving: false, leaving: false).HasLanded(10.01));
        Assert.True(Landing(arriving: true, leaving: true).HasLanded(10.01));
    }

    [Fact]
    public void Empty_target_desktop_lands_when_the_old_desktop_is_cloaked()
    {
        Assert.False(Landing(arriving: null, leaving: true).HasLanded(10.01));
        Assert.True(Landing(arriving: null, leaving: false).HasLanded(10.01));
    }

    [Fact]
    public void Desktop_id_alone_counts_only_after_a_delay()
    {
        // The id changes as soon as the switch is requested, before the screen shows it.
        Assert.False(Landing(arriving: null, leaving: null).HasLanded(10.05));
        Assert.True(Landing(arriving: null, leaving: null).HasLanded(10 + SwitchLanding.UnwatchedDelay));
        Assert.False(Landing(arriving: null, leaving: null, idChanged: false).HasLanded(11));
    }

    [Fact]
    public void A_window_shown_on_every_desktop_cannot_hold_the_overlay()
    {
        Assert.True(Landing(arriving: null, leaving: true).HasLanded(10 + SwitchLanding.UnwatchedDelay));
    }
}

public class SpriteChangeTrackingTests
{
    private static readonly Size Monitor = new(1000, 600);

    [Fact]
    public void Hidden_motion_does_not_submit_updates_and_reentry_uses_latest_position()
    {
        var state = new SpriteState();
        var home = new Rectangle(0, 0, 200, 100);
        Assert.NotEqual(SpriteChanges.None, state.Place(home, 255, null, Monitor));
        Assert.Equal(SpriteChanges.Visibility, state.Place(new Rectangle(-1000, 0, 200, 100), 255, null, Monitor));
        for (var frame = 0; frame < 240; frame++)
            Assert.Equal(SpriteChanges.None, state.Place(new Rectangle(-1001 - frame, 0, 200, 100), 255, null, Monitor));
        var reentry = new Rectangle(25, 0, 200, 100);
        Assert.Equal(SpriteChanges.Location | SpriteChanges.Visibility, state.Place(reentry, 255, null, Monitor));
        Assert.Equal(reentry.Location, state.Location);
        Assert.Equal(SpriteChanges.None, state.Place(reentry, 255, null, Monitor));
    }

    [Fact]
    public void Clipped_to_unclipped_transition_and_fading_require_updates()
    {
        var state = new SpriteState();
        var home = new Rectangle(0, 0, 200, 100);
        state.Place(home, 255, new Rectangle(20, 0, 1000, 600), Monitor);
        Assert.Equal(SpriteChanges.Clip, state.Place(home, 255, null, Monitor));
        Assert.Equal(SpriteChanges.Opacity, state.Place(home, 140, null, Monitor));
    }

    [Fact]
    public void Resizing_is_the_only_change_that_reaches_DWM()
    {
        var state = new SpriteState();
        state.Place(new Rectangle(0, 0, 200, 100), 255, null, Monitor);
        var changes = state.Place(new Rectangle(0, 0, 180, 90), 255, null, Monitor);
        Assert.True(changes.HasFlag(SpriteChanges.Size));
        Assert.Equal(new Size(180, 90), state.Size);
    }
}

public class AnimatedThumbnailTests
{
    [Fact]
    public void Fractional_motion_is_preserved_in_position_size_and_clip()
    {
        var state = new SpriteState();
        var rect = new RectangleF(-10.25f, 20.5f, 300.75f, 200.25f);
        state.Place(rect, 255, new RectangleF(0, 0, 1000, 600), new Size(1000, 600));
        Assert.Equal(rect.Location, state.Offset);
        Assert.Equal(rect.Size, state.DrawSize);
        Assert.Equal(new RectangleF(10.25f, 0, 290.5f, 200.25f), state.Clip);
        var changes = state.Place(new RectangleF(-10.0f, 20.5f, 300.75f, 200.25f), 255, null, new Size(1000, 600));
        Assert.True(changes.HasFlag(SpriteChanges.Location));
        Assert.False(changes.HasFlag(SpriteChanges.Size));
    }

    [Fact]
    public void Overview_raster_keeps_full_source_resolution_for_click_close()
    {
        var source = new Size(1800, 1200);
        var raster = ThumbnailSizing.RasterSize(source);
        Assert.Equal(source, raster);
        // All display sizes are GPU scales of this raster; entering/leaving the overview
        // must not switch DWM between a 600x400 raster and the 1800x1200 source.
        Assert.Equal(new SizeF(600, 400), new SizeF(raster.Width / 3f, raster.Height / 3f));
    }

    [Fact]
    public void Enlarged_restore_destination_scales_a_valid_source_raster()
    {
        var source = new Size(800, 600);
        Assert.Equal(source, ThumbnailSizing.RasterSize(source));
        Assert.Equal(new Size(1600, 1200), ThumbnailSizing.RasterSize(new Size(1600, 1200)));
        Assert.Equal(new Size(1, 1), ThumbnailSizing.RasterSize(Size.Empty));
    }
}
