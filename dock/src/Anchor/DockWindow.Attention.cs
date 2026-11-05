using System.Numerics;
using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Anchor;

/// <summary>
/// An app asking for the user, as a Mac's Dock shows it: its icon bounces and its dot turns amber, and a dock tucked
/// away comes out to show it. What asks is a window flashing its taskbar button (see <see cref="DockManager"/>'s shell
/// hook) — a UAC prompt for an app in the background waits exactly like that, and with the taskbar's buttons hidden
/// behind the dock, the dock is the only place left for it to show. Partial of <see cref="DockWindow"/>.
/// <para>
/// The bounce is a compositor animation: it runs on the GPU's own clock and costs this thread nothing while it plays.
/// It stays inside the strip — the window is the strip's size, so an icon can't leave it — hopping toward the screen's
/// middle by the room the cell has around its icon.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    private const int BounceCount = 4;
    private static readonly TimeSpan BounceDuration = TimeSpan.FromMilliseconds(620);

    private DockItem? _attentionItem;
    private UIElement? _bouncing;

    /// <summary>
    /// Marks the icon of the app that owns <paramref name="window"/> as asking for attention, or clears the mark (0).
    /// Called again after each refresh of the open apps, as the icon may only appear a moment after the flash.
    /// </summary>
    internal void SetAttention(nint window)
    {
        DockItem? item = window == 0 ? null : ItemFor(window);
        if (ReferenceEquals(item, _attentionItem))
            return;
        if (_attentionItem is { } previous)
        {
            previous.SetNeedsAttention(false);
            StopBounce();
        }
        _attentionItem = item;
        if (item is null)
            return;
        item.SetNeedsAttention(true);
        Diag.Log($"Attention: '{item.DisplayName}' asks for the user");
        RevealNow();
        Bounce(item);
    }

    /// <summary>The icon on this dock whose app owns <paramref name="window"/> (or the window it belongs to).</summary>
    private DockItem? ItemFor(nint window)
    {
        nint owner = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOTOWNER);
        foreach (var item in Items)
        {
            if (_manager.Running.Find(item) is { } app &&
                (app.Windows.Contains(window) || (owner != 0 && app.Windows.Contains(owner))))
                return item;
        }
        return null;
    }

    private void Bounce(DockItem item)
    {
        if (DockItemAnimations.ReducedMotion)
            return; // the amber dot says it on its own
        int index = Items.IndexOf(item);
        if (index < 0 || ItemsHost.TryGetElement(index) is not Panel cell)
            return;
        var button = cell.Children.OfType<Button>().FirstOrDefault();
        if (button is null)
            return;

        StopBounce();
        _bouncing = button;
        ElementCompositionPreview.SetIsTranslationEnabled(button, true);
        var visual = ElementCompositionPreview.GetElementVisual(button);
        var compositor = visual.Compositor;

        // Away from the edge the dock sits on, by the room around the icon in its cell.
        float hop = (float)Math.Max(3, (DockMetrics.Cell - DockMetrics.Icon) / 2 + 1);
        Vector3 away = !_profile.Snapped ? new(0, -hop, 0) : _profile.Edge switch
        {
            DockEdge.Top => new(0, hop, 0),
            DockEdge.Left => new(hop, 0, 0),
            DockEdge.Right => new(-hop, 0, 0),
            _ => new(0, -hop, 0),
        };

        var rise = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.4f, 1f));
        var fall = compositor.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0f), new Vector2(0.9f, 0.5f));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, Vector3.Zero);
        animation.InsertKeyFrame(0.4f, away, rise);
        animation.InsertKeyFrame(0.8f, Vector3.Zero, fall);
        animation.InsertKeyFrame(1f, Vector3.Zero); // a beat on the ground between hops
        animation.Duration = BounceDuration;
        animation.IterationBehavior = AnimationIterationBehavior.Count;
        animation.IterationCount = BounceCount;
        visual.StartAnimation("Translation", animation);
    }

    private void StopBounce()
    {
        if (_bouncing is null)
            return;
        var visual = ElementCompositionPreview.GetElementVisual(_bouncing);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _bouncing = null;
    }
}
