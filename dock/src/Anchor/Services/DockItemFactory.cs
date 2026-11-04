using Anchor.Models;

namespace Anchor.Services;

/// <summary>Shared helpers for turning a raw target string into a typed dock item.</summary>
public static class DockItemFactory
{
    /// <summary>
    /// How a Start-menu app is written down. Items in the shell's virtual <c>AppsFolder</c> — the
    /// list Start shows — have an AppUserModelID instead of a path, and qualifying it with the
    /// folder gives a target ShellExecute launches like any other (see <see cref="ShellDrop"/>).
    /// </summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>True for a <c>shell:AppsFolder\…</c> target: an app, but not a file on disk.</summary>
    public static bool IsAppsFolderTarget(string? target) =>
        target is not null && target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Infers the <see cref="DockItemKind"/> of a target (path or URL).</summary>
    public static DockItemKind Classify(string target)
    {
        // Ahead of everything else: an AppUserModelID is neither a path nor a URL, and its dots
        // would otherwise be read as a file extension.
        if (IsAppsFolderTarget(target))
            return DockItemKind.Application;

        if (Directory.Exists(target))
            return DockItemKind.Folder;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return DockItemKind.WebLink;

        return Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".exe" or ".lnk" or ".bat" or ".cmd" or ".com" => DockItemKind.Application,
            _ => DockItemKind.File,
        };
    }

    /// <summary>
    /// Turns the text of a dropped link into an http/https address, or null when it isn't one.
    /// Bare text is only promoted to a URL when it plausibly is one (a single dotted token such as
    /// <c>example.com</c>), so dropping a sentence of prose never becomes a web link.
    /// </summary>
    public static string? TryNormalizeWebUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();

        if (!text.Contains("://"))
        {
            if (text.Contains(' ') || !text.Contains('.'))
                return null;
            text = "https://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    /// <summary>A friendly default display name derived from a target.</summary>
    public static string SuggestName(string target)
    {
        // Only a fallback for an AppsFolder target: a drop names the item from the shell's own
        // display name ("Google Chrome"), which is friendlier than any part of the ID. This is
        // what a hand-typed one gets — the ID with the folder qualifier stripped off.
        if (IsAppsFolderTarget(target))
            return target[AppsFolderPrefix.Length..] is { Length: > 0 } appId ? appId : target;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.Host.Length > 0 ? uri.Host : target;

        var trimmed = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
