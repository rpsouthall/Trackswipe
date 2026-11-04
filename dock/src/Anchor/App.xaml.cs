using System.Threading;
using Anchor.Services;
using Microsoft.UI.Xaml;

namespace Anchor;

/// <summary>
/// Application entry point. Creates the <see cref="DockManager"/> (which in turn creates a window
/// per configured dock), enforces one Anchor per config (a second launch exits quietly), and
/// routes otherwise-unhandled exceptions to the diagnostic log so a crash leaves a trace instead
/// of vanishing silently.
/// </summary>
public partial class App : Application
{
    /// <summary>The running docks and everything app-wide that goes with them.</summary>
    public static DockManager? Manager { get; private set; }

    // Held for the whole process lifetime so the named mutex it represents stays alive; a second
    // launch (e.g. the Start-menu entry while the "start with Windows" copy is already running)
    // sees the mutex already exists and bows out. Kept in a static field so the GC/finalizer
    // never closes the handle out from under a running dock. "Local\" scopes it per user session.
    private static Mutex? _instanceMutex;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A dock is an always-on utility, so record what went wrong (to %Temp%\anchor.log) for
            // diagnosis. Handled is deliberately left false: swallowing every fault could leave the
            // dock running in a corrupt state, so a genuinely unhandled exception still surfaces.
            Diag.Log($"UNHANDLED: {e.Message}\n{e.Exception}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!IsFirstInstance())
        {
            Diag.Log("Another Anchor instance is already running — exiting this one.");
            Exit();
            return;
        }

        // Load the string table before any window is constructed: XAML resolves its
        // {loc:Localize} bindings as it loads, so the language has to be settled first. The
        // config is read once here and handed to the manager — loading it twice would give the
        // dock windows a different object from the one that gets saved.
        var config = DockStore.Load();
        Loc.Initialize(config.Language);

        Manager = new DockManager(config);
        Manager.Start();
    }

    /// <summary>
    /// True if this is the only running Anchor in the current session. Uses a named mutex: the
    /// first process creates it (and keeps it alive via <see cref="_instanceMutex"/>); any later
    /// process finds it already present and returns false.
    /// <para>
    /// The name is scoped to the data directory, so two copies pointed at the same dock still
    /// collapse to one (the case this exists for) while a copy running against its own data —
    /// the UI test harness, or a portable install on a removable drive — is a separate instance
    /// rather than one that silently refuses to start.
    /// </para>
    /// </summary>
    private static bool IsFirstInstance()
    {
        string name = @"Local\Anchor.SingleInstance.v1." + DataDirectoryKey();
        _instanceMutex = new Mutex(initiallyOwned: false, name, out bool createdNew);
        return createdNew;
    }

    /// <summary>
    /// Releases the single-instance lock. Called right before <see cref="DockManager.Restart"/>
    /// replaces this process — the mutex is named and held for the process lifetime (see
    /// <see cref="_instanceMutex"/>), so without this a relaunched copy would find it still held
    /// and immediately bow out thinking another Anchor is already running.
    /// </summary>
    public static void ReleaseInstanceLock()
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    /// <summary>A short, stable, path-character-free token identifying the data directory.</summary>
    private static string DataDirectoryKey()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            DockStore.DataDirectory.ToLowerInvariant());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8);
    }
}
