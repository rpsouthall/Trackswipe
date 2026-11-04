using System.Runtime.InteropServices;
using System.Text;

namespace Anchor.Services;

/// <summary>
/// Resolves a Windows shortcut (<c>.lnk</c>) to the executable it points at.
/// <para>
/// Needed by the running-app indicators: most pinned "apps" are shortcuts, and a shortcut's own
/// path never matches the image path of a running process, so a dock full of .lnk items would
/// otherwise never light up. Launching doesn't need this — <c>ShellExecute</c> follows a shortcut
/// itself (see <see cref="Launcher"/>) — only the "is it already running?" comparison does.
/// </para>
/// <para>
/// There is no managed API for this, so it goes through the shell's own <c>IShellLinkW</c>, the
/// same component Explorer uses. Results are cached against the shortcut's last-write time, since
/// resolving means a COM activation plus a file read and the answer only changes if the .lnk is
/// edited.
/// </para>
/// </summary>
public static class ShortcutResolver
{
    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry(DateTime Stamp, string? Target);

    // Keyed and locked the same as Cache: a lookup for a path that is already being resolved on a
    // thread-pool thread is joined to that same task rather than starting a second one. Without
    // this, a share that stays unresponsive would pick up one more stuck thread-pool thread every
    // poll (see RunningAppMonitor, which calls Resolve every couple of seconds) for as long as it
    // stays down.
    private static readonly Dictionary<string, Task<string?>> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long <see cref="Resolve"/> waits on the calling thread before giving up on a lookup
    /// that hasn't come back yet. <see cref="Resolve"/> runs on the UI thread on every running-app
    /// poll (<c>RunningAppMonitor.IsRunning</c>/<c>TryFocus</c>), for every <c>.lnk</c> item on
    /// every dock, every couple of seconds — a shortcut whose target volume (a network share, a
    /// mounted image) has gone unresponsive must not be able to turn <see cref="File.Exists"/> or
    /// the COM read below into the OS's own SMB/mount timeout (tens of seconds, sometimes much
    /// longer) on the thread that draws the dock. The lookup itself is not abandoned when this
    /// expires — it keeps running on its thread-pool thread and populates the cache whenever it
    /// does finish — only this call gives up early and reports "not resolved for now".
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The executable a shortcut points at, or null if <paramref name="path"/> isn't a resolvable
    /// shortcut — including one whose lookup didn't finish within <see cref="LookupTimeout"/>, in
    /// which case the caller simply sees "not resolved" for this call and the next poll tries
    /// again. Never throws, never blocks past that timeout.
    /// </summary>
    public static string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;

        Task<string?> lookup;
        lock (Cache)
        {
            if (!InFlight.TryGetValue(path, out lookup!))
            {
                lookup = Task.Run(() => ResolveCore(path));
                InFlight[path] = lookup;
                // Runs whenever the lookup finishes, however long that takes, so a slow path
                // doesn't stay "in flight" forever and queue every later call behind the same
                // stale task.
                lookup.ContinueWith(_ =>
                {
                    lock (Cache)
                    {
                        if (InFlight.TryGetValue(path, out var current) && current == lookup)
                            InFlight.Remove(path);
                    }
                }, TaskScheduler.Default);
            }
        }

        try
        {
            return lookup.Wait(LookupTimeout) ? lookup.Result : null;
        }
        catch
        {
            // ResolveCore never throws on its own — everything inside it is already guarded — but
            // Task.Result rethrows a faulted task's exception, and this is never worth surfacing.
            return null;
        }
    }

    private static string? ResolveCore(string path)
    {
        if (!File.Exists(path))
            return null;

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            stamp = DateTime.MinValue;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Target;
        }

        var target = ReadTarget(path);

        lock (Cache)
        {
            Cache[path] = new CacheEntry(stamp, target);
        }
        return target;
    }

    private static string? ReadTarget(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(path, 0 /* STGM_READ */);

            var buffer = new StringBuilder(1024);
            // SLGP_RAWPATH: the path exactly as stored, with no attempt to "fix" a target that
            // has moved. A repaired path would point somewhere the user never pinned.
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, nint.Zero, SLGP_RAWPATH);

            var target = Environment.ExpandEnvironmentVariables(buffer.ToString()).Trim();
            return target.Length == 0 ? null : target;
        }
        catch (Exception ex)
        {
            // A shortcut to a Store app or a control-panel item has no file-system target at all,
            // so this is an ordinary outcome, not an error: the item simply never reports running.
            Diag.Log($"ShortcutResolver: '{path}' didn't resolve ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
                Marshal.ReleaseComObject(link);
        }
    }

    private const uint SLGP_RAWPATH = 0x4;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // Only the vtable slots actually called are declared, and they must stay in their real
    // interface order: COM dispatches by slot index, so an extra or reordered member here would
    // silently call the wrong function. GetPath is IShellLinkW's first method.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int fileLength,
            nint findData,
            uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        // Inherited from IPersist, and ahead of IPersistFile's own members in the vtable.
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    }
}
