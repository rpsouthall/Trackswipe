using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;

namespace Anchor;

// The parts of an app icon's right-click menu that match the Windows taskbar's: the app's jump list
// (its recent or frequent files, the categories it files itself, and its tasks such as "New
// window"), the app's name to open another instance, pinning, and closing its windows.
public sealed partial class DockWindow
{
    /// <summary>
    /// Adds the jump list, the app's name and the pin/unpin command to the top of <paramref name="menu"/>.
    /// The jump list is read now, while the menu is built, and its COM objects let go once it closes.
    /// </summary>
    private void AddTaskbarSection(MenuFlyout menu, DockItem item, Action pin, string pinLabel)
    {
        var running = _manager.Running.Find(item);
        var categories = JumpLists.AppIdFor(item, running) is { } appId
            ? JumpLists.Read(appId)
            : new List<JumpCategory>();

        foreach (var category in categories)
        {
            if (category.Title.Length > 0)
                menu.Items.Add(new MenuFlyoutItem { Text = category.Title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var entry in category.Entries)
            {
                if (entry.IsSeparator)
                    menu.Items.Add(new MenuFlyoutSeparator());
                else
                    menu.Items.Add(TaskbarItem(entry.Title, () => JumpLists.Open(entry)));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        // The app itself: on the taskbar, its name starts another instance.
        menu.Items.Add(TaskbarItem(item.DisplayName, () =>
        {
            if (_manager.LaunchOrFocus(item, forceNewInstance: true) && !ReducedMotion)
                PlayLaunchZoom(item);
        }));
        menu.Items.Add(TaskbarItem(pinLabel, pin));

        if (categories.Count > 0)
        {
            // After the click has been handled: Closed can arrive before an item's Click.
            menu.Closed += (_, _) => DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => JumpLists.Release(categories));
        }
    }

    /// <summary>"Close window" or "Close all windows", as the taskbar says it, when the app has any open.</summary>
    private void AddCloseSection(MenuFlyout menu, DockItem item)
    {
        if (_manager.Running.Find(item) is not { } running)
            return;
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TaskbarItem(
            Loc.Get(running.Windows.Count > 1 ? "Menu.CloseAllWindows" : "Menu.CloseWindow"),
            () => _manager.Running.Quit(item)));
    }

    private static MenuFlyoutItem TaskbarItem(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        return item;
    }
}
