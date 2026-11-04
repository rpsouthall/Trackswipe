using System.Runtime.InteropServices;
using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// Anchor's notification-area (system tray) icon: the app has no taskbar button — it is a
/// borderless tool window by design — so the tray icon is what tells you it's running and gives
/// you a way back to it when the dock is hidden behind a screen edge or pushed off-screen.
/// <list type="bullet">
/// <item>Left-click (or double-click) brings the dock to the front.</item>
/// <item>Right-click opens a native menu: show/hide the dock, add an item, settings, quit.</item>
/// </list>
/// Uses a native popup menu rather than a XAML flyout because the icon's owner window
/// (<see cref="MessageWindow"/>) has no XAML content to anchor a flyout to.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // Any value in the WM_APP range works; the shell echoes it back for every mouse event on the
    // icon, with the event itself in the low word of lParam.
    private const uint CallbackMessage = NativeMethods.WM_APP + 1;

    private const uint IconId = 1;

    // Menu command ids. Arbitrary but non-zero: TrackPopupMenuEx returns 0 for "dismissed".
    private const uint CmdShowHide = 1;
    private const uint CmdAddNew = 2;
    private const uint CmdSettings = 3;
    private const uint CmdQuit = 4;
    private const uint CmdSearch = 5;
    private const uint CmdUpdate = 6;

    private readonly MessageWindow _window;
    private readonly uint _taskbarCreated;
    private nint _icon;
    // The last-resort system icon must be loaded LR_SHARED, and a shared icon must never be
    // passed to DestroyIcon — so remember which kind we ended up with.
    private bool _iconIsShared;
    private bool _added;
    private bool _disposed;
    private bool _menuOpen;

    /// <summary>Left-click / double-click: bring the dock to the front.</summary>
    public event Action? Activated;

    /// <summary>Menu: show the dock if hidden, hide it if shown.</summary>
    public event Action? ShowHideRequested;

    /// <summary>Menu: open the "Add to Dock" window.</summary>
    public event Action? AddNewRequested;

    /// <summary>Menu: open Settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>Menu: quit Anchor.</summary>
    public event Action? QuitRequested;

    /// <summary>Menu: open quick-launch search.</summary>
    public event Action? SearchRequested;

    /// <summary>Menu: an update was found; show what it is.</summary>
    public event Action? UpdateRequested;

    /// <summary>
    /// Supplies the show/hide item's label each time the menu opens, so it reads "Hide dock"
    /// while the dock is visible and "Show dock" while it isn't.
    /// </summary>
    public Func<bool>? IsDockVisible { get; set; }

    /// <summary>
    /// True while a newer release has been found and not yet dismissed. The startup update check
    /// deliberately raises nothing on screen — an app that interrupts you as it starts is worse
    /// than one that is a version behind — so this menu entry is how the result surfaces.
    /// </summary>
    public Func<bool>? HasUpdate { get; set; }

    public TrayIconService(MessageWindow window)
    {
        _window = window;

        // Explorer re-broadcasts this after a crash/restart; every tray icon has to re-add itself
        // or it is gone for the rest of the session.
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        _window.MessageReceived += OnMessage;
        (_icon, _iconIsShared) = LoadAppIcon();
        Add();
    }

    // ---- Shell registration ------------------------------------------------

    private void Add()
    {
        if (_window.Handle == nint.Zero || _added)
            return;

        var data = NewData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON |
                           NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_added)
            Diag.Log($"Tray: NIM_ADD failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Re-reads the tooltip from the string table (after a language change).</summary>
    public void RefreshTooltip()
    {
        if (!_added)
            return;
        var data = NewData(NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private NativeMethods.NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        // szTip is a fixed 128-char buffer; anything longer would overflow the marshaller.
        szTip = Truncate(Loc.Get("Tray.Tooltip"), 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// The exe's own icon at the shell's small-icon size (DPI-aware). Falls back to extracting it
    /// from the executable file, then to the generic application icon, so the tray icon is never
    /// blank even if the resource lookup fails.
    /// </summary>
    /// <returns>The icon handle and whether it is a shared (non-destroyable) system icon.</returns>
    private static (nint Icon, bool Shared) LoadAppIcon()
    {
        int cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        int cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);

        var instance = NativeMethods.GetModuleHandle(null);
        var icon = NativeMethods.LoadImage(
            instance, NativeMethods.IDI_APPLICATION, NativeMethods.IMAGE_ICON, cx, cy,
            NativeMethods.LR_DEFAULTCOLOR);
        if (icon != nint.Zero)
            return (icon, false);

        if (Environment.ProcessPath is { Length: > 0 } exe &&
            NativeMethods.ExtractIconEx(exe, 0, out var large, out var small, 1) > 0)
        {
            if (large != nint.Zero && large != small)
                NativeMethods.DestroyIcon(large);
            if (small != nint.Zero)
                return (small, false);
        }

        Diag.Log("Tray: falling back to the generic application icon");
        return (NativeMethods.LoadImage(
            nint.Zero, NativeMethods.IDI_APPLICATION, NativeMethods.IMAGE_ICON, cx, cy,
            NativeMethods.LR_DEFAULTCOLOR | NativeMethods.LR_SHARED), true);
    }

    // ---- Message handling --------------------------------------------------

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated)
        {
            _added = false;
            Add();
            return;
        }

        if (msg != CallbackMessage)
            return;

        // The shell packs the mouse event into the low word of lParam.
        uint mouseEvent = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (mouseEvent)
        {
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_LBUTTONDBLCLK:
                Activated?.Invoke();
                break;
            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        // TrackPopupMenuEx runs its own modal message loop, and the tray keeps sending click
        // messages while it does; re-entering would stack menus.
        if (_menuOpen || _window.Handle == nint.Zero)
            return;
        if (!NativeMethods.GetCursorPos(out var cursor))
            return;

        _menuOpen = true;
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == nint.Zero)
        {
            _menuOpen = false;
            return;
        }

        try
        {
            bool visible = IsDockVisible?.Invoke() ?? true;
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdShowHide,
                Loc.Get(visible ? "Tray.HideDock" : "Tray.Show"));
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdSearch, Loc.Get("Tray.Search"));
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdAddNew, Loc.Get("Tray.AddNew"));
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdSettings, Loc.Get("Tray.Settings"));

            // Only present when there is actually something to report, so the menu doesn't carry
            // a permanent "check for updates" entry for a feature that is off by default.
            if (HasUpdate?.Invoke() == true)
            {
                NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
                NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdUpdate, Loc.Get("Tray.Update"));
            }

            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdQuit, Loc.Get("Tray.Quit"));

            // The long-standing shell requirement: the owner must be foreground before the menu
            // is tracked, and must be poked with a null message afterwards, or the menu refuses
            // to dismiss when you click away from it.
            NativeMethods.SetForegroundWindow(_window.Handle);

            int command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                cursor.X, cursor.Y, _window.Handle, nint.Zero);

            NativeMethods.PostMessage(_window.Handle, NativeMethods.WM_NULL, nint.Zero, nint.Zero);

            switch ((uint)command)
            {
                case CmdShowHide:
                    ShowHideRequested?.Invoke();
                    break;
                case CmdAddNew:
                    AddNewRequested?.Invoke();
                    break;
                case CmdSettings:
                    SettingsRequested?.Invoke();
                    break;
                case CmdSearch:
                    SearchRequested?.Invoke();
                    break;
                case CmdUpdate:
                    UpdateRequested?.Invoke();
                    break;
                case CmdQuit:
                    QuitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window.MessageReceived -= OnMessage;

        if (_added)
        {
            var data = NewData(0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != nint.Zero && !_iconIsShared)
            NativeMethods.DestroyIcon(_icon);
        _icon = nint.Zero;
    }
}
