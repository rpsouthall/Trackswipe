using TrackSwipe.Core;

namespace TrackSwipe.Overlay;

/// <summary>
/// The GNOME-style overview: each desktop is a card (the whole screen scaled down, wallpaper and all, with rounded
/// corners) with its windows spread inside, the neighbouring desktops' cards showing at the sides.
/// </summary>
internal sealed partial class MissionControl
{
    /// <summary>A click on a neighbouring desktop's card is sliding the cards along: which way, and until when.</summary>
    private (int Step, double Until)? _autoPan;

    /// <summary>
    /// One desktop's card, <paramref name="layer"/> places along from the current one. Everything in it travels as
    /// one picture: at openness 0 it is that desktop at full size (the current one exactly as it is on screen, the
    /// others whole screens away), fully open it is the card.
    /// </summary>
    private void BuildCard(Plan plan, DesktopScene scene, OverviewCards layout, int layer)
    {
        var surface = plan.Surface;
        var screen = new Rectangle(Point.Empty, surface.Monitor.Size);
        var fromShift = (int)Math.Round(layer * layout.FarPitch);
        var toShift = (int)Math.Round(layer * layout.Pitch);
        var viewport = screen;
        viewport.Offset(fromShift, 0);
        var card = Round(layout.Card);
        card.Offset(toShift, 0);
        var first = plan.Parts.Count;

        foreach (var wallpaper in scene.Wallpapers)
        {
            if (!wallpaper.Bounds.IntersectsWith(surface.Monitor) || Acquire(surface, wallpaper, $"card:{layer}") is not { } sprite) continue;
            var from = sprite.Home;
            from.Offset(fromShift, 0);
            var to = Round(layout.Map(ToLayout(sprite.Home)));
            to.Offset(toShift, 0);
            plan.Parts.Add(new Part(surface, sprite, from, to, 255, 255)
            {
                Pans = true, Rigid = true, Slot = $"card:{layer}", ClipFrom = viewport, ClipTo = card,
            });
        }

        BuildSpread(plan, scene, scene.Current + layer, fromShift, toShift, Round(layout.SpreadArea()), (layout, layer), isCurrent: layer == 0);

        // Rounded corners: a mask of the grey behind the cards over each, growing from nothing as the card shrinks.
        var radius = (int)Math.Round(layout.Radius);
        for (var corner = 0; corner < 4; corner++)
        {
            if (Acquire(surface, _swatches.CornerMask(radius, corner), $"corner:{layer}:{corner}") is not { } sprite) continue;
            bool left = corner is 0 or 2, top = corner is 0 or 1;
            var from = new Rectangle(left ? viewport.Left : viewport.Right, top ? viewport.Top : viewport.Bottom, 0, 0);
            var to = new Rectangle(left ? card.Left : card.Right - radius, top ? card.Top : card.Bottom - radius, radius, radius);
            plan.Parts.Add(new Part(surface, sprite, from, to, 255, 255) { Pans = true, Rigid = true, Slot = $"corner:{layer}:{corner}" });
        }

        // Thumbnails reused from the last build keep their old place in the stack, and a card only reads right
        // bottom to top: wallpaper, windows, corners. Cards never overlap each other, so that's all that matters.
        // Left alone when they're in order already (always, the first time): nothing is taken out of the tree.
        var stack = new List<Sprite>();
        for (var i = first; i < plan.Parts.Count; i++)
        {
            if (plan.Parts[i].Backing is { } backing) stack.Add(backing);
            stack.Add(plan.Parts[i].Sprite);
        }
        if (!surface.IsStacked(stack)) surface.Restack(stack);
    }

    /// <summary>
    /// The card layout: keeps the taskbar above every card. A card starts as the whole screen, the taskbar's place
    /// included, and one built or reordered after the taskbar's thumbnail would otherwise cover it until it had shrunk.
    /// </summary>
    private static void RaiseTaskbar(Plan plan)
    {
        if (plan.Cards is null) return;
        var taskbars = plan.Parts.Where(part => part.Slot == "taskbar").Select(part => part.Sprite).ToList();
        if (taskbars.Count == 0) return;
        var surface = plan.Surface;
        var highestCard = -1;
        foreach (var part in plan.Parts)
        {
            if (!part.Pans) continue;
            highestCard = Math.Max(highestCard, surface.StackIndex(part.Sprite));
            if (part.Backing is { } backing) highestCard = Math.Max(highestCard, surface.StackIndex(backing));
        }
        if (taskbars.Any(taskbar => surface.StackIndex(taskbar) < highestCard)) surface.Restack(taskbars);
    }

    /// <summary>
    /// The card layout draws the ring on the current desktop's preview, over its edge, so it goes above the previews
    /// whenever they are restacked (they move in and out of a group of their own).
    /// </summary>
    private static void RaiseRing(Plan plan)
    {
        if (plan.Cards is null) return;
        var ring = plan.Parts.Where(part => part.Slot == "ring").Select(part => part.Sprite).ToList();
        if (ring.Count > 0) plan.Surface.Restack(ring);
    }

    /// <summary>
    /// Slides the cards <paramref name="step"/> places along, as a sideways swipe does: a click on a neighbouring
    /// desktop's card brings it to the middle. The overview stays open; the real switch waits until it closes.
    /// </summary>
    private void PanTo(int step)
    {
        if (step is not (1 or -1) || !CanPan(step) || !IsOpen) return;
        if (DeferUntilAwake(() => PanTo(step))) return; // sharp thumbnails: wake the scaled copies a frame first
        BeginPan();
        var motion = SettleMotion.Create(SpringPreset.Snappy, 0, 0, step);
        var now = Clock.Now;
        Settle(motion, (long)(now * System.Diagnostics.Stopwatch.Frequency), _ => 1, pan: true);
        _autoPan = (step, now + motion.Duration);
        FramesNeeded?.Invoke();
    }

    /// <summary>True while a click's pan is still sliding.</summary>
    private bool TickAutoPan(double now)
    {
        if (_autoPan is not { } pan) return false;
        if (now < pan.Until) return true;
        _autoPan = null;
        if (_state != State.Open) return false;
        CommitPan(pan.Step);
        EndPan();
        RefreshHover();
        return false;
    }

    /// <summary>For diagnostics: where the current desktop's card and its neighbours are, on each monitor.</summary>
    public string DescribeCards() => string.Join("; ", _plans.Select(plan => plan.Cards is not { } cards
        ? "no cards"
        : $"card {Round(cards.Card)} scale {cards.Scale:0.###}, pitch {cards.Pitch:0}, radius {cards.Radius:0}; " +
          string.Join(" ", plan.Parts.Where(part => part.Slot.StartsWith("card:")).Select(part =>
          {
              var shown = Rectangle.Round(FrameFor(plan, part, _openness, _pan, 0).Viewport ?? RectangleF.Empty);
              return $"[{part.Slot} at {shown.X},{shown.Y} {shown.Width}x{shown.Height}]";
          }))));

    /// <summary>
    /// For diagnostics: the colours on screen at the places that tell whether the cards are drawn right, on the
    /// monitor with the desktop previews. <paramref name="pixel"/> reads one screen pixel.
    /// </summary>
    public string SampleCards(Func<Point, Color> pixel)
    {
        if (_plans.FirstOrDefault(plan => plan.Cards is not null && plan.Surface.IsPrimary) is not { } plan || plan.Cards is not { } cards) return "no cards";
        var monitor = plan.Surface.Monitor;
        var card = Round(cards.Card);
        string At(string name, double x, double y)
        {
            if (x < 0 || y < 0 || x >= monitor.Width || y >= monitor.Height) return $"{name} off screen";
            var colour = pixel(new Point(monitor.X + (int)x, monitor.Y + (int)y));
            return $"{name} {colour.R},{colour.G},{colour.B}";
        }
        var gap = cards.Pitch - cards.Card.Width;
        return string.Join("; ",
            At("card centre", cards.Card.CenterX, cards.Card.CenterY),
            At("card top edge, mid", cards.Card.CenterX, card.Top + 3),
            At("corner tip (backdrop)", card.Left + 1, card.Top + 1),
            At("just inside the corner's arc", card.Left + cards.Radius * 0.45, card.Top + cards.Radius * 0.45),
            At("gap right of the card (backdrop)", card.Right + gap / 2, cards.Card.CenterY),
            At("above the card (backdrop)", cards.Card.CenterX, card.Top - 5),
            At("next card's edge", card.Right + gap + 6, cards.Card.CenterY),
            At("previous card's edge", card.Left - gap - 6, cards.Card.CenterY));
    }

    /// <summary>For diagnostics: acts as if the neighbouring desktop's card (<paramref name="step"/> is ±1) were clicked.</summary>
    public bool ClickCard(int step)
    {
        if (!IsOpen || _plans.All(plan => plan.Cards is null) || !CanPan(step)) return false;
        PanTo(step);
        return true;
    }
}
