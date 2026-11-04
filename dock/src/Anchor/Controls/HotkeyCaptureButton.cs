using Anchor.Interop;
using Anchor.Models;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Anchor.Controls;

/// <summary>
/// A button that doubles as its own key-capture surface: it shows the shortcut currently assigned,
/// and clicking it (or focusing it and pressing Space) arms capture so the next real key press —
/// together with whatever modifiers are held — becomes the new one. Escape cancels;
/// Backspace/Delete clears the shortcut entirely.
/// <para>
/// Shared by the global shortcut on Settings ▸ General and by the per-item shortcut on an icon's
/// right-click menu. Capturing a raw combination is fiddly enough — modifier-only presses,
/// unassignable keys, combinations with no modifier, abandoning a capture on focus loss — that
/// two copies of it would be two sets of bugs.
/// </para>
/// </summary>
public sealed partial class HotkeyCaptureButton : Button
{
    private bool _capturing;
    private HotkeyGesture? _gesture;

    public HotkeyCaptureButton()
    {
        Render();
        Click += (_, _) => BeginCapture();
        PreviewKeyDown += OnPreviewKeyDown;
        LostFocus += (_, _) =>
        {
            // Clicking or tabbing away abandons a capture, so the button never sits in
            // "Press keys…" forever.
            if (_capturing)
                EndCapture();
        };
    }

    /// <summary>The shortcut shown on the button. Setting it does not raise <see cref="Assigned"/>
    /// — that is for user edits only, so a caller can seed the control without echoing back.</summary>
    public HotkeyGesture? Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            Render();
        }
    }

    private string _label = Loc.Get("Settings.Hotkey");

    /// <summary>
    /// What this button is a shortcut <em>for</em>, prefixed to the combination in the name
    /// Narrator announces ("Quick-launch search: Ctrl+Alt+Space"). The visible content is only the
    /// combination — which of several shortcut buttons you are on is obvious on screen from the
    /// card around it, and not obvious at all to a screen reader.
    /// </summary>
    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            Render();
        }
    }

    /// <summary>Raised when the user assigns a combination (non-null) or clears it (null).</summary>
    public event Action<HotkeyGesture?>? Assigned;

    /// <summary>Raised when the user pressed a key with no Ctrl/Alt/Win — a combination Windows
    /// would refuse — so the host can say so. Capture stays armed.</summary>
    public event Action? NeedsModifier;

    private void BeginCapture()
    {
        if (_capturing)
            return;
        _capturing = true;
        Render();
        Focus(FocusState.Programmatic);
    }

    private void EndCapture()
    {
        _capturing = false;
        Render();
    }

    private void Render()
    {
        Content = _capturing
            ? Loc.Get("Hotkey.Press")
            : _gesture?.ToString() ?? Loc.Get("Hotkey.None");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _label + ": " + Content);
    }

    // PreviewKeyDown (not KeyDown): it runs before the platform gets a chance to treat the press
    // as an access key or as navigation, which is exactly what capturing a raw combination needs.
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing)
            return;

        var key = e.Key;
        e.Handled = true;

        // A modifier on its own isn't the shortcut yet — keep waiting for the real key.
        if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Menu
                or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows)
            return;

        if (key == Windows.System.VirtualKey.Escape)
        {
            EndCapture();
            return;
        }

        if (key is Windows.System.VirtualKey.Back or Windows.System.VirtualKey.Delete)
        {
            _gesture = null;
            EndCapture();
            Assigned?.Invoke(null);
            return;
        }

        // Not a key Anchor can bind (see HotkeyGesture's fixed table) — ignore it and stay armed
        // rather than committing something the config can't round-trip.
        if (HotkeyGesture.Name((uint)key) is null)
            return;

        var gesture = new HotkeyGesture(HeldModifiers(), (uint)key);
        if (!gesture.IsValid)
        {
            NeedsModifier?.Invoke(); // e.g. a bare "A", or Shift+A
            return;
        }

        _gesture = gesture;
        EndCapture();
        Assigned?.Invoke(gesture);
    }

    /// <summary>
    /// Which modifiers are physically held right now. Read from the keyboard rather than from the
    /// event args: a key-down carries only the modifiers the platform decided were part of the
    /// gesture, and a shortcut has to record exactly what the user was holding.
    /// </summary>
    public static HotkeyModifiers HeldModifiers()
    {
        var mods = HotkeyModifiers.None;
        if (Down(NativeMethods.VK_CONTROL))
            mods |= HotkeyModifiers.Control;
        if (Down(NativeMethods.VK_MENU))
            mods |= HotkeyModifiers.Alt;
        if (Down(NativeMethods.VK_SHIFT))
            mods |= HotkeyModifiers.Shift;
        if (Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN))
            mods |= HotkeyModifiers.Windows;
        return mods;

        static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}
