using System.Text.RegularExpressions;
using Xunit;

namespace Anchor.Tests.Resilience;

/// <summary>
/// The handlers Anchor gives to events it does not raise itself must not touch UI objects off the UI
/// thread and must not throw. Source rules: the event comes from the OS, so a unit test cannot raise it.
/// <para>
/// <c>UISettings.ColorValuesChanged</c> is raised on a thread of the notification system's own, while
/// the update behind it reads the root element's <c>ActualTheme</c> and writes the composition
/// controller, both of which belong to the UI thread. And an exception that leaves a handler that XAML
/// called is not catchable by anyone: XAML turns it into a stowed-exception fail-fast (0xc000027b), the
/// crash signature this app has on record (a dump from 2026-09-21 shows it on a
/// <c>Windows.UI.Immersive</c> notification thread calling into <c>Microsoft.UI.Xaml</c>). Whether
/// this handler is the one involved is not proven; this pins the shape it must have either way.
/// </para>
/// </summary>
public class SystemEventHandlerTests
{
    private static string AcrylicSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "Anchor", "Services", "AcrylicBackdropManager.cs");
            if (File.Exists(file))
                return File.ReadAllText(file);
        }
        throw new FileNotFoundException("AcrylicBackdropManager.cs not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The block (or expression body) of a private method, by brace matching.</summary>
    private static string BodyOf(string text, string method)
    {
        var m = Regex.Match(text, @"\bvoid\s+" + Regex.Escape(method) + @"\s*\(");
        Assert.True(m.Success, $"{method} not found");
        int open = text.IndexOf('{', m.Index);
        int arrow = text.IndexOf("=>", m.Index, StringComparison.Ordinal);
        if (open < 0 || (arrow >= 0 && arrow < open))
            return text.Substring(m.Index, text.IndexOf(';', m.Index) - m.Index);
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0)
                return text.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced braces in " + method);
    }

    [Fact]
    public void The_system_color_handler_hands_the_work_to_the_UI_thread()
    {
        var body = BodyOf(AcrylicSource(), "OnSystemColorsChanged");

        Assert.Contains("TryEnqueue", body);
        Assert.DoesNotContain("SyncWithSystemColors", body);
        Assert.DoesNotContain("UpdateTheme", body);
    }

    [Fact]
    public void The_color_update_cannot_throw_back_into_the_caller()
    {
        var source = AcrylicSource();

        var apply = BodyOf(source, "ApplySystemColors");
        Assert.Matches(@"\btry\b[\s\S]*\bcatch\b", apply);
        Assert.Contains("SyncWithSystemColors", apply);
        Assert.Contains("UpdateTheme", apply);

        // The XAML-raised theme handler goes through the same guarded path rather than calling the update itself.
        var themeChanged = BodyOf(source, "OnThemeChanged");
        Assert.Contains("ApplySystemColors", themeChanged);
        Assert.DoesNotContain("UpdateTheme()", themeChanged);
    }
}
