using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Anchor.UITests;

/// <summary>
/// A running Anchor under test: launches the exe against a throwaway data directory, exposes its
/// windows through UI Automation, and shuts it down (and cleans up) on dispose.
/// <para>
/// The data directory matters. Anchor persists items, position and settings the moment anything
/// changes, so a harness pointed at the real <c>%AppData%\Anchor</c> would rearrange — or reset —
/// the dock of whoever ran the tests. <c>ANCHOR_DATA_DIR</c> (see <c>Services/DockStore.cs</c>)
/// redirects both the config and the icon cache into a temp folder that is deleted afterwards,
/// and it also scopes the single-instance mutex, so these tests run happily alongside the
/// developer's own copy of Anchor.
/// </para>
/// </summary>
public sealed class AnchorApp : IDisposable
{
    /// <summary>The dock window's title — its UI Automation name (see <c>DockWindow.WindowTitle</c>).</summary>
    public const string DockWindowTitle = "Anchor Dock";

    /// <summary>How long to wait for a window or element to turn up before giving up.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly string _dataDirectory;

    public UIA3Automation Automation { get; }

    public Application Application { get; }

    private AnchorApp(Process process, Application application, UIA3Automation automation, string dataDirectory)
    {
        _process = process;
        _dataDirectory = dataDirectory;
        Application = application;
        Automation = automation;
    }

    // ---- Opt-in gating -----------------------------------------------------

    public static bool UITestsEnabled =>
        Environment.GetEnvironmentVariable("ANCHOR_UITESTS") is "1" or "true";

    /// <summary>The exe under test, or null when the variable is unset or points nowhere.</summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("ANCHOR_EXE");
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    // ---- Lifetime ----------------------------------------------------------

    /// <summary>Starts Anchor with a fresh, empty data directory and waits for its dock to appear.</summary>
    public static AnchorApp Launch() => Launch("""{ "Language": "en" }""");

    /// <summary>
    /// Starts Anchor against a hand-written <c>dock.json</c>, for a test that needs the dock to
    /// already hold something specific — a group with a child in it, say. Building that through
    /// the UI takes a run through Settings and the new-group window, which is a lot of moving
    /// parts to cross before the behavior actually under test is even reachable; seeding the
    /// config puts the dock in that state before the window opens.
    /// </summary>
    /// <param name="dockJson">The complete config file. Set <c>"Language": "en"</c> in it (see
    /// below), and <c>"Seeded": true</c> to suppress the first-run default items.</param>
    public static AnchorApp Launch(string dockJson)
    {
        string exe = ExecutablePath
                     ?? throw new InvalidOperationException("ANCHOR_EXE is not set to an existing Anchor.exe.");

        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "anchor-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        // Pin the UI language. The tests look controls up by the name Narrator announces, which
        // is translated, and Anchor follows the Windows display language by default — so without
        // this the suite would pass or fail depending on whose machine it ran on. The default
        // config leaves "Seeded" out, so that run is still a first run and the default items
        // appear.
        File.WriteAllText(Path.Combine(dataDirectory, "dock.json"), dockJson);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        start.Environment["ANCHOR_DATA_DIR"] = dataDirectory;

        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Anchor.exe did not start.");

        var automation = new UIA3Automation();
        var application = Application.Attach(process);

        var app = new AnchorApp(process, application, automation, dataDirectory);
        try
        {
            // The dock is the app's reason for existing; if it never shows, nothing else can pass
            // and a clear failure here beats a confusing one later.
            app.WaitForDock();
        }
        catch
        {
            app.Dispose();
            throw;
        }
        return app;
    }

    /// <summary>
    /// The dock strip. Found from the desktop root rather than through the process's "main
    /// window": the dock is a borderless tool window, deliberately absent from the taskbar and
    /// Alt-Tab, so it is not what Windows considers a main window.
    /// </summary>
    public Window WaitForDock() => WaitForWindow(DockWindowTitle);

    /// <summary>
    /// Every top-level window this process currently owns.
    /// <para>
    /// Enumerated off the desktop root and filtered by process id, rather than through FlaUI's
    /// <c>Application.GetAllTopLevelWindows</c>: that helper works from the process's "main
    /// window", and Anchor has none — every one of its windows is either a borderless tool window
    /// (the docks, deliberately off the taskbar and Alt-Tab) or a dialog. Off the desktop they are
    /// all plainly visible.
    /// </para>
    /// </summary>
    public IReadOnlyList<AutomationElement> TopLevelWindows()
    {
        int pid = _process.Id;
        return Automation.GetDesktop()
            .FindAllChildren()
            .Where(e =>
            {
                try
                {
                    return e.Properties.ProcessId.ValueOrDefault == pid;
                }
                catch
                {
                    return false; // a window that closed mid-enumeration
                }
            })
            .ToList();
    }

    /// <summary>Waits for a top-level window of this process with the given automation name.</summary>
    public Window WaitForWindow(string name)
    {
        var found = Retry(() => TopLevelWindows()
            .FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal))
            ?.AsWindow());

        return found ?? throw new TimeoutException(
            $"No window named '{name}' appeared within {Timeout.TotalSeconds:0}s. " +
            $"Windows seen: {DescribeWindows()}");
    }

    /// <summary>True once no window of this process carries the given name any more.</summary>
    public bool WaitForWindowToClose(string name) =>
        Retry(() => TopLevelWindows()
            .All(w => !string.Equals(w.Name, name, StringComparison.Ordinal))
            ? (bool?)true
            : null) ?? false;

    private string DescribeWindows()
    {
        try
        {
            var names = TopLevelWindows().Select(w => $"'{w.Name}'").ToList();
            return names.Count > 0 ? string.Join(", ", names) : "(none)";
        }
        catch (Exception ex)
        {
            return $"(could not enumerate: {ex.Message})";
        }
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns non-null or <see cref="Timeout"/> passes.
    /// UI Automation is asynchronous by nature — an element exists a beat after the click that
    /// creates it — so every lookup here is a retry rather than a single attempt.
    /// </summary>
    public static T? Retry<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                if (probe() is { } value)
                    return value;
            }
            catch (Exception)
            {
                // An element can vanish between being found and being read; treat that as "not
                // ready yet" and try again until the deadline.
            }

            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, returning false if it never does. The form
    /// <see cref="Retry{T}(Func{T?})"/> cannot take: an assertion that something is <em>gone</em>
    /// has no element to return, and waiting for a null lookup to keep being null would burn the
    /// whole timeout on every passing run.
    /// </summary>
    public static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                if (condition())
                    return true;
            }
            catch (Exception)
            {
                // Same as Retry: an element can vanish mid-read. Try again until the deadline.
            }

            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(150);
        }
    }

    /// <summary>Value-typed overload of <see cref="Retry{T}(Func{T?})"/>.</summary>
    public static T? Retry<T>(Func<T?> probe, bool _ = true) where T : struct
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                if (probe() is { } value)
                    return value;
            }
            catch (Exception)
            {
            }

            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(150);
        }
    }

    /// <summary>Finds a descendant by its AutomationProperties.AutomationId, waiting for it.</summary>
    public static AutomationElement? FindById(AutomationElement scope, string automationId) =>
        Retry(() => scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId)));

    /// <summary>
    /// Waits for a descendant button with the given (English — see <see cref="Launch"/>) name.
    /// A control that is only in a collapsed page is absent from the automation tree entirely, so
    /// finding one is itself proof that its page is showing.
    /// </summary>
    public static AutomationElement? FindButtonByName(AutomationElement scope, string name) =>
        Retry(() => scope.FindFirstDescendant(
            cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button))));

    /// <summary>Every descendant Button, waiting until at least one exists.</summary>
    public static IReadOnlyList<AutomationElement> FindButtons(AutomationElement scope) =>
        Retry(() =>
        {
            var found = scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            return found.Length > 0 ? found : null;
        }) ?? Array.Empty<AutomationElement>();

    /// <summary>
    /// Presses a control the way a keyboard user would — through its Invoke pattern — falling
    /// back to a real mouse click.
    /// <para>
    /// The fallback is not paranoia: WinUI 3's invoke pattern is reached over COM, and the
    /// marshalled call throws <see cref="InvalidCastException"/> for some peers. A click always
    /// works, but it moves the physical cursor, so it is the second choice rather than the first.
    /// </para>
    /// </summary>
    public static void Press(AutomationElement element)
    {
        var invoke = element.Patterns.Invoke.PatternOrDefault;
        if (invoke is not null)
        {
            try
            {
                invoke.Invoke();
                return;
            }
            catch (Exception)
            {
                // Fall through to the physical click below.
            }
        }
        element.Click();
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone, or we lack rights to signal it — either way there is nothing useful
            // left to do, and a failing teardown must not mask the test result.
        }

        Automation.Dispose();

        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // A temp folder left behind is untidy, not a failure.
        }
    }
}
