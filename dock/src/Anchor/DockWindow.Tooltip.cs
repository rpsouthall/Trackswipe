using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anchor;

/// <summary>
/// Item name tooltips, shown as soon as a cell is hovered instead of after WinUI's own (roughly
/// one-second) built-in delay — which this SDK has no public API to shorten. Each tooltip-bearing
/// element in <c>DockWindow.xaml</c> declares its <see cref="ToolTip"/> explicitly (rather than as
/// a plain string) so this can drive <see cref="ToolTip.IsOpen"/> directly; placement, theme and
/// the closing animation are still entirely WinUI's own. Partial of <see cref="DockWindow"/>.
/// </summary>
public sealed partial class DockWindow
{
    private ToolTip? _openTooltip;

    /// <summary>Wires the two standalone buttons (the item template drives its own tooltip from
    /// <see cref="TrackStripPointer"/>, since a repeater-realized cell gets no PointerEntered).</summary>
    private void HookFastTooltips()
    {
        HookButtonTooltip(AddNewButton);
        HookButtonTooltip(SettingsButton);
    }

    private void HookButtonTooltip(FrameworkElement owner)
    {
        owner.PointerEntered += (_, _) => SetFastTooltip(owner, true);
        owner.PointerExited += (_, _) => SetFastTooltip(null, false);
    }

    /// <summary>
    /// Opens <paramref name="owner"/>'s tooltip immediately (closing whichever other one was
    /// open — only one is ever showing at a time), or closes whatever is currently open when
    /// <paramref name="show"/> is false. <paramref name="owner"/> is ignored on close: the caller
    /// doesn't need to know which element it was, since there is at most one open tooltip.
    /// </summary>
    private void SetFastTooltip(FrameworkElement? owner, bool show)
    {
        if (!show)
        {
            if (_openTooltip is { } open)
            {
                open.IsOpen = false;
                _openTooltip = null;
            }
            return;
        }

        if (owner is null || ToolTipService.GetToolTip(owner) is not ToolTip tip)
            return;
        if (tip == _openTooltip)
            return; // already open — don't restart its entrance animation on every pointer tick
        if (_openTooltip is { } prior)
            prior.IsOpen = false;
        tip.IsOpen = true;
        _openTooltip = tip;
    }
}
