using System.Globalization;
using System.Text.Json;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// Guards the translation tables themselves as much as the lookup code: a missing or misspelled
/// key in one of the eight shipped languages is invisible at build time and shows up in the UI
/// as a raw key like "Settings.Theme", so the completeness tests below are the safety net.
/// </summary>
public class LocTests
{
    public LocTests() => Loc.Initialize("en"); // tests run in any order; pin the language

    // ---- The string tables -------------------------------------------------

    private static Dictionary<string, string> Table(string code)
    {
        var name = $"Anchor.Strings.{code}.json";
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream(name);
        Assert.True(stream is not null, $"missing embedded string table '{name}'");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
    }

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Loc.Available)
            data.Add(language.Code);
        return data;
    }

    [Fact]
    public void Ships_English_plus_at_least_five_other_languages()
    {
        Assert.Contains(Loc.Available, l => l.Code == "en");
        Assert.True(Loc.Available.Count >= 6, $"only {Loc.Available.Count} languages");
        Assert.Equal(Loc.Available.Count, Loc.Available.Select(l => l.Code).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_translates_every_English_key(string code)
    {
        var english = Table("en");
        var translated = Table(code);

        var missing = english.Keys.Where(k => !translated.ContainsKey(k)).Order().ToList();
        Assert.True(missing.Count == 0, $"{code}.json is missing: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void No_language_carries_keys_English_does_not(string code)
    {
        // A stray key is a typo'd one: the UI asks for the English spelling and gets nothing.
        var english = Table("en");
        var extra = Table(code).Keys.Where(k => !english.ContainsKey(k)).Order().ToList();
        Assert.True(extra.Count == 0, $"{code}.json has unknown keys: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void No_translation_is_blank(string code)
    {
        var blank = Table(code).Where(kv => string.IsNullOrWhiteSpace(kv.Value))
                               .Select(kv => kv.Key).Order().ToList();
        Assert.True(blank.Count == 0, $"{code}.json has empty values: {string.Join(", ", blank)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Format_placeholders_survive_translation(string code)
    {
        // Loc.Format feeds these to string.Format; a translation that dropped {0} would silently
        // lose the version number / error detail, and an added {1} would throw at runtime.
        var english = Table("en");
        foreach (var (key, translated) in Table(code))
        {
            int expected = english[key].Count(c => c == '{');
            Assert.True(translated.Count(c => c == '{') == expected,
                $"{code}.json '{key}' has {translated.Count(c => c == '{')} placeholders, expected {expected}");
        }
    }

    // ---- Lookup behaviour --------------------------------------------------

    [Fact]
    public void Get_returns_the_translation_for_the_active_language()
    {
        Loc.Initialize("de");
        Assert.Equal("de", Loc.CurrentCode);
        Assert.Equal(Table("de")["Menu.Open"], Loc.Get("Menu.Open"));
    }

    [Fact]
    public void Get_returns_the_key_itself_when_nothing_defines_it()
    {
        Assert.Equal("No.Such.Key", Loc.Get("No.Such.Key"));
        Assert.Equal(string.Empty, Loc.Get(""));
    }

    [Fact]
    public void Format_substitutes_arguments()
    {
        Loc.Initialize("en");
        Assert.Equal("Version 1.2.3", Loc.Format("About.Version", "1.2.3"));
    }

    // ---- Language resolution ----------------------------------------------

    [Theory]
    [InlineData("de", "de")]
    [InlineData("zh-Hans", "zh-Hans")]
    // Matched case-insensitively, but normalized back to the canonical casing — the code becomes
    // part of an embedded-resource name, which is matched case-sensitively.
    [InlineData("DE", "de")]
    [InlineData("zh-hans", "zh-Hans")]
    [InlineData("kl", "en")]   // explicitly chosen but not shipped
    public void Resolve_honours_an_explicit_choice(string configured, string expected)
    {
        Assert.Equal(expected, Loc.Resolve(configured));
    }

    [Fact]
    public void Resolve_follows_Windows_when_no_choice_is_stored()
    {
        // Whatever this machine's display language is, the result must be a language we ship.
        foreach (var configured in new string?[] { null, "", "  " })
            Assert.Contains(Loc.Available, l => l.Code == Loc.Resolve(configured));
    }

    [Theory]
    [InlineData("de-AT", "de")]     // regional variant falls back to the base language
    [InlineData("pt-BR", "pt")]
    [InlineData("fr-CA", "fr")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "en")]     // traditional script: English beats the wrong characters
    [InlineData("zh-HK", "en")]
    [InlineData("nl-NL", "en")]     // a language we don't ship
    public void MatchCulture_picks_the_closest_shipped_language(string culture, string expected)
    {
        Assert.Equal(expected, Loc.MatchCulture(new CultureInfo(culture)));
    }
}
