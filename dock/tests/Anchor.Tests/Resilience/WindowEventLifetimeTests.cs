using System.Text.RegularExpressions;
using Xunit;

namespace Anchor.Tests.Resilience;

/// <summary>
/// A closed window must be collectable. Source rules, because a WinUI <c>Window</c> cannot be built
/// in a unit test and the leak they prevent only shows in a running process.
/// <para>
/// <c>Window</c> is not a <c>DependencyObject</c>, so the XAML reference tracking that lets the GC
/// see through a managed-to-native cycle does not cover it: a handler subscribed to the window's own
/// <c>Activated</c> or <c>Closed</c> event, and referencing the window (a lambda that calls any
/// instance member does), is a strong reference the native window holds for as long as the process
/// runs. Measured here before the rule existed: after 12 Settings opens and a forced full GC there
/// were 12 live <c>SettingsWindow</c>s, and after 20 Search opens, 20 live <c>SearchWindow</c>s —
/// about 33 GDI objects, 40 handles and 5 MB per Settings open, 2 handles and 1.2 MB per Search
/// open. Handlers on child elements, and on <c>AppWindow</c>, do not matter.
/// </para>
/// <para>
/// The rule: subscribe a named method, and remove every handler — the <c>Closed</c> handler itself
/// included — inside <c>Closed</c>. A subscription to another window's event must not capture that
/// window either; take it from <c>sender</c>.
/// </para>
/// </summary>
public class WindowEventLifetimeTests
{
    private static readonly string[] OwnEvents = { "Activated", "Closed", "VisibilityChanged" };

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Anchor");
            if (File.Exists(Path.Combine(candidate, "Anchor.csproj")))
                return candidate;
        }
        throw new DirectoryNotFoundException("src/Anchor not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Every class that derives from <c>Window</c>, so a partial in another file is found too.</summary>
    private static HashSet<string> WindowClasses() =>
        SourceFiles()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"partial\s+class\s+(\w+)\s*:\s*Window\b")
                .Select(m => m.Groups[1].Value))
            .ToHashSet();

    private static string Strip(string line)
    {
        int slashes = line.IndexOf("//", StringComparison.Ordinal);
        return slashes < 0 ? line : line[..slashes];
    }

    /// <summary>The text of a method's block, by brace matching from its declaration.</summary>
    private static string? BodyOf(string text, string method)
    {
        var m = Regex.Match(text, @"\bvoid\s+" + Regex.Escape(method) + @"\s*\(");
        if (!m.Success)
            return null;
        int open = text.IndexOf('{', m.Index);
        int arrow = text.IndexOf("=>", m.Index, StringComparison.Ordinal);
        if (open < 0 || (arrow >= 0 && arrow < open))
            return text.Substring(m.Index, Math.Max(0, text.IndexOf(';', m.Index) - m.Index)); // expression-bodied
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0)
                return text.Substring(open, i - open + 1);
        }
        return null;
    }

    [Fact]
    public void A_window_subscribes_to_its_own_events_with_named_methods()
    {
        var windows = WindowClasses();
        Assert.NotEmpty(windows);

        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!windows.Any(w => Regex.IsMatch(text, @"partial\s+class\s+" + w + @"\b")))
                continue;

            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var code = Strip(lines[i]);
                var m = Regex.Match(code, @"^\s*(?:this\.)?(" + string.Join("|", OwnEvents) + @")\s*\+=\s*(.*)$");
                if (!m.Success)
                    continue;

                var rhs = m.Groups[2].Value.Trim();
                if (!Regex.IsMatch(rhs, @"^[A-Za-z_]\w*\s*;$"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A window's own event must be subscribed with a named method (a lambda or anonymous " +
            "delegate keeps the closed window alive for the life of the process):\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void A_window_removes_every_one_of_its_own_handlers_inside_Closed()
    {
        var windows = WindowClasses();
        var problems = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!windows.Any(w => Regex.IsMatch(text, @"partial\s+class\s+" + w + @"\b")))
                continue;

            var subs = Regex.Matches(text, @"^\s*(?:this\.)?(" + string.Join("|", OwnEvents) + @")\s*\+=\s*([A-Za-z_]\w*)\s*;", RegexOptions.Multiline);
            var closedHandlers = subs.Where(s => s.Groups[1].Value == "Closed").Select(s => s.Groups[2].Value).ToList();

            foreach (Match s in subs)
            {
                string evt = s.Groups[1].Value, handler = s.Groups[2].Value;
                if (closedHandlers.Count == 0)
                {
                    problems.Add($"{Path.GetFileName(file)}: '{evt} += {handler}' but the window never subscribes a Closed handler to remove it in");
                    continue;
                }

                var body = closedHandlers.Select(c => BodyOf(text, c) ?? string.Empty);
                if (!body.Any(b => Regex.IsMatch(b, @"\b" + evt + @"\s*-=\s*" + handler + @"\b")))
                    problems.Add($"{Path.GetFileName(file)}: '{evt} += {handler}' is never removed inside the Closed handler");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_window_unsubscribes_from_shared_models_inside_Closed()
    {
        // DockItem is owned by the config and outlives every window that shows it. A row that subscribes
        // item.PropertyChanged and unsubscribes only from its own Unloaded leaves the subscription behind
        // when the whole window closes (Unloaded does not reliably fire on descendants then), and the
        // handler's closure holds the window: gcroot on a retained SettingsWindow ran DockItem ->
        // PropertyChangedEventHandler -> closure -> SettingsWindow. Closed has to drop them itself.
        var windows = WindowClasses();
        var problems = new List<string>();

        // The fly-out bar's cells live in a popup, whose content does unload when the fly-out closes,
        // so their Unloaded unsubscription runs. Checked with gcroot on retained fly-outs after 20
        // opens: no managed root. (What does pile up there is native — see NativeReclaim.)
        var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DockWindow.FlyoutBar.cs" };

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!windows.Any(w => Regex.IsMatch(text, @"partial\s+class\s+" + w + @"\b")) || exempt.Contains(Path.GetFileName(file)))
                continue;

            var subs = Regex.Matches(text, @"^\s*(?!this\.)(\w+)\.PropertyChanged\s*\+=", RegexOptions.Multiline);
            if (subs.Count == 0)
                continue;

            var closed = Regex.Matches(text, @"^\s*(?:this\.)?Closed\s*\+=\s*([A-Za-z_]\w*)\s*;", RegexOptions.Multiline)
                .Select(m => BodyOf(text, m.Groups[1].Value) ?? string.Empty)
                .ToList();

            if (!closed.Any(b => Regex.IsMatch(b, @"PropertyChanged\s*-=") || b.Contains("_rowSubscriptions")))
                problems.Add($"{Path.GetFileName(file)}: {subs.Count} PropertyChanged subscription(s) on a shared model, but the Closed handler removes none");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_subscription_to_another_windows_Closed_does_not_capture_that_window()
    {
        // `window.Closed += (_, _) => _docks.Remove(window);` hands the native window a delegate whose
        // closure holds the managed window: the same cycle, one hop longer. Take it from `sender`.
        // Fields (the `_name` convention) are read through the manager and are nulled by the handler.
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            // Only locals that hold a Window: a Flyout is a DependencyObject, which XAML tracks.
            var windowLocals = Regex.Matches(text, @"\b(?:var|\w*Window)\s+([a-z]\w*)\s*=\s*new\s+\w*Window\s*\(")
                .Select(x => x.Groups[1].Value).ToHashSet();

            foreach (Match m in Regex.Matches(text, @"\b([a-z]\w*)\.Closed\s*\+=\s*\(([^)]*)\)\s*=>\s*(\{.*?\n\s*\}\s*;|[^;]*;)", RegexOptions.Singleline))
            {
                var local = m.Groups[1].Value;
                if (!windowLocals.Contains(local))
                    continue;
                if (Regex.IsMatch(m.Groups[3].Value, @"\b" + Regex.Escape(local) + @"\b"))
                {
                    int line = text[..m.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line}  {local}.Closed lambda mentions '{local}'");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}
