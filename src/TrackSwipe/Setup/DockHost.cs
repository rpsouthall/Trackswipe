using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TrackSwipe.Setup;

/// <summary>
/// TrackSwipe's dock: an optional part, turned on from the notification-area menu.
/// <para>
/// The dock is its own process (a WinUI app, in a <c>Dock</c> folder beside TrackSwipe.exe), so TrackSwipe itself stays
/// small and nothing the dock does can ever hold up a swipe; to the user it is one app, with one icon, one install and
/// one set of menus. It isn't part of TrackSwipe's own download: the first time it's turned on, the release's dock is
/// fetched (after asking), checked against the SHA-256 this build of TrackSwipe was made with, and unpacked. It is
/// started as the signed-in user even when TrackSwipe runs as administrator, restarted if it stops on its own, and
/// told to quit when it's turned off or TrackSwipe quits.
/// </para>
/// </summary>
internal sealed class DockHost : IDisposable
{
    /// <summary>The registered message the dock listens for, and its commands (see the dock's Hosting class).</summary>
    private static readonly uint CommandMessage = RegisterWindowMessageW("TrackSwipe.Dock");
    private const int QuitCommand = 1, SettingsCommand = 2;

    private static readonly string Arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public static string Folder => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Dock");
    public static string Exe => Path.Combine(Folder, "Anchor.exe");
    private static string StampPath => Path.Combine(Folder, "trackswipe-dock.sha256");

    /// <summary>The SHA-256 of the dock archive this build was made with (set by build.ps1), or null for a development build.</summary>
    private static readonly string? ExpectedHash = Metadata($"DockSha256-{Arch}");
    private static readonly long ExpectedBytes = long.TryParse(Metadata($"DockBytes-{Arch}"), out var bytes) ? bytes : 0;

    private readonly Action<string, string> _notify;
    private readonly Action<Action> _post;
    private Process? _process;
    private bool _wanted;
    private readonly Queue<long> _restarts = new();

    /// <param name="notify">Shows a balloon from TrackSwipe's icon.</param>
    /// <param name="post">Runs an action on the thread that owns this (the shell thread).</param>
    public DockHost(Action<string, string> notify, Action<Action> post)
    {
        _notify = notify;
        _post = post;
    }

    public static bool Installed => File.Exists(Exe);

    /// <summary>Whether the installed dock is the one this TrackSwipe was built with (always true for a development build).</summary>
    private static bool Current => Installed && (ExpectedHash is null || (File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == ExpectedHash));

    /// <summary>Turns the dock on: installs or updates it first if it needs to (asking before a first download).</summary>
    public void TurnOn(bool askFirst)
    {
        _wanted = true;
        if (Current)
        {
            Start();
            return;
        }
        if (!Installed && askFirst && !ConfirmDownload())
        {
            _wanted = false;
            Changed?.Invoke(false);
            return;
        }
        _notify("Getting the dock", Installed ? "Updating the dock to match this version of TrackSwipe…" : "Downloading the dock. It appears when it's ready.");
        _ = Task.Run(async () =>
        {
            string? failure = null;
            try { await InstallAsync(); }
            catch (Exception ex) { failure = ex.Message; Log.Write($"Dock: install failed: {ex}"); }
            _post(() =>
            {
                if (failure is not null)
                {
                    _notify("The dock couldn't be installed", failure);
                    if (!Installed) { _wanted = false; Changed?.Invoke(false); return; }
                }
                if (_wanted) Start();
            });
        });
    }

    /// <summary>Raised when the dock turns itself off (the download was declined or failed), so the menu can follow.</summary>
    public event Action<bool>? Changed;

    public void TurnOff()
    {
        _wanted = false;
        Stop();
        Overlay.DockTargets.Clear(); // windows go back to their taskbar buttons
    }

    public void OpenSettings() => Send(SettingsCommand);

    private static bool ConfirmDownload()
    {
        var size = ExpectedBytes > 0 ? $" ({ExpectedBytes / 1_000_000.0:0} MB)" : "";
        var result = Win32.TaskDialog.Show(new Win32.TaskDialog.Page
        {
            Heading = "Download the dock?",
            Content = "The dock is an optional part of TrackSwipe: pinned and open apps along the bottom of the screen, as on a Mac. " +
                      $"It's downloaded the first time it's turned on{size}, from this version's release on GitHub.",
            Buttons = new[] { new Win32.TaskDialog.Button(Win32.TaskDialog.IdOk, "Download and turn on the dock") },
        });
        return result.Button == Win32.TaskDialog.IdOk;
    }

    /// <summary>
    /// Fetches the dock and unpacks it into <see cref="Folder"/>. A development build (no expected hash) uses a
    /// TrackSwipe-Dock-&lt;arch&gt;.zip beside TrackSwipe.exe, or in dist\ beside it, instead of downloading.
    /// </summary>
    private static async Task InstallAsync()
    {
        var zip = Path.Combine(Path.GetTempPath(), $"TrackSwipe-Dock-{Arch}-{Guid.NewGuid():N}.zip");
        try
        {
            var local = new[]
            {
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, $"TrackSwipe-Dock-{Arch}.zip"),
            }.FirstOrDefault(File.Exists);
            if (local is not null && ExpectedHash is null)
            {
                File.Copy(local, zip, overwrite: true);
            }
            else
            {
                if (ExpectedHash is null) throw new InvalidOperationException("This build of TrackSwipe has no dock to download.");
                var version = typeof(DockHost).Assembly.GetName().Version?.ToString(3);
                var url = $"https://github.com/rpsouthall/Trackswipe/releases/download/v{version}/TrackSwipe-Dock-{Arch}.zip";
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"TrackSwipe/{version}");
                await using (var source = await http.GetStreamAsync(url))
                await using (var file = File.Create(zip))
                    await source.CopyToAsync(file);
            }

            string hash;
            await using (var file = File.OpenRead(zip))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (ExpectedHash is not null && !string.Equals(hash, ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded dock didn't match this version of TrackSwipe, so it wasn't installed.");

            // Unpacked beside the old one, then swapped in, so a failure halfway leaves the old dock working.
            var staging = Folder + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(zip, staging);
            File.WriteAllText(Path.Combine(staging, "trackswipe-dock.sha256"), hash);
            StopRunningCopies();
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
            Directory.Move(staging, Folder);
            Log.Write($"Dock: installed ({hash[..12]}…).");
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
    }

    /// <summary>Starts the dock if it isn't running, and watches it.</summary>
    private void Start()
    {
        if (!_wanted || !Installed) return;
        if (Running() is { } existing)
        {
            Watch(existing);
            return;
        }
        // At sign-in TrackSwipe starts before Explorer has finished starting; a dock started then comes up into a
        // shell that isn't ready for it. Wait for the taskbar (and a moment more, if it had to be waited for).
        if (FindWindowW("Shell_TrayWnd", null) == IntPtr.Zero)
        {
            if (++_shellWaits <= 60)
            {
                _ = Task.Run(async () => { await Task.Delay(1000); _post(Start); });
                return;
            }
            Log.Write("Dock: the taskbar never appeared; starting the dock anyway.");
        }
        else if (_shellWaits > 0 && _shellWaits < 1000)
        {
            _shellWaits = 1000; // once: let Explorer finish what it does just after the taskbar appears
            _ = Task.Run(async () => { await Task.Delay(2000); _post(Start); });
            return;
        }
        Startup.RunUnelevated(Exe); // as the signed-in user: never an administrator dock
        // Explorer starts it, so there's no handle to it here: find it once it's up.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                if (Running() is { } started)
                {
                    _post(() => Watch(started));
                    return;
                }
            }
            Log.Write("Dock: started, but never appeared.");
        });
    }

    /// <summary>Seconds spent waiting for Explorer's taskbar before starting the dock (1000: done waiting).</summary>

    private int _shellWaits;


    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]

    private static extern IntPtr FindWindowW(string? className, string? title);


    private void Watch(Process process)
    {
        _process?.Dispose();
        _process = process;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _post(() => Exited(process));
        }
        catch (Exception ex) { Log.Write($"Dock: can't watch it: {ex.Message}"); }
    }

    /// <summary>The dock went away. Started again if it's still wanted, unless it keeps falling over.</summary>
    private void Exited(Process process)
    {
        if (!ReferenceEquals(process, _process)) return;
        _process = null;
        Overlay.DockTargets.Clear(); // no dock to send windows into until it's back and says where its icons are
        if (!_wanted) return;
        var now = Environment.TickCount64;
        _restarts.Enqueue(now);
        while (_restarts.Count > 0 && now - _restarts.Peek() > 60_000) _restarts.Dequeue();
        if (_restarts.Count > 3)
        {
            Log.Write("Dock: stopped four times in a minute; leaving it off.");
            _notify("The dock keeps stopping", "It's been left off for now. Turn it on again from TrackSwipe's menu.");
            _wanted = false;
            Changed?.Invoke(false);
            return;
        }
        // A restart from the dock's own menu starts its replacement itself; give it a moment to appear first.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            _post(Start);
        });
    }

    private void Stop()
    {
        var process = _process ?? Running();
        _process = null;
        if (process is null) return;
        Send(QuitCommand, process);
        try
        {
            if (!process.WaitForExit(3000)) process.Kill();
        }
        catch { }
        process.Dispose();
    }

    private static void StopRunningCopies()
    {
        while (Running() is { } process)
        {
            Send(QuitCommand, process);
            try { if (!process.WaitForExit(3000)) process.Kill(); } catch { }
            process.Dispose();
        }
    }

    private void Send(int command) => Send(command, _process ?? Running());

    /// <summary>Posts a command to the dock's message window (its class name carries its process id).</summary>
    private static void Send(int command, Process? process)
    {
        if (process is null) return;
        var window = FindWindowExW(IntPtr.Zero, IntPtr.Zero, $"Anchor.MessageWindow.{process.Id}", null);
        if (window != IntPtr.Zero) PostMessageW(window, CommandMessage, command, IntPtr.Zero);
    }

    /// <summary>The running dock, if any: the Anchor process whose exe is ours.</summary>
    private static Process? Running()
    {
        foreach (var process in Process.GetProcessesByName("Anchor"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase)) return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    private static string? Metadata(string key) =>
        typeof(DockHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value ? value : null;

    public void Dispose()
    {
        _wanted = false;
        Stop();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, nint wParam, IntPtr lParam);
}
