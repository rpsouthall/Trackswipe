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
/// </summary>
public static class StartMenuApps
{
    private static volatile Dictionary<string, string> _byTarget = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The AppUserModelID Start lists <paramref name="exePath"/> under, or null.</summary>
    public static string? AppIdFor(string exePath)
    {
        if (_byTarget.TryGetValue(exePath, out var id))
            return id;
        // An app installed since the last read: look again, but not on every right-click.
        if (Environment.TickCount64 - _lastRead > 60_000)
            Warm(TimeSpan.Zero);
        return null;
    }

    private static void Read()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1)
            return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var iid = ShellInterop.IID_IShellItem;
            if (ShellInterop.SHCreateItemFromParsingName("shell:AppsFolder", nint.Zero, ref iid, out var folder) != 0)
                return;
            var bhid = ShellInterop.BHID_EnumItems;
            var enumIid = ShellInterop.IID_IEnumShellItems;
            if (folder.BindToHandler(nint.Zero, ref bhid, ref enumIid, out var enumObject) != 0)
                return;
            var items = (ShellInterop.IEnumShellItems)enumObject;
            while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    // An AppsFolder item's own parsing name is its AppUserModelID.
                    var appId = ShellInterop.DisplayName(item, ShellInterop.SIGDN_PARENTRELATIVEPARSING);
                    if (string.IsNullOrEmpty(appId) || Target(item) is not { Length: > 0 } target)
                        continue;
                    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        map.TryAdd(Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)), appId);
                }
                catch
                {
                    // One odd entry doesn't spoil the list.
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }
        catch (Exception ex)
        {
            Diag.Log("StartMenuApps: " + ex.Message);
        }
        finally
        {
            if (map.Count > 0)
                _byTarget = map;
            _lastRead = Environment.TickCount64;
            Interlocked.Exchange(ref _reading, 0);
        }
    }

    private static string? Target(ShellInterop.IShellItem item)
    {
        var bhid = ShellInterop.BHID_PropertyStore;
        var iid = ShellInterop.IID_IPropertyStore;
        if (item.BindToHandler(nint.Zero, ref bhid, ref iid, out var storeObject) != 0)
            return null;
        var store = (NativeMethods.IPropertyStore)storeObject;
        try { return ShellInterop.StringProperty(store, ShellInterop.PKEY_Link_TargetParsingPath); }
        finally { Marshal.ReleaseComObject(store); }
    }
}
