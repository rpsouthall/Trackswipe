using System.Runtime.InteropServices;
using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// Which AppUserModelID each program is listed under in Start (the shell's AppsFolder) — the ID its
/// jump list is filed under. A dock icon often names an app by its exe (Edge as
/// <c>msedge.exe</c>, not <c>MSEdge</c>), and the exe alone says nothing about that ID.
/// <para>
/// Read once, a few seconds after startup, on a background thread of its own: the walk takes a
/// few hundred milliseconds of shell work and must never be what holds up the first right-click.
/// Then again only when asked about an exe it doesn't know, at most once a minute, for apps
/// installed since.
/// </para>
/// <para>
/// It is also what Start and Search call an app and show for it: a shortcut can dress an exe as
/// something else (Edge's <c>msedge.exe</c> listed as "Chromium", with an icon of its own), and the
/// dock follows Start, as the taskbar does.
/// </para>
/// </summary>
public static class StartMenuApps
{
    /// <summary>An app as Start lists it: its AppUserModelID (its <c>shell:AppsFolder</c> name) and what Start calls it.</summary>
    public sealed record Entry(string AppId, string Name);

    private static volatile Dictionary<string, Entry> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    private static long _lastRead = long.MinValue;
    private static int _reading;

    /// <summary>Reads the list in the background after <paramref name="delay"/>.</summary>
    public static void Warm(TimeSpan delay)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(delay);
            Read();
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Anchor Start menu index" };
        // The shell namespace expects a single-threaded apartment.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Raised, on the index's own thread, after a read that changed what Start lists: the first
    /// one, a few seconds after startup, and any later one that found an app installed or renamed.
    /// </summary>
    public static event Action? Changed;

    /// <summary>The AppUserModelID Start lists <paramref name="exePath"/> under, or null.</summary>
    public static string? AppIdFor(string exePath)
    {
        if (_byTarget.TryGetValue(exePath, out var entry))
            return entry.AppId;
        // An app installed since the last read: look again, but not on every right-click.
        if (Environment.TickCount64 - _lastRead > 60_000)
            Warm(TimeSpan.Zero);
        return null;
    }

    /// <summary>
    /// How Start lists <paramref name="exePath"/> (a full path), from what was last read, or null:
    /// not listed, or not read yet. Never starts a read, as it is asked on every refresh of the open apps.
    /// </summary>
    public static Entry? Find(string exePath) => _byTarget.GetValueOrDefault(exePath);

    private static void Read()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1)
            return;
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var iid = ShellInterop.IID_IShellItem;
            if (ShellInterop.SHCreateItemFromParsingName("shell:AppsFolder", nint.Zero, ref iid, out var folderPointer) != 0 ||
                ComInterop.Take<ShellInterop.IShellItem>(folderPointer) is not { } folder)
                return;
            var bhid = ShellInterop.BHID_EnumItems;
            var enumIid = ShellInterop.IID_IEnumShellItems;
            if (folder.BindToHandler(nint.Zero, ref bhid, ref enumIid, out var enumPointer) != 0 ||
                ComInterop.Take<ShellInterop.IEnumShellItems>(enumPointer) is not { } items)
            {
                ComInterop.Release(folder);
                return;
            }
            while (items.Next(1, out var itemPointer, out uint fetched) == 0 && fetched == 1)
            {
                if (ComInterop.Take<ShellInterop.IShellItem>(itemPointer) is not { } item)
                    continue;
                try
                {
                    // An AppsFolder item's own parsing name is its AppUserModelID.
                    var appId = ShellInterop.DisplayName(item, ShellInterop.SIGDN_PARENTRELATIVEPARSING);
                    if (string.IsNullOrEmpty(appId) || Target(item) is not { Length: > 0 } target)
                        continue;
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = ShellInterop.DisplayName(item, ShellInterop.SIGDN_NORMALDISPLAY);
                    var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target));
                    // Several entries can start one exe: a browser's profiles each get a shortcut,
                    // their IDs the browser's own with the profile added ("Chrome.UserData.Profile2"),
                    // so the shortest is the app itself.
                    if (!map.TryGetValue(path, out var known) || appId.Length < known.AppId.Length)
                        map[path] = new Entry(appId, string.IsNullOrWhiteSpace(name) ? appId : name.Trim());
                }
                catch
                {
                    // One odd entry doesn't spoil the list.
                }
                finally
                {
                    ComInterop.Release(item);
                }
            }
            ComInterop.Release(items);
            ComInterop.Release(folder);
        }
        catch (Exception ex)
        {
            Diag.Log("StartMenuApps: " + ex.Message);
        }
        finally
        {
            bool changed = map.Count > 0 && !SameAs(_byTarget, map);
            if (changed)
                _byTarget = map;
            _lastRead = Environment.TickCount64;
            Interlocked.Exchange(ref _reading, 0);
            if (changed)
            {
                try { Changed?.Invoke(); }
                catch (Exception ex) { Diag.Log("StartMenuApps: change handler failed: " + ex.Message); }
            }
        }
    }

    private static bool SameAs(Dictionary<string, Entry> old, Dictionary<string, Entry> fresh)
    {
        if (old.Count != fresh.Count)
            return false;
        foreach (var (path, entry) in fresh)
            if (!old.TryGetValue(path, out var was) || was != entry)
                return false;
        return true;
    }

    private static string? Target(ShellInterop.IShellItem item)
    {
        var bhid = ShellInterop.BHID_PropertyStore;
        var iid = ShellInterop.IID_IPropertyStore;
        if (item.BindToHandler(nint.Zero, ref bhid, ref iid, out var storePointer) != 0 ||
            ComInterop.Take<NativeMethods.IPropertyStore>(storePointer) is not { } store)
            return null;
        try { return ShellInterop.StringProperty(store, ShellInterop.PKEY_Link_TargetParsingPath); }
        finally { ComInterop.Release(store); }
    }
}
