using System.Runtime.InteropServices;
using Anchor.Interop;
using Anchor.Models;

namespace Anchor.Services;

/// <summary>
/// Registers Anchor's system-wide shortcuts with Windows and raises <see cref="Pressed"/> with the
/// id of whichever one was used. Windows delivers <c>WM_HOTKEY</c> to a window, so this rides on
/// the same hidden <see cref="MessageWindow"/> the tray icon uses.
/// <para>
/// Several shortcuts can be live at once — the one that summons the docks, the one that opens
/// quick-launch search, and one per item that has been given one. Each is keyed by a caller-chosen
/// id (see <c>DockManager</c>, which allocates them), and <c>WM_HOTKEY</c> is routed on the same
/// id, which arrives in <c>wParam</c>.
/// </para>
/// <para>
/// Registering an id that is already registered replaces it, and a combination Windows refuses —
/// almost always because another application already owns it — reports false without throwing, so
/// the caller can surface a "pick a different one" message instead of failing silently.
/// </para>
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, HotkeyGesture> _registered = new();
    private bool _disposed;

    /// <summary>Raised on the UI thread with the id of the shortcut that was pressed.</summary>
    public event Action<int>? Pressed;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != NativeMethods.WM_HOTKEY)
            return;
        int id = wParam.ToInt32();
        if (_registered.ContainsKey(id))
            Pressed?.Invoke(id);
    }

    /// <summary>The gesture registered under <paramref name="id"/>, if any.</summary>
    public HotkeyGesture? Current(int id) =>
        _registered.TryGetValue(id, out var gesture) ? gesture : null;

    /// <summary>
    /// Makes <paramref name="gesture"/> the live shortcut for <paramref name="id"/>, replacing any
    /// previous one. Passing null (or an invalid gesture) just unregisters that id.
    /// </summary>
    /// <returns>True if the shortcut is now registered; false if Windows refused it — almost
    /// always because another application already holds that combination.</returns>
    public bool Register(int id, HotkeyGesture? gesture)
    {
        Unregister(id);

        if (_window.Handle == nint.Zero || gesture is null || !gesture.IsValid)
            return false;

        // MOD_NOREPEAT so holding the keys down fires once rather than hammering the action on
        // every auto-repeat.
        uint modifiers = (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, modifiers, gesture.Key))
        {
            Diag.Log($"Hotkey: RegisterHotKey({gesture}) for id {id} failed ({Marshal.GetLastWin32Error()})");
            return false;
        }

        _registered[id] = gesture;
        return true;
    }

    /// <summary>Releases one shortcut back to the system. Safe when nothing is registered for it.</summary>
    public void Unregister(int id)
    {
        if (_registered.Remove(id) && _window.Handle != nint.Zero)
            NativeMethods.UnregisterHotKey(_window.Handle, id);
    }

    /// <summary>Releases every shortcut this service holds.</summary>
    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
            Unregister(id);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window.MessageReceived -= OnMessage;
        UnregisterAll();
    }
}
