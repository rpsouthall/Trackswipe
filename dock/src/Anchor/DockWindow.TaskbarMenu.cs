using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anchor;

// The parts of an app icon's right-click menu that match the Windows taskbar's: the app's jump list
// (its recent or frequent files, the categories it files itself, and its tasks such as "New
// window"), the app's name to open another instance, pinning, and closing or ending it.
public sealed partial class DockWindow
{
    // A jump list carries whatever the app filed — full URLs and long document names among it — so
    // a menu sized to its longest line could stretch most of the way across the screen. Lines are
    // shortened with an ellipsis (the whole of one is in its tooltip), and the menu has a ceiling,
    // about the width of the taskbar's own jump lists.
    private const int MaxMenuLineChars = 48;
    private const double MaxMenuWidth = 360;
    private static Style? _menuStyle;

    /// <summary>The style every dock menu uses, which caps its width.</summary>
    private static Style MenuStyle => _menuStyle ??= new Style(typeof(MenuFlyoutPresenter))
    {
        Setters = { new Setter(FrameworkElement.MaxWidthProperty, MaxMenuWidth) },
    };

    /// <summary>
    /// Adds the jump list, the app's name and the pin/unpin command to the top of <paramref name="menu"/>.
    /// The jump list is read now, while the menu is built, and its COM objects let go once it closes.
    /// </summary>
    private void AddTaskbarSection(MenuFlyout menu, DockItem item, Action pin, string pinLabel)
    {
        menu.MenuFlyoutPresenterStyle = MenuStyle;
        var running = _manager.Running.Find(item);
        var categories = JumpLists.AppIdFor(item, running) is { } appId
            ? JumpLists.Read(appId)
            : new List<JumpCategory>();

        foreach (var category in categories)
        {
            if (category.Title.Length > 0)
                menu.Items.Add(new MenuFlyoutItem { Text = Shorten(category.Title), IsEnabled = false, FontWeight = FontWeights.SemiBold });
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

    /// <summary>
    /// "Close window" or "Close all windows", as the taskbar says it, and "End task" for an app that
    /// won't close — when the app has anything open.
    /// </summary>
    private void AddCloseSection(MenuFlyout menu, DockItem item)
    {
        if (_manager.Running.Find(item) is not { } running)
            return;
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TaskbarItem(
            Loc.Get(running.Windows.Count > 1 ? "Menu.CloseAllWindows" : "Menu.CloseWindow"),
            () => _manager.Running.Quit(item)));
        if (running.ProcessIds is { Count: > 0 })
            menu.Items.Add(TaskbarItem(Loc.Get("Menu.EndTask"), () => _manager.Running.EndTask(item)));
    }

    // ---- The same menu, away from the dock (TrackSwipe's taskbar label) ----------------------

    private static Window? _menuHost;
    private static Grid? _menuAnchor;

    /// <summary>
    /// Shows an app's taskbar-style menu at a screen point outside the dock — where TrackSwipe's
    /// taskbar label (the app in front, by name) was clicked. A WinUI menu opens from an element in
    /// a window, so it opens from a one-pixel borderless window moved to that point, made once and
    /// hidden again when the menu closes.
    /// </summary>
    internal void ShowTaskbarMenuAt(DockItem item, int x, int y)
    {
        var owner = _manager.Docks.FirstOrDefault(dock => dock.AllItems.Contains(item));
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = MenuStyle };
        if (owner is not null)
            AddTaskbarSection(menu, item, () => owner.RemoveDockItem(item), Loc.Get("Menu.RemoveFromDock"));
        else
            AddTaskbarSection(menu, item, () => _manager.KeepInDock(this, item, PinnedCount), Loc.Get("Menu.KeepInDock"));
        AddCloseSection(menu, item);

        var host = MenuHost();
        if (_menuAnchor is null)
            return;
        _menuAnchor.RequestedTheme = RootGrid.ActualTheme;
        host.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, 1, 1));
        host.AppWindow.Show();
        host.Activate(); // a menu needs its window in front to close when clicked away from
        menu.Closed += (_, _) => host.AppWindow.Hide();
        menu.ShowAt(_menuAnchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(0, 0),
        });
    }

    private static Window MenuHost()
    {
        if (_menuHost is not null)
            return _menuHost;
        _menuAnchor = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _menuHost = new Window { Content = _menuAnchor, Title = "TrackSwipe Dock menu" };
        _menuHost.ExtendsContentIntoTitleBar = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_menuHost);
        WindowChrome.MakeBorderlessToolWindow(_menuHost.AppWindow, hwnd);
        WindowChrome.StripFrame(hwnd);
        return _menuHost;
    }

    private static MenuFlyoutItem TaskbarItem(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = Shorten(text) };
        if (item.Text != text)
            ToolTipService.SetToolTip(item, text);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>A line cut to <see cref="MaxMenuLineChars"/>, keeping its start and end (a URL's host, a file's extension).</summary>
    private static string Shorten(string text)
    {
        if (text.Length <= MaxMenuLineChars)
            return text;
        int head = MaxMenuLineChars * 2 / 3, tail = MaxMenuLineChars - head - 1;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }
}
