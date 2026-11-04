using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Anchor.Services;

/// <summary>One selectable UI language: its BCP-47 code and its name written in itself.</summary>
/// <param name="Code">BCP-47 code, or the empty string for "follow Windows".</param>
/// <param name="NativeName">The language's own name, so a user who can't read the current UI
/// language can still find theirs in the list.</param>
public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// Anchor's string table. Translations live as embedded JSON dictionaries under <c>Strings\</c>
/// (one flat key→text file per language), and are resolved once at startup into
/// <see cref="Get"/>'s lookup with English as the fallback for any key a translation is missing.
/// <para>
/// Deliberately not the WinUI resource (.resw/PRI) pipeline: Anchor ships unpackaged, where the
/// PRI-based <c>ResourceLoader</c> is fiddly to get right and impossible to override per-user —
/// and the language here is an <em>app</em> setting (see <c>DockConfig.Language</c>), not the
/// Windows display language. A plain dictionary makes "match Windows, or pick your own" trivial.
/// </para>
/// </summary>
public static class Loc
{
    /// <summary>Every language Anchor ships, English first, then alphabetical by code.</summary>
    public static IReadOnlyList<LanguageOption> Available { get; } = new[]
    {
        new LanguageOption("en", "English"),
        new LanguageOption("de", "Deutsch"),
        new LanguageOption("es", "Español"),
        new LanguageOption("fr", "Français"),
        new LanguageOption("hi", "हिन्दी"),
        new LanguageOption("ja", "日本語"),
        new LanguageOption("pt", "Português (Brasil)"),
        new LanguageOption("zh-Hans", "简体中文"),
    };

    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    /// <summary>The language code actually in use (always one of <see cref="Available"/>).</summary>
    public static string CurrentCode { get; private set; } = "en";

    /// <summary>
    /// Loads the string table for <paramref name="configured"/> (a code from
    /// <see cref="Available"/>, or null/empty to follow the Windows display language). Safe to
    /// call more than once; the last call wins.
    /// </summary>
    public static void Initialize(string? configured)
    {
        CurrentCode = Resolve(configured);
        _fallback = LoadTable("en");
        _strings = CurrentCode == "en" ? _fallback : LoadTable(CurrentCode);
    }

    /// <summary>
    /// Picks the language to use: an explicit choice if it's one we ship, otherwise the closest
    /// match to the Windows display language, otherwise English.
    /// </summary>
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Canonical casing, not whatever the config file said: the code becomes part of an
            // embedded-resource name, and those are matched case-sensitively.
            return Canonical(configured)
                   ?? "en"; // an explicit choice we don't ship — don't silently follow Windows
        }

        try
        {
            return MatchCulture(CultureInfo.CurrentUICulture);
        }
        catch
        {
            return "en";
        }
    }

    /// <summary>
    /// Maps a culture onto the closest language Anchor ships: exact code, then the Chinese script
    /// (zh-CN/zh-SG → Simplified; zh-TW/zh-HK have no table yet and fall back to English), then
    /// the bare two-letter language, else English.
    /// </summary>
    public static string MatchCulture(CultureInfo culture)
    {
        if (Canonical(culture.Name) is { } exact)
            return exact;

        if (culture.TwoLetterISOLanguageName == "zh")
        {
            // Simplified only: traditional-script users read a different set of characters, so
            // showing them zh-Hans would be worse than the English they can at least recognize.
            var name = culture.Name.Replace('_', '-');
            return name.Contains("Hans", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("-CN", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("-SG", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hans"
                : "en";
        }

        return Canonical(culture.TwoLetterISOLanguageName) ?? "en";
    }

    /// <summary>The shipped code matching <paramref name="code"/>, or null if we don't ship it.</summary>
    private static string? Canonical(string code) =>
        Available.FirstOrDefault(
            l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;

    /// <summary>
    /// The translated text for <paramref name="key"/>, falling back to English and finally to the
    /// key itself — a missing string shows up as a visible key rather than an empty control.
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;
        if (_strings.TryGetValue(key, out var s) || _fallback.TryGetValue(key, out s))
            return s;
        Diag.Log($"Loc: missing string '{key}' ({CurrentCode})");
        return key;
    }

    /// <summary>
    /// <see cref="Get"/> with <c>{0}</c>-style placeholders filled in. Invariant culture: the
    /// arguments Anchor substitutes are version numbers and exception messages, not user-facing
    /// numerics that would want local formatting.
    /// </summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    private static Dictionary<string, string> LoadTable(string code)
    {
        var name = $"Anchor.Strings.{code}.json";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null)
            {
                Diag.Log($"Loc: no embedded string table '{name}'");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Diag.Log($"Loc: failed to load '{name}': {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
