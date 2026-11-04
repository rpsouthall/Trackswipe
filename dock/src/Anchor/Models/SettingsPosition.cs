namespace Anchor.Models;

/// <summary>Where the settings gear sits on the dock strip.</summary>
public enum SettingsPosition
{
    /// <summary>After user items: … items | divider | gear (default).</summary>
    Trailing,

    /// <summary>Before user items: gear | divider | items …</summary>
    Leading,
}
