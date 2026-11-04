using Anchor.Controls;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anchor;

/// <summary>
/// Per-item shortcuts: a system-wide combination that launches (or focuses) one pinned item
/// without the dock having to be visible at all. Partial of <see cref="DockWindow"/>.
/// <para>
/// The combination lives on the item (<see cref="DockItem.Hotkey"/>) and is registered by
/// <see cref="DockManager.ApplyItemHotkeys"/>, which owns every registration Anchor holds. This
/// file is only the UI for assigning one.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>
    /// The <c>Shortcut ▸</c> submenu for an item: what it currently has, and a way to assign a new
    /// one. The caller builds it only while the per-item master switch is on — a combination
    /// assigned with it off would quietly never fire — and never for a separator or a group, which
    /// have nothing to launch.
    /// </summary>
    private MenuFlyoutSubItem BuildItemHotkeyMenu(FrameworkElement target, DockItem item)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get("Menu.Shortcut") };

        var assign = new MenuFlyoutItem
        {
            Text = HotkeyGesture.TryParse(item.Hotkey, out var current)
                ? Loc.Format("Menu.ShortcutAssigned", current.ToString())
                : Loc.Get("Menu.ShortcutAssign"),
        };
        assign.Click += (_, _) => ShowItemHotkeyFlyout(target, item);
        sub.Items.Add(assign);

        if (!string.IsNullOrWhiteSpace(item.Hotkey))
        {
            var clear = new MenuFlyoutItem { Text = Loc.Get("Menu.ShortcutClear") };
            clear.Click += (_, _) => _manager.SetItemHotkey(item, null);
            sub.Items.Add(clear);
        }

        return sub;
    }

    /// <summary>
    /// The capture flyout: press a combination and it is assigned. Anchored on the item's own cell,
    /// like every other in-place edit on the dock.
    /// </summary>
    private void ShowItemHotkeyFlyout(FrameworkElement target, DockItem item)
    {
        var capture = new HotkeyCaptureButton
        {
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Named for the item, so Narrator says which icon the combination is being bound to —
            // the flyout is anchored on that icon, which conveys it visually and not otherwise.
            Label = item.DisplayName,
            Gesture = HotkeyGesture.TryParse(item.Hotkey, out var current) ? current : null,
        };

        var message = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            Text = Loc.Get("Hotkey.ItemHint"),
        };

        var panel = PanelBody(Loc.Get("Menu.Shortcut"));
        panel.Children.Add(capture);
        panel.Children.Add(message);

        var flyout = PanelFlyout(panel);

        capture.NeedsModifier += () => message.Text = Loc.Get("Hotkey.NeedModifier");
        capture.Assigned += gesture =>
        {
            bool registered = _manager.SetItemHotkey(item, gesture);
            if (gesture is not null && !registered)
            {
                // Keep the flyout open on a clash: the user's next move is to try another
                // combination, and closing it would make them re-open the menu to do that.
                message.Text = Loc.Get("Hotkey.Taken");
                return;
            }
            flyout.Hide();
        };

        // Opened as the same panel as every other in-place edit: anchored on the dock window and
        // free of its bounds, so the capture button is not drawn inside the strip. Auto-hide is
        // held out for the life of the panel by ShowPanelFlyout.
        ShowPanelFlyout(flyout, target);
        capture.Focus(FocusState.Programmatic);
    }
}
