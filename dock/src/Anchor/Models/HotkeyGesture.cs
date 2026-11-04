using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Anchor.Models;

/// <summary>
/// Modifier keys for a global hotkey. The values match the Win32 <c>MOD_*</c> constants that
/// <c>RegisterHotKey</c> takes, so the enum can be passed straight through.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,      // MOD_ALT
    Control = 0x0002,  // MOD_CONTROL
    Shift = 0x0004,    // MOD_SHIFT
    Windows = 0x0008,  // MOD_WIN
}

/// <summary>
/// A system-wide keyboard shortcut — one or more modifiers plus a key — stored in the config as
/// a readable string such as <c>"Ctrl+Alt+A"</c> so a hand-edited <c>dock.json</c> stays legible.
/// Immutable; parse with <see cref="TryParse"/> and render with <see cref="ToString"/>.
/// </summary>
public sealed class HotkeyGesture : IEquatable<HotkeyGesture>
{
    /// <summary>The shortcut Anchor ships with: Ctrl+Alt+A.</summary>
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);

    public HotkeyGesture(HotkeyModifiers modifiers, uint key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public HotkeyModifiers Modifiers { get; }

    /// <summary>The Win32 virtual-key code of the non-modifier key.</summary>
    public uint Key { get; }

    /// <summary>
    /// True when this gesture is registrable. Windows refuses (or would silently steal every
    /// press of) a bare key, so at least one modifier is required — Shift alone is allowed by
    /// the API but makes typing impossible, so it doesn't count on its own either.
    /// </summary>
    public bool IsValid =>
        Key != 0 && Name(Key) is not null &&
        (Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Windows)) != 0;

    /// <summary>Round-trippable text form, e.g. <c>"Ctrl+Alt+A"</c>. Modifier order is fixed.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
            sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            sb.Append("Win+");
        sb.Append(Name(Key) ?? "?");
        return sb.ToString();
    }

    /// <summary>
    /// Parses the text form produced by <see cref="ToString"/>. Tolerant of spacing, casing and
    /// the common modifier spellings ("Control", "Windows", "Meta"), so a hand-edited config
    /// still loads. Returns false — rather than a half-parsed gesture — on anything else.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var mods = HotkeyModifiers.None;
        uint key = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0)
                return false;

            switch (token.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    mods |= HotkeyModifiers.Control;
                    continue;
                case "ALT":
                    mods |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    mods |= HotkeyModifiers.Shift;
                    continue;
                case "WIN" or "WINDOWS" or "META":
                    mods |= HotkeyModifiers.Windows;
                    continue;
            }

            if (key != 0)
                return false; // two non-modifier keys
            if (Code(token) is not uint vk)
                return false;
            key = vk;
        }

        if (key == 0)
            return false;
        gesture = new HotkeyGesture(mods, key);
        return true;
    }

    // ---- Key name <-> virtual-key code ------------------------------------
    //
    // Deliberately a fixed, layout-independent table rather than MapVirtualKey: the config file
    // has to mean the same thing on every keyboard layout, and these are the keys a launcher
    // shortcut is plausibly bound to. Anything outside the table simply can't be assigned.

    private static readonly Dictionary<string, uint> ByName = BuildNameTable();
    private static readonly Dictionary<uint, string> ByCode =
        ByName.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>The canonical name of a virtual-key code, or null if it isn't assignable.</summary>
    public static string? Name(uint key) => ByCode.TryGetValue(key, out var n) ? n : null;

    /// <summary>The virtual-key code for a key name, or null if it isn't assignable.</summary>
    public static uint? Code(string name) =>
        ByName.TryGetValue(name.Trim(), out var vk) ? vk : null;

    private static Dictionary<string, uint> BuildNameTable()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        for (uint c = 'A'; c <= 'Z'; c++)
            map[((char)c).ToString()] = c;
        for (uint d = '0'; d <= '9'; d++)
            map[((char)d).ToString()] = d;
        for (uint f = 1; f <= 24; f++)
            map["F" + f] = 0x70 + f - 1; // VK_F1 = 0x70

        map["Space"] = 0x20;
        map["Enter"] = 0x0D;
        map["Tab"] = 0x09;
        map["Backspace"] = 0x08;
        map["Insert"] = 0x2D;
        map["Delete"] = 0x2E;
        map["Home"] = 0x24;
        map["End"] = 0x23;
        map["PageUp"] = 0x21;
        map["PageDown"] = 0x22;
        map["Left"] = 0x25;
        map["Up"] = 0x26;
        map["Right"] = 0x27;
        map["Down"] = 0x28;

        return map;
    }

    // ---- Equality ---------------------------------------------------------

    public bool Equals(HotkeyGesture? other) =>
        other is not null && other.Modifiers == Modifiers && other.Key == Key;

    public override bool Equals(object? obj) => Equals(obj as HotkeyGesture);

    public override int GetHashCode() => HashCode.Combine(Modifiers, Key);
}
