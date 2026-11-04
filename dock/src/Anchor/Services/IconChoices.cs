namespace Anchor.Services;

/// <summary>
/// A curated set of built-in Segoe Fluent Icons glyphs offered by the icon picker, as a
/// no-network, no-file-dialog alternative to browsing for a custom image. Deliberately a small,
/// general-purpose set — enough to tell a "Dev tools" group from a "Media" group at a glance —
/// not an exhaustive dump of every icon Windows ships.
/// <para>
/// <see cref="Choice.Name"/> is a plain English literal rather than routed through
/// <see cref="Loc"/>: it is a mnemonic label on a swatch grid (the glyph itself is the content
/// being chosen), not a sentence a non-English speaker needs translated to use the feature —
/// the same scoping most icon/color pickers give their swatch names. Every other string in the
/// picker's chrome (title, the "browse for an image" entry) is fully localized as usual.
/// </para>
/// </summary>
public static class IconChoices
{
    public readonly record struct Choice(string Glyph, string Name);

    public static readonly IReadOnlyList<Choice> All = new[]
    {
        new Choice("\uE8B7", "Folder"),
        new Choice("\uE838", "Open folder"),
        new Choice("\uE80F", "Home"),
        new Choice("\uE774", "Globe"),
        new Choice("\uE721", "Search"),
        new Choice("\uE713", "Settings"),
        new Choice("\uE710", "Add"),
        new Choice("\uE74E", "Pin"),
        new Choice("\uE8A5", "Link"),
        new Choice("\uE943", "Code"),
        new Choice("\uE8BD", "Chat"),
        new Choice("\uE715", "Mail"),
        new Choice("\uE714", "Video"),
        new Choice("\uE722", "Camera"),
        new Choice("\uE91B", "Photos"),
        new Choice("\uE787", "Calendar"),
        new Choice("\uE823", "Clock"),
        new Choice("\uE753", "Cloud"),
        new Choice("\uE72E", "Lock"),
        new Choice("\uE734", "Favorite"),
        new Choice("\uE719", "Shop"),
        new Choice("\uE8F1", "Library"),
        new Choice("\uE95E", "Health"),
        new Choice("\uE716", "People"),
        new Choice("\uE968", "Network"),
        new Choice("\uE90F", "Tools"),
        new Choice("\uE804", "Car"),
        new Choice("\uE7C3", "Document"),
        new Choice("\uE8A7", "Music"),
        new Choice("\uE7FC", "Game"),
        new Choice("\uE7F4", "Phone"),
        new Choice("\uE770", "Map"),
        new Choice("\uE946", "Terminal"),
        new Choice("\uE7EF", "Download"),
        new Choice("\uE898", "Upload"),
        new Choice("\uE756", "Command"),
    };
}
